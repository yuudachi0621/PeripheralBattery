using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PeripheralBattery.Models;
using PeripheralBattery.Services;

namespace PeripheralBattery.Diagnostics;

/// <summary>
/// 逻辑自检（命令行 --selftest）。
///
/// 这类"该不该弹气泡""这一行到底对齐没对齐"的判断没法靠看界面验证，只能跑断言。
/// 覆盖的组：
///   1~6   低电量提醒（只提醒一次、冷却、充电后恢复、多台只报一台、总开关、无电量不算低）
///   7~8   悬停提示的基本形态（一台一行、不超 63 字符、名字完整）
///   9     主设备（托盘跟主设备、离线不退回最低值、不影响悬停列表）
///   10~11 从面板移除设备（不会被下轮轮询加回来、老配置默认已跟踪）
///   12    扫描结果的副标题（不出现"2.4G · 2.4G"这种重复）
///   13*   悬停提示的排版（全部列出、电量前置、像素级对齐、竖线分栏、20 字符截断）
///   14    设备多到放不下时的降级（仍不超 63 字符、要么全列要么说清还有几台）
///   15    充电中（显示真实电量 + 充电标记、不触发低电量、标记必须是 ASCII）
///   16    读不到电量的显示（无读数→统一"未知"图标；充电中→绿字+闪电）
///   17    USB 有线兜底（切有线后按名字找回、连接方式改"有线"、拔线还原、纯蓝牙不误判）
///
/// 只用断言、不碰真实配置：ConfigStore.ReadOnly 由 App 在解析到 --selftest 时打开。
/// </summary>
internal static class SelfTest
{
    public static void Run()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var log = new StringBuilder();
        int failed = 0;

        void Check(string name, bool ok)
        {
            log.AppendLine($"{(ok ? "[通过]" : "[失败]")} {name}");
            if (!ok) failed++;
        }

        // ---- 1. 电量 10%，阈值 20% → 应该提醒 1 次 ----
        var watcher = new LowBatteryWatcher();
        var device = Make("a", 10);
        var first = watcher.Evaluate(new[] { device }, 20, true);
        Check("低电量首次应提醒", first.Count == 1);

        // ---- 2. 同一台设备再评估 5 次 → 冷却期内不该重复提醒 ----
        var repeat = 0;
        for (int i = 0; i < 5; i++) repeat += watcher.Evaluate(new[] { device }, 20, true).Count;
        Check("冷却期内不重复提醒", repeat == 0);

        // ---- 3. 充上电后再放电 → 允许重新提醒 ----
        device.Charging = true;
        device.Percent = 90;
        watcher.Evaluate(new[] { device }, 20, true);   // 这一轮清掉记忆
        device.Charging = false;
        device.Percent = 8;
        Check("充电后能再次提醒", watcher.Evaluate(new[] { device }, 20, true).Count == 1);

        // ---- 4. 三台同时低电量 → 只报电量最低的一台 ----
        var w2 = new LowBatteryWatcher();
        var many = new[] { Make("x", 18), Make("y", 5), Make("z", 12) };
        var picked = w2.Evaluate(many, 20, true);
        Check("多台低电量只报一台", picked.Count == 1 && picked[0].Percent == 5);

        // ---- 5. 总开关关掉 → 一律不提醒 ----
        var w3 = new LowBatteryWatcher();
        Check("关闭提醒后不弹", w3.Evaluate(new[] { Make("k", 3) }, 20, false).Count == 0);

        // ---- 6. 电量未知（Note 说明）不该被当低电量 ----
        var unknown = new DeviceState("u", "t") { DefaultName = "K600", Percent = null };
        Check("无电量设备不算低电量", new LowBatteryWatcher().Evaluate(new[] { unknown }, 20, true).Count == 0);

        // ---- 7. 托盘提示一台一行、且不超过 63 字符 ----
        var monitor = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        monitor.Online.Clear();
        monitor.Online.Add(Make("m1", 30));
        monitor.Online.Add(Make("m2", 80));
        var tip = monitor.BuildTooltip();
        Check("悬停提示一设备一行", tip.Contains('\n'));
        Check("悬停提示不超 63 字符", tip.Length <= 63);

