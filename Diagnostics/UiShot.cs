using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PeripheralBattery.Models;
using PeripheralBattery.Services;

namespace PeripheralBattery.Diagnostics;

/// <summary>
/// 开发用：把界面离屏渲染成 PNG（<c>--ui-shot 路径 [demo|settings|scan]</c>）。
/// 不依赖真实开窗，改完版式可以直接看图核对，也能在没有图形交互的环境里验证。
///
/// 三个模式的区别：
///   · 不传 → 用真实配置跑一轮轮询，渲染主面板（看到的是这台机器此刻的真相）；
///   · demo  → 在主面板里塞一批假设备，覆盖低电量/未知/充电/USB 供电/离线等所有视觉状态；
///   · settings / scan → 渲染设置窗口、"扫描其他设备"窗口（scan 会真的跑一遍通道）。
///
/// 顺手还会导出一张托盘图标对比图和一份布局溢出报告（写进 app.log），见 <see cref="RunAsync"/>。
/// </summary>
internal static class UiShot
{
    private static string _lastTooltip = "(未生成)";

    public static async Task RunAsync(string outputPath, string mode)
    {
        await RenderAsync(outputPath, mode).ConfigureAwait(true);

        // 顺手导出一张托盘图标对比图，并把悬停提示的文字打出来核对（会有 63 字符上限）
        DumpTrayIcons(Path.Combine(Path.GetDirectoryName(outputPath)!, "ui_tray.png"));
        Log.Write($"托盘悬停提示（{mode}）:\n{_lastTooltip}");
    }