        // ---- 8. 悬停提示里设备名必须完整（不超过 20 字符上限时） ----
        // 注意：这些断言用的是"从未刷新过"的 monitor，所以必须走 SeedForTest 灌数据；
        // 只往 Online 里 Add 是不够的 —— 那是界面集合，逻辑走的是内部的 _all。
        var m8 = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        m8.SeedForTest(new DeviceState("m8", "test")
        {
            // 这条正好 20 字符，处于上限边缘，必须原样完整显示
            DefaultName = "PRO X SUPERLIGHT 20", Percent = 41, Online = true, LowThreshold = 20,
        });
        var tip8 = m8.BuildTooltip();
        Check("悬停提示显示完整设备名", tip8.Contains("PRO X SUPERLIGHT 20"));
        Check("完整名称仍不超 63 字符", tip8.Length <= 63);

        // ---- 9. 托盘显示主设备电量，而不是最低值 ----
        var cfg9 = new AppConfig();
        var m9 = new BatteryMonitor(cfg9, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var mouse9 = new DeviceState("k:mouse", "test") { DefaultName = "鼠标", Percent = 20, Online = true };
        var pad9 = new DeviceState("k:pad", "test") { DefaultName = "手柄", Percent = 90, Online = true };
        m9.SeedForTest(mouse9, pad9);
        Check("未指定主设备时取最低值", m9.TrayPercent() == 20 && m9.Primary == null);

        m9.SetPrimary(pad9);
        Check("指定主设备后托盘跟随主设备", m9.TrayPercent() == 90);
        Check("主设备标记正确", pad9.IsPrimary && !mouse9.IsPrimary);

        // 主设备只改"托盘图标显示哪个数"，悬停提示仍然要列出**所有**在线设备。
        // 否则用户设了主设备后鼠标一悬停就只剩一台，根本看不到其他外设还有多少电。
        var tip9 = m9.BuildTooltip();
        Check("设了主设备后悬停提示仍列出所有设备",
            tip9.Contains("鼠标") && tip9.Contains("手柄"));
        Check("设了主设备后悬停提示仍是两台两行", tip9.Split('\n').Length == 2);

        pad9.Online = false;
        Check("主设备离线时不退回最低值", m9.TrayPercent() == null);

        // 主设备离线后，提示里就只剩在线的那台
        Check("主设备离线后提示里不再有它", !m9.BuildTooltip().Contains("手柄"));

        m9.SetPrimary(null);
        Check("取消主设备后恢复最低值", m9.TrayPercent() == 20 && !pad9.IsPrimary);

        // ---- 10. 移除设备后不会被下一轮轮询悄悄加回来 ----
        var cfg10 = new AppConfig();
        cfg10.Devices["k:del"] = new DeviceRecord { DefaultName = "待删", Tracked = true };
        var m10 = new BatteryMonitor(cfg10, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var del10 = new DeviceState("k:del", "test") { DefaultName = "待删", Percent = 50, Online = true };
        m10.SeedForTest(del10);
        m10.SetPrimary(del10);
        m10.Remove(del10);

        Check("移除后不在界面列表里", !m10.Online.Contains(del10) && !m10.Offline.Contains(del10));
        Check("移除后不再是主设备", m10.Primary == null);
        Check("移除的设备标记为不跟踪", cfg10.Devices["k:del"].Tracked == false);
        Check("移除主设备后托盘不指向它", m10.TrayPercent() == null);

        // 模拟下一轮轮询：即使通道又读到了它，也不能重新出现在面板里
        Check("移除后轮询不会加回来", !m10.IsTrackedByUser("k:del"));

        // ---- 11. 没跟踪过的老配置不能被误判成"已移除" ----
        var cfg11 = new AppConfig();
        cfg11.Devices["k:old"] = new DeviceRecord { DefaultName = "老设备" };   // 没有 Tracked 字段
        var m11 = new BatteryMonitor(cfg11, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        Check("老配置默认视为已跟踪", m11.IsTrackedByUser("k:old"));

        // ---- 12. 扫描结果的副标题不该出现"2.4G · 2.4G"这种重复 ----
        var item12 = new ScanItem
        {
            Id = "s1", Name = "PRO X SUPERLIGHT 2 SE", Provider = "罗技 2.4G", Link = LinkKind.Receiver24G,
        };
        Check("副标题不重复连接方式", item12.SubText == "罗技 2.4G");

        var item12b = new ScanItem
        {
            Id = "s2", Name = "OPPO Enco Free4", Provider = "经典蓝牙", Link = LinkKind.Bluetooth,
            Note = "左右耳取最低",
        };
        Check("副标题保留不重复的说明", item12b.SubText == "经典蓝牙 · 左右耳取最低");

        Check("已添加状态文案正确", item12.StatusText == "未添加"
            && new ScanItem { Id = "s3", Name = "x", Provider = "p", AlreadyAdded = true }.StatusText == "已添加");

        // ---- 13. 悬停提示要列出非折叠区的**全部**设备，不能只列前几台 ----
        var m13 = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        m13.SeedForTest(
            new DeviceState("n1", "test") { DefaultName = "GPW 2", Percent = 41, Online = true },
            new DeviceState("n2", "test") { DefaultName = "K600", Percent = 55, Online = true },
            new DeviceState("n3", "test") { DefaultName = "Enco Free4", Percent = 30, Online = true },
            new DeviceState("n4", "test") { DefaultName = "Xbox 手柄", Percent = 73, Online = true },
            new DeviceState("n5", "test") { DefaultName = "备用鼠标", Percent = 88, Online = true },
            new DeviceState("n6", "test") { DefaultName = "旧耳机", Percent = 90, Online = true });

        var tip13 = m13.BuildTooltip();
        var lines13 = tip13.Split('\n');
        Check("悬停提示列出全部 6 台设备", lines13.Length == 6);
        Check("悬停提示仍不超 63 字符", tip13.Length <= 63);
        Check("6 台设备每行都是电量前置", lines13.All(l => ValueStartsAt(l, 0)));
        // 6 台是最能考验字符预算的场景：标准格式塞不下，
        // 兜底会依次压缩电量栏宽度和分隔符，但**绝不靠截断设备名**来省字符 ——
        // 名字被截成 "Xbox …" 的话，用户根本认不出是哪台设备。
        Check("6 台设备仍全部列出", lines13.Length == 6);
        Check("6 台设备不靠截断名字省字符", !tip13.Contains('…'));
        Check("6 台设备保留可分辨的分隔符", lines13.All(l => l.Contains('|')));

        // 离线的设备不该出现在提示里（它们在折叠区）
        var m13b = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        m13b.SeedForTest(
            new DeviceState("on", "test") { DefaultName = "在线设备", Percent = 50, Online = true },
            new DeviceState("off", "test") { DefaultName = "离线设备", Percent = 20, Online = false });
        Check("悬停提示不含离线设备", !m13b.BuildTooltip().Contains("离线设备"));

        // ---- 13c. 电量对齐：每行电量栏的右边缘落在同一像素位置 ----
        // 电量前置 + 像素级补位之后，"对齐"这件事可以直接量出来：
        // 每行从行首到 "%" 的像素宽度应当完全一致（容忍 1px 的取整误差）。
        var pctEnds13 = lines13.Select(PercentEndPx).Distinct().ToList();
        Check("6 台设备电量右边缘对齐", pctEnds13.Count == 1);

        // 像素测量本身要合理：中文一定比同字符数的英文宽
        Check("中文比同长度英文宽", TextMetrics.Width("手柄") > TextMetrics.Width("ab"));
        Check("空格比数字窄", TextMetrics.Width(" ") < TextMetrics.Width("0"));
        Check("长名字像素更宽", TextMetrics.Width("OPPO Enco Free4") > TextMetrics.Width("GPW 2"));

        // 全英文名（真实设备就是这种）也要对齐
        var m13c = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        m13c.SeedForTest(
            new DeviceState("e1", "test") { DefaultName = "GPW 2", Percent = 41, Online = true },
            new DeviceState("e2", "test") { DefaultName = "Machenike K600", Percent = 55, Online = true },
            new DeviceState("e3", "test") { DefaultName = "OPPO Enco Free4", Percent = 30, Online = true });
        var tip13c = m13c.BuildTooltip();
        var lines13c = tip13c.Split('\n');
        Check("全英文设备名也对齐", lines13c.Select(PercentEndPx).Distinct().Count() == 1
            && lines13c.Length == 3);

        // ---- 13d. 真实场景（2 台设备）：名字完整、带 %、电量完全对齐 ----
        // 这是用户实际会看到的形态 —— 本机只有鼠标 + 耳机在线。
        var m13d = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        m13d.SeedForTest(
            new DeviceState("r1", "test") { DefaultName = "GPW 2", Percent = 41, Online = true },
            new DeviceState("r2", "test") { DefaultName = "OPPO Enco Free4", Percent = 30, Online = true });
        var tip13d = m13d.BuildTooltip();
        var lines13d = tip13d.Split('\n');
        Check("真实两台设备名字完整不截断", !tip13d.Contains('…'));
        Check("真实两台设备保留百分号", tip13d.Contains("41%") && tip13d.Contains("30%"));
        Check("真实两台设备电量右边缘完全对齐",
            lines13d.Select(PercentEndPx).Distinct().Count() == 1 && lines13d.Length == 2);

        // 复现用户截图那个 case：百分比必须对齐（截图里差了 25px）
        Check("两行百分比结束位置相同(截图case)",
            Math.Abs(PercentEndPx(lines13d[0]) - PercentEndPx(lines13d[1])) <= 1);

        // ---- 13h. 电量与设备名之间要有可辨认的分隔符（用户反馈"难一眼分辨"） ----
        // 之前是 " 41%  GPW 2"，只有空格分隔，比例字体下数字和名字糊成一片。
        // 现在电量后面固定跟 " | "，每行都有，扫一眼就知道竖线右边才是名字。
        Check("悬停提示用竖线分隔电量与名字", lines13d.All(l => l.Contains(" | ")));
        Check("分隔符每行落在同一列",
            lines13d.Select(SeparatorPx).Where(x => x >= 0).Distinct().Count() == 1);
        Check("名字在分隔符之后",
            lines13d.All(l => l[(l.IndexOf('|') + 1)..].Trim().Length > 0));

        // 三台也很宽裕，同样不该截断
        var m13e = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        m13e.SeedForTest(
            new DeviceState("t1", "test") { DefaultName = "GPW 2", Percent = 41, Online = true },
            new DeviceState("t2", "test") { DefaultName = "Machenike K600", Percent = 55, Online = true },
            new DeviceState("t3", "test") { DefaultName = "OPPO Enco Free4", Percent = 30, Online = true });
        var tip13e = m13e.BuildTooltip();
        Check("三台设备名字完整不截断", !tip13e.Contains('…'));
        Check("三台设备保留百分号", tip13e.Contains('%'));
        Check("三台设备电量也对齐",
            tip13e.Split('\n').Select(PercentEndPx).Distinct().Count() == 1);

        // ---- 13f. 设备名超过 20 字符要用省略号截断 ----
        var longName = "PRO X SUPERLIGHT 2 SE WIRELESS GAMING MOUSE";   // 45 字符
        var m13f = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        m13f.SeedForTest(
            new DeviceState("L1", "test") { DefaultName = longName, Percent = 77, Online = true },
            new DeviceState("L2", "test") { DefaultName = "GPW 2", Percent = 41, Online = true });
        var tip13f = m13f.BuildTooltip();
        // 列表按电量升序排，77% 的超长名字那行在最后
        var longLine13f = tip13f.Split('\n').First(l => l.Contains("PRO X"));
        // 名字从竖线分隔符之后开始（现在格式是 " 77% | 名字"）
        var bar13f = longLine13f.IndexOf('|');
        var namePart13f = longLine13f[(bar13f + 1)..].Trim();
        Check("超长设备名截断到 20 字符", namePart13f.Length == 20);
        Check("超长设备名以省略号结尾", namePart13f.EndsWith('…'));
        Check("截断后仍能看出是哪台设备", namePart13f.StartsWith("PRO X SUPER"));
        Check("截断后电量仍对齐",
            tip13f.Split('\n').Select(PercentEndPx).Distinct().Count() == 1);
        Check("截断后名字起点仍一致",
            tip13f.Split('\n').Select(NameStartPx).Where(x => x >= 0).Distinct().Count() == 1);

        // 正好 20 字符的不该被截
        var m13g = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        m13g.SeedForTest(new DeviceState("E1", "test")
        {
            DefaultName = "PRO X SUPERLIGHT 20", Percent = 41, Online = true,   // 正好 20 字符
        });
        Check("正好 20 字符不被截断", !m13g.BuildTooltip().Contains('…'));

        // ---- 13i. 100% 与低位电量混排时，名字起点仍然一致 ----
        // 这是最容易破的地方，因为比例字体里数字之间也不等宽：
        //   实测 "  5"=13.04px、" 30"=16.22px、"100"=19.41px。
        // 所以光靠 PadLeft 补齐字符数是不够的，必须再由 TextMetrics 按像素补空格收尾。
        // 这组断言同时钉住两层：电量右边缘一致 + 名字起点一致。
        var m13i = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        m13i.SeedForTest(
            new DeviceState("h1", "test") { DefaultName = "GPW 2", Percent = 100, Online = true },
            new DeviceState("h2", "test") { DefaultName = "K600", Percent = 30, Online = true },
            new DeviceState("h3", "test") { DefaultName = "Enco Free4", Percent = 5, Online = true });
        var lines13i = m13i.BuildTooltip().Split('\n');
        Check("100% 与低位电量混排时名字起点一致",
            lines13i.Select(NameStartPx).Where(x => x >= 0).Distinct().Count() == 1);
        Check("100% 与低位电量混排时电量右边缘一致",
            lines13i.Select(PercentEndPx).Distinct().Count() == 1);
        Check("100% 混排仍不超 63 字符", m13i.BuildTooltip().Length <= 63);

        // 补字符数不等于补像素 —— 这个断言把上面那条结论钉死，防止以后有人"优化"回去
        Check("按字符补位对齐不了像素(反证)",
            Math.Abs(TextMetrics.Width(" 5%") - TextMetrics.Width("100%")) > 5);

        // ---- 15. 充电中：显示真实电量 + 充电标记 ----
        // 需求变更：以前插着线一律显示"充电中 100%"，但实测鼠标插线时 HID++ 仍回真实读数
        // （79% 且还在往上走），强制显示 100% 反而像是在骗人。
        // 现在改成「真实电量 + 小图标」，充电状态靠图标表达。
        var m15 = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var usb15 = new DeviceState("usb", "test")
        {
            DefaultName = "GPW 2", Percent = 12, Online = true, LowThreshold = 20,
        };
        var bt15 = new DeviceState("bt", "test")
        {
            DefaultName = "OPPO Enco Free4", Percent = 30, Online = true, LowThreshold = 20,
        };
        m15.SeedForTest(usb15, bt15);
        Check("未接 USB 时显示真实电量", usb15.PercentText == "12%");
        Check("未充电时不显示充电图标", !usb15.ShowChargeIcon);

        usb15.ExternalPower = true;
        Check("USB 供电时电量仍显示真实读数", usb15.PercentText == "12%");
        Check("USB 供电时对外电量就是真实值", usb15.EffectivePercent == 12);
        Check("USB 供电时显示充电图标", usb15.ShowChargeIcon);
        Check("USB 供电不触发低电量", !usb15.IsLow);
        // 颜色只反映电量，不反映是否在充电：12% 就是低电量红
        Check("USB 供电时数字颜色仍按真实电量分档", usb15.LevelBrush == Palettes.LevelLow);
        // 但闪电图标固定暖绿，不跟着数字变红（红闪电会被读成"充电故障"）
        Check("充电图标固定用暖绿色", usb15.ChargeBrush == Palettes.LevelHigh);
        Check("USB 供电时副标题说明是 USB 供电", usb15.SubText == "USB 供电");

        // 电池真在充的时候，副标题把"充电中"也说清楚（图标不解释文字，总有用户看不懂）
        usb15.Charging = true;
        Check("充电中的副标题含 USB 供电", usb15.SubText.Contains("USB 供电"));
        Check("充电中的副标题明确写出充电中", usb15.SubText.Contains("充电中"));

        // 悬停提示里用 1 个字符的 ASCII "+" 标充电，并且**数字是真实值**。
        // 注意不要断言 "+12%" 这种紧贴形式 —— Value() 会把数字补齐到 3 位（"+ 12%"），
        // 断言写得过紧就会绑死在内部补位细节上。
        var tip15 = m15.BuildTooltip();
        Check("悬停提示里用 + 标注充电", tip15.Contains(DeviceState.ExternalPowerBadge));
        Check("悬停提示保留真实电量而非 100%", tip15.Contains("12%") && !tip15.Contains("100%"));
        Check("悬停提示里仍有其它设备的电量", tip15.Contains("30%"));

        // 标记必须是系统提示框字体真的画得出来的字符。
        // 踩过的坑：最初用 "⚡"(U+26A1)，Segoe UI 没有它的字形，
        // 提示框里直接显示成一个空心方框，比没标还糟。所以现在钉死"必须是 ASCII"。
        Check("外接电标记是 ASCII 字符(提示框字体保证有字形)",
            DeviceState.ExternalPowerBadge.All(c => c < 128));
        Check("悬停提示里没有豆腐块字符",
            !tip15.Contains('⚡') && tip15.All(c => c < 128 || char.IsLetter(c) || c == '…'));

        // 托盘图标：显示真实电量，但充电中不标红
        Check("充电中的设备不被判成低电量告警", !m15.TrayIsLow());
        Check("USB 供电设备按真实电量参与托盘取值", m15.TrayPercent() == 12);

        // 低电量提醒必须跳过它 —— 插着线还弹"电量低"是最糟糕的体验
        var w15 = new LowBatteryWatcher();
        var alert15 = w15.Evaluate(new[] { usb15, bt15 }, 20, true);
        Check("低电量提醒跳过充电中的设备",
            alert15.All(d => d.Id != "usb"));

        // 拔掉线：图标消失，低电量判断恢复
        usb15.ExternalPower = false;
        usb15.Charging = false;
        Check("拔线后不再显示充电图标", !usb15.ShowChargeIcon);
        Check("拔线后恢复低电量判定", usb15.PercentText == "12%" && usb15.IsLow);
        Check("拔线后低电量提醒恢复",
            new LowBatteryWatcher().Evaluate(new[] { usb15 }, 20, true).Count == 1);

        // 主设备在充电：托盘显示真实电量（不是 100），且不标红
        var m15b = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var usbMain = new DeviceState("usb-main", "test")
        {
            DefaultName = "GPW 2", Percent = 79, Online = true, LowThreshold = 20,
            ExternalPower = true, Charging = true,
        };
        m15b.SeedForTest(usbMain);
        m15b.SetPrimary(usbMain);
        Check("主设备充电时托盘显示真实电量", m15b.TrayPercent() == 79);
        Check("主设备充电时托盘不标红", !m15b.TrayIsLow());

        // ---- 16. 充着电但读不到电量的设备（有线模式的键盘）----
        // 这类设备只知道"在供电"，不知道百分比，界面必须说"充电中"而不是给个破折号。
        var kb16 = new DeviceState("kb", "test")
        {
            DefaultName = "Machenike K600", Percent = null, Online = true, LowThreshold = 20,
        };
        // 读不到电量又没充电：文字留空，由统一的"电量未知"图标表达
        Check("无电量且未充电时不显示文字", kb16.PercentText == "");
        Check("无电量且未充电时显示未知图标", kb16.ShowLevelUnknown);
        Check("无电量且未充电时图标说明来自 Note", kb16.LevelHint.Length > 0);

        kb16.ExternalPower = true;
        kb16.Charging = true;
        Check("充电但无电量时显示充电中", kb16.PercentText == "充电中");
        Check("充电但无电量时仍显示充电图标", kb16.ShowChargeIcon);
        Check("充电但无电量时不再显示未知图标", !kb16.ShowLevelUnknown);
        Check("充电但无电量时不进低电量告警", !kb16.IsLow);
        Check("充电但无电量时电量条不画量", kb16.EffectivePercent == null && !kb16.HasLevel);
        // 没有数字可显示时，灰色会把"充电中"这个唯一有效信息也压暗，所以给暖绿
        Check("充电但无电量时文字用暖绿而非灰", kb16.LevelBrush == Palettes.LevelHigh);

        // 提示框里没有数字可写，退化成"充电中"三个字
        var m16 = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        m16.SeedForTest(kb16);
        Check("提示框里写明充电中而不是破折号", m16.BuildTooltip().Contains("充电中"));

        // 扫描窗口里的那一行也要跟着改
        var scan15 = new ScanItem
        {
            Id = "s15", Name = "GPW 2", Provider = "罗技 2.4G", Link = LinkKind.Receiver24G,
            Level = 79, ExternalPower = true, Charging = true,
        };
        Check("扫描结果也显示真实电量", scan15.LevelText == "79%");
        Check("扫描结果副标题标注 USB 供电", scan15.SubText.Contains("USB 供电"));
        Check("扫描结果充电中显示充电图标", scan15.ShowChargeIcon);

        var scan16 = new ScanItem
        {
            Id = "s16", Name = "Machenike K600", Provider = "经典蓝牙", Link = LinkKind.Bluetooth,
            Level = null, ExternalPower = true, Charging = true,
        };
        Check("扫描结果无电量但充电时显示充电中", scan16.LevelText == "充电中");

        // ---- 17. USB 有线兜底：双模键盘切到有线后，无线通道读不到，按名字从 USB 找回 ----
        // 真实场景：K600 插线 → 自动断开蓝牙 → 经典蓝牙通道报 0 台 → 正常逻辑会把它判离线。
        // 兜底逻辑按设备名在 USB HID 里找到它，拉回在线并标记"USB 供电 · 充电中"。
        var m17 = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var kb17 = new DeviceState("bt:k600", "test")
        {
            DefaultName = "Machenike K600", Percent = null, Online = true, LowThreshold = 20,
            Link = LinkKind.Bluetooth,
        };
        m17.SeedForTest(kb17);
        var seen17 = new HashSet<string>();   // 空 = 无线通道这轮没读到它（蓝牙已断开）
        m17.ApplyWiredConnections(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Machenike K600",
        }, seen17);
        Check("有线模式下设备被找回并保持在线", kb17.Online);
        Check("有线模式连接方式改为有线", kb17.Link == LinkKind.Wired);
        Check("有线模式副标题显示有线", kb17.SubText == "有线");
        Check("有线模式显示充电中", kb17.PercentText == "充电中");
        Check("有线模式显示充电图标", kb17.ShowChargeIcon);
        Check("有线模式不触发低电量", !kb17.IsLow);
        Check("有线模式电量条不画量", !kb17.HasLevel);

        // 拔线 / 切回无线：USB 里不再有它 → 还原连接方式与充电标记
        m17.ApplyWiredConnections(new HashSet<string>(StringComparer.OrdinalIgnoreCase), seen17);
        Check("有线断开后充电标记被清掉", !kb17.ExternalPower && !kb17.Charging && !kb17.WiredMarked);
        Check("有线断开后连接方式还原为蓝牙", kb17.Link == LinkKind.Bluetooth);

        // ---- 17b. 回归：纯蓝牙、没插线时绝不能显示"充电中" ----
        // 曾经踩的坑：PnP 属性 BatteryChargingState 在没插线时也是 True，
        // 拿它推断"USB 供电"会让蓝牙模式一直显示充电中（用户实际反馈过）。
        // 现在有线/供电只认 USB 直连接口，这个用例把行为钉死。
        var m17d = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var pureBt = new DeviceState("bt:pure", "test")
        {
            DefaultName = "Machenike K600", Percent = null, Online = true, LowThreshold = 20,
            Link = LinkKind.Bluetooth,
        };
        m17d.SeedForTest(pureBt);
        // USB 里只有接收器/触摸板等无关设备，没有键盘本体
        m17d.ApplyWiredConnections(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ITE Device(8910)", "USB Receiver", "HIDI2C Device",
        }, new HashSet<string> { "bt:pure" });
        Check("纯蓝牙模式下不显示充电中", pureBt.PercentText == "" && !pureBt.ShowChargeIcon);
        Check("纯蓝牙模式下不标记 USB 供电", !pureBt.ExternalPower);
        Check("纯蓝牙模式下连接方式仍是蓝牙", pureBt.Link == LinkKind.Bluetooth);
        Check("纯蓝牙模式下副标题以蓝牙开头", pureBt.SubText.StartsWith("蓝牙"));
        Check("纯蓝牙模式下显示未知电量图标", pureBt.ShowLevelUnknown);

        // 用户改过名：DefaultName 匹配不上时用 CustomName 也要能匹配
        var kb17c = new DeviceState("bt:k600c", "test")
        {
            DefaultName = "Machenike K600", CustomName = "我的键盘", Percent = null, Online = true,
        };
        var m17c = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        m17c.SeedForTest(kb17c);
        m17c.ApplyWiredConnections(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "我的键盘",
        }, new HashSet<string>());
        Check("自定义名称也能匹配有线设备", kb17c.WiredMarked && kb17c.Online);