    private static async Task RenderAsync(string outputPath, string mode)
    {
        if (mode == "settings")
        {
            var cfg = ConfigStore.Load();
            var mon = new BatteryMonitor(cfg, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            var window = new UI.SettingsWindow(cfg, mon);
            RenderInWindow(window, 408, outputPath);
            return;
        }

        // 扫描窗口：会真的跑一遍四条通道，所以顺带验证"扫描能不能扫到设备"
        if (mode == "scan")
        {
            var cfg = ConfigStore.Load();
            var mon = new BatteryMonitor(cfg, System.Windows.Threading.Dispatcher.CurrentDispatcher);
            var window = new UI.ScanWindow(mon);
            await window.RunScanForTestAsync();
            RenderInWindow(window, 452, outputPath, fixedHeight: 480);
            return;
        }

        var config = ConfigStore.Load();
        var monitor = new BatteryMonitor(config, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        await monitor.RefreshAsync(manual: true);

        if (mode == "demo") InjectDemo(monitor);

        _lastTooltip = monitor.BuildTooltip();

        var popup = new UI.PopupWindow(monitor, () => { });
        RenderInWindow(popup, 380, outputPath);
    }

    /// <summary>
    /// 在窗口自己的骨架上离屏渲染：不动 Content，而是把窗口的 Content 包一层固定宽度、
    /// 直接 Measure/Arrange 后画进 PNG。
    ///
    /// 为什么这么做：StaticResource（DataTemplate、隐式 Style）在 XAML 加载时就绑到了窗口的资源作用域，
    /// 一旦把 Content 摘下来挂到别处，这些引用就断了 —— 渲染出来会缺样式，跟真实界面对不上。
    /// 保持窗口为父级，作用域就完整。
    /// </summary>
    /// <param name="fixedHeight">窗口高度固定时（比如扫描窗口靠 * 行撑满）传进来，否则按内容高度。</param>
    private static void RenderInWindow(Window window, double width, string outputPath, double? fixedHeight = null)
    {
        var content = (FrameworkElement)window.Content;

        // 一句话说清这个坐标系（踩过好几次坑）：
        //   窗口宽度 = 左边框 + 根元素.Margin.Left + 内容宽度 + 根元素.Margin.Right + 右边框
        // 根元素自己的 Margin 是**留白**，不能动它，否则设置窗口那 22px 的左右边距就没了。
        // 所以这里只设内容宽度，让 Margin 继续生效；画布宽度取整个窗口宽度。
        var frame = window is UI.SettingsWindow or UI.ScanWindow ? 16.0 : 0.0;
        var margin = content.Margin;
        var innerWidth = width - frame - margin.Left - margin.Right;

        if (window is UI.SettingsWindow settings) settings.PrepareForRender(width);

        // 排布时要连 Margin 一起算，所以用根元素的完整占位宽度
        var slotWidth = innerWidth + margin.Left + margin.Right;

        // 关键：必须显式清掉 Width，让元素按内容自适应。
        //
        // Measure 的可用宽度给的是"窗口内容区宽度"（不含边框），
        // 根元素自己的 Margin 会在这个宽度里被扣掉 —— 这正是我们要的：
        // 内容宽度 = 可用宽度 - 左右 Margin = 348，Marrange 位置从 (margin.Left, margin.Top) 开始。
        content.Width = double.NaN;

        // 固定高度的窗口（扫描窗口里有 * 行，高度靠窗口本身决定）要给个确定的高度约束，
        // 否则 Measure 出来是内容的最小高度，列表区会被压扁、跑出画面。
        var available = fixedHeight is double fh
            ? new Size(slotWidth, fh - margin.Top - margin.Bottom)
            : new Size(slotWidth, double.PositiveInfinity);

        content.Measure(available);
        content.Arrange(new Rect(0, 0, slotWidth,
            fixedHeight is double h2 ? h2 : content.DesiredSize.Height));
        content.UpdateLayout();

        var w = (int)Math.Ceiling(width);
        var h = (int)Math.Ceiling(fixedHeight ?? content.DesiredSize.Height);

        // 布局体检：对比"内容想要的宽度"和"窗口能给的宽度"。
        // 截图只能看出"看起来裁了"，这个数字能直接证明有没有溢出、差多少。
        ReportOverflow(content, width - (window is UI.SettingsWindow or UI.ScanWindow ? 16.0 : 0.0));

        var bitmap = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, w, h));
        }
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);

        Log.Write($"UI 截图: {outputPath} ({w}x{h})");
    }

    /// <summary>
    /// 布局溢出体检：把所有后代元素的实际右边界和窗口右边界比一遍。
    /// 界面上"文字被裁一半"这种事光看截图容易看漏，这里换算成数字写进日志。
    /// </summary>
    private static void ReportOverflow(FrameworkElement root, double availableWidth)
    {
        var offenders = new List<string>();

        void Walk(DependencyObject node)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement fe && fe.ActualWidth > 0)
                {
                    var right = fe.TranslatePoint(new Point(fe.ActualWidth, 0), root).X;
                    if (right > availableWidth + 0.5)
                    {
                        var label = fe is TextBlock tb ? "TextBlock«" + Trim(tb.Text, 18) + "»"
                                   : fe is Button btn ? "Button«" + Trim(btn.Content?.ToString(), 14) + "»"
                                   : fe.GetType().Name;
                        offenders.Add($"{label} 右边界 {right:F1} > 可用 {availableWidth:F1}");
                    }
                }
                Walk(child);
            }
        }

        Walk(root);

        if (offenders.Count == 0)
        {
            Log.Write($"布局体检: 无溢出（可用宽度 {availableWidth:F0}）");
        }
        else
        {
            Log.Write($"布局体检: {offenders.Count} 处溢出\n  " +
                      string.Join("\n  ", offenders.Distinct().Take(12)));
        }
    }

    private static string Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s[..max] + "…" : s);

    /// <summary>塞几台假设备，检查"未知电量 / 低电量 / 充电 / USB 供电 / 离线"这些状态长什么样。</summary>
    private static void InjectDemo(BatteryMonitor monitor)
    {
        var mouse = new DeviceState("demo:mouse", "demo")
        {
            DefaultName = "PRO X SUPERLIGHT 2 SE", Kind = DeviceKind.Mouse, Link = LinkKind.Receiver24G,
            Percent = 12, Online = true, LowThreshold = 20,
        };
        var keyboard = new DeviceState("demo:kb", "demo")
        {
            DefaultName = "机械师 K600", Kind = DeviceKind.Keyboard, Link = LinkKind.Receiver24G,
            Percent = null, Note = "未提供电量接口", Online = true, LowThreshold = 20,
        };
        var headset = new DeviceState("demo:hs", "demo")
        {
            DefaultName = "OPPO Enco Free4", Kind = DeviceKind.Headset, Link = LinkKind.Bluetooth,
            Percent = 50, Note = "左右耳取最低", Online = true, LowThreshold = 20,
        };
        var gamepad = new DeviceState("demo:pad", "demo")
        {
            DefaultName = "Xbox 无线控制器", Kind = DeviceKind.Gamepad, Link = LinkKind.Bluetooth,
            Percent = 73, Online = true, LowThreshold = 20,
        };
        // 有线模式：界面应显示真实电量 + 充电小图标（不是"充电中 100%"）
        var wiredMouse = new DeviceState("demo:wired", "demo")
        {
            DefaultName = "GPW 2（有线模式）", Kind = DeviceKind.Mouse, Link = LinkKind.Receiver24G,
            Percent = 12, ExternalPower = true, Charging = true, Online = true, LowThreshold = 20,
        };
        // 有线模式的键盘：读不到电量，但插着线 —— 只能显示"充电中"
        var wiredKeyboard = new DeviceState("demo:wiredkb", "demo")
        {
            DefaultName = "机械师 K600（有线）", Kind = DeviceKind.Keyboard, Link = LinkKind.Wired,
            Percent = null, ExternalPower = true, Charging = true, Online = true, LowThreshold = 20,
        };
        var offlinePad = new DeviceState("demo:pad2", "demo")
        {
            DefaultName = "Xbox 无线控制器", Kind = DeviceKind.Gamepad, Link = LinkKind.Bluetooth,
            Percent = 73, LastSeen = DateTime.Now.AddDays(-2), LowThreshold = 20,
        };
        var offlineBuds = new DeviceState("demo:buds", "demo")
        {
            DefaultName = "OPPO Enco Air2", Kind = DeviceKind.Headset, Link = LinkKind.Bluetooth,
            Percent = null, LastSeen = DateTime.Now.AddHours(-3), LowThreshold = 20,
        };

        monitor.Online.Clear();
        monitor.Offline.Clear();
        monitor.Online.Add(mouse);
        monitor.Online.Add(keyboard);
        monitor.Online.Add(headset);
        monitor.Online.Add(gamepad);
        monitor.Online.Add(wiredMouse);
        monitor.Online.Add(wiredKeyboard);
        monitor.Offline.Add(offlinePad);
        monitor.Offline.Add(offlineBuds);
    }

    /// <summary>
    /// 把托盘图标画成对比图，检查各电量档位 + 低电量角标的观感。
    ///
    /// 分**深色/浅色两行**：托盘图标的背景是任务栏，用户的主题可能是深也可能是浅，
    /// 一个"在深色下好看"的图标很可能在浅色任务栏上糊掉。两种底色都画一遍才能验收。
    /// 每格按 96px 放大显示（原始 16px），这比在真托盘里更容易看出细节问题。
    /// </summary>
    private static void DumpTrayIcons(string path)
    {
        (int? percent, bool low)[] samples =
        {
            (null, false),   // 读不到电量
            (1, true),       // 极端低电量
            (12, true),      // 低电量（触发角标）
            (30, false),     // 琥珀
            (60, false),     // 绿
            (95, false),     // 接近满电
            (100, false),
        };

        const int cell = 96;
        var backgrounds = new[]
        {
            System.Drawing.Color.FromArgb(0x20, 0x20, 0x20),   // 深色任务栏
            System.Drawing.Color.FromArgb(0xF3, 0xF3, 0xF3),   // 浅色任务栏
        };

        using var canvas = new System.Drawing.Bitmap(cell * samples.Length, cell * backgrounds.Length);
        using var g = System.Drawing.Graphics.FromImage(canvas);
        // 放大用最近邻：这是"看 16px 下的真实像素"而不是"看平滑后的假象"
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        for (int row = 0; row < backgrounds.Length; row++)
        {
            using (var bg = new System.Drawing.SolidBrush(backgrounds[row]))
                g.FillRectangle(bg, 0, row * cell, canvas.Width, cell);

            for (int i = 0; i < samples.Length; i++)
            {
                using var icon = UI.TrayIcon.RenderIcon(samples[i].percent, samples[i].low);
                using var raw = icon.ToBitmap();

                // 关键：先缩到托盘真实显示的 16×16，再放大 4 倍展示。
                // 直接放大 32px 原图会掩盖"缩到 16 之后糊不糊"这类问题。
                using var small = new System.Drawing.Bitmap(16, 16);
                using (var sg = System.Drawing.Graphics.FromImage(small))
                {
                    sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    sg.DrawImage(raw, 0, 0, 16, 16);
                }
                g.DrawImage(small, i * cell + 16, row * cell + 16, 64, 64);
            }
        }
        canvas.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