        // 无线通道仍能读到的设备：状态归通道管，有线逻辑不碰
        var kb17b = new DeviceState("bt:k600b", "test")
        {
            DefaultName = "Machenike K600", Percent = null, Online = true,
        };
        var m17b = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        m17b.SeedForTest(kb17b);
        m17b.ApplyWiredConnections(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Machenike K600",
        }, new HashSet<string> { "bt:k600b" });
        Check("通道已读到的设备不做有线标记", !kb17b.WiredMarked);

        // ---- 14. 设备多到放不下时，降级但绝不能超 63 字符 ----
        var m14 = new BatteryMonitor(new AppConfig(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var many14 = Enumerable.Range(1, 9)
            .Select(i => new DeviceState("b" + i, "test")
            {
                DefaultName = "PRO X SUPERLIGHT 2 SE 备用鼠标 " + i,
                Percent = 100 - i,
                Online = true,
            })
            .ToArray();
        m14.SeedForTest(many14);
        var tip14 = m14.BuildTooltip();
        Check("超长设备列表也不超 63 字符", tip14.Length <= 63);

        // 9 台超长名字（45 字符）无论怎么压缩都塞不进 63，
        // 此时必须"减少台数 + 明确告知还有几台"，不能截断成半句话。
        var listed14 = tip14.Split('\n').Length;
        var saysMore14 = tip14.Contains("…等");
        Check("超长设备列表要么全列、要么说清楚还有几台", listed14 == 9 || saysMore14);
        Check("超长设备列表不全列时至少给出 2 台", saysMore14 ? listed14 >= 2 : listed14 == 9);
        Check("超长设备列表不会把行截断成半个名字",
            tip14.Split('\n').All(l => !l.EndsWith("SUP") && l.Length < 63));

        log.AppendLine();
        log.AppendLine(failed == 0 ? "全部通过" : $"{failed} 项失败");

        var text = log.ToString();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pb_selftest.txt");
        System.IO.File.WriteAllText(path, text, Encoding.UTF8);
        Console.WriteLine(text);
        Console.WriteLine("[报告已写入] " + path);
    }

    private static DeviceState Make(string id, int percent) => new(id, "test")
    {
        DefaultName = "设备" + id,
        Percent = percent,
        Online = true,
        LowThreshold = 20,
    };

    /// <summary>
    /// 一行里百分比**结束**位置（像素），用来验证电量右对齐。
    ///
    /// 电量前置的格式是 ` 41%  GPW 2`，所以电量部分的结束位置 = 到 "%" 为止的像素宽。
    /// 全用同一个右对齐位宽，这个值在所有行应当完全一致。
    /// </summary>
    private static int PercentEndPx(string line)
    {
        var pct = line.IndexOf('%');
        if (pct < 0) return -1;
        return (int)Math.Round(TextMetrics.Width(line[..(pct + 1)]));
    }

    /// <summary>
    /// 一行里竖线分隔符的 x 坐标（像素）。用来验证"电量栏 | 名字"的分界点每行都在同一列。
    /// 没有分隔符返回 -1。
    /// </summary>
    private static int SeparatorPx(string line)
    {
        var bar = line.IndexOf('|');
        if (bar < 0) return -1;
        return (int)Math.Round(TextMetrics.Width(line[..bar]));
    }

    /// <summary>
    /// 一行里设备名**起始**位置的 x 坐标（像素）。
    ///
    /// 断言"名字起点一致"用这个比量分隔符更直接：用户看到的错位就是名字没对齐。
    /// 优先用竖线定位，干净版兜底用"首个非空白的非数字字符"。
    /// </summary>
    private static int NameStartPx(string line)
    {
        var bar = line.IndexOf('|');
        if (bar >= 0)
        {
            var rest = line[(bar + 1)..];
            if (rest.Length == 0) return -1;
            return (int)Math.Round(TextMetrics.Width(line[..(bar + 1)]) + TextMetrics.Width(" "));
        }
        return -1;
    }

    /// <summary>
    /// 判断这一行的电量是否从指定像素位置开始（电量前置时应当从 0 附近开始）。
    /// </summary>
    private static bool ValueStartsAt(string line, int x)
    {
        var digitsStart = 0;
        while (digitsStart < line.Length && (line[digitsStart] == ' ' || char.IsDigit(line[digitsStart])))
            digitsStart++;
        // 前导空格 + 数字之后应当是 %
        if (digitsStart >= line.Length || line[digitsStart] != '%') return false;
        return (int)Math.Round(TextMetrics.Width(line[..digitsStart].TrimStart())) >= x;
    }
}
