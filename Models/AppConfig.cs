using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeripheralBattery.Models;

/// <summary>
/// 持久化的一份设备档案（记住自定义名称、上次电量和最后在线时间）。
///
/// 它和设备在界面上的活对象（DeviceState）是两回事：这里只有"下次启动时还认得它"所需要的东西。
///
/// 枚举字段（Kind / Link）存成 int：好处是**枚举成员改名**不会让老配置失效；
/// 但它**挡不住顺序调整** —— 读取时是直接强转 `(DeviceKind)record.Kind`，
/// 所以往 LinkKind 中间插一个值会让老配置里的 2 被解释成别的连接方式。
/// 需要演进时只能往末尾追加，或者改成显式固定数值 / 存枚举名。
/// </summary>
public sealed class DeviceRecord
{
    /// <summary>用户改过的名字；空 = 没有改过，用 DefaultName。</summary>
    public string CustomName { get; set; } = "";

    /// <summary>设备自报的名字，同时是"恢复默认名称"的依据。</summary>
    public string DefaultName { get; set; } = "";

    /// <summary>DeviceKind 的整数值。</summary>
    public int Kind { get; set; }

    /// <summary>LinkKind 的整数值。</summary>
    public int Link { get; set; }

    /// <summary>上次读到的电量。启动时先显示它，避免界面在第一次轮询完成前一片空白。</summary>
    public int? LastPercent { get; set; }

    /// <summary>最后在线时间（UTC 存，显示时再转本地）。</summary>
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>上次退出/保存时它是否在线。启动时先按这个状态显示，避免第一次没读到就闪成"离线"。</summary>
    public bool WasOnline { get; set; } = true;

    /// <summary>由哪条通道读到它。判断离线时要用（通道成功但设备没出现才算离线）。</summary>
    public string Provider { get; set; } = "";

    /// <summary>
    /// 用户是否把它收进了面板。
    ///
    /// 为什么需要这个开关：轮询会看到一堆外设（别人的耳机、已经配对但不用的设备），
    /// 全都塞进面板会让列表变噪音。所以"发现"和"关注"分开：
    ///   · 老配置没有这个字段 → 默认 true（升级后原有设备一个不丢）；
    ///   · 新发现的设备在加入跟踪列表时显式置 true；
    ///   · 用户可以删掉不想看的（置 false）；离线时删掉则连记录一起删。
    /// </summary>
    public bool Tracked { get; set; } = true;
}

/// <summary>全部配置，一个 JSON 文件搞定。</summary>
public sealed class AppConfig
{
    /// <summary>轮询间隔（秒）。默认 60 秒：既够及时，蓝牙射频唤醒也不至于太频繁。</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>开机自启动。</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>在折叠区显示离线/历史设备。</summary>
    public bool ShowOffline { get; set; } = true;

    /// <summary>用户长时间无操作时自动降频，省 CPU 和射频功耗。</summary>
    public bool ThrottleWhenIdle { get; set; } = true;

    /// <summary>低电量时弹 Windows 通知气泡。</summary>
    public bool NotifyLowBattery { get; set; } = true;

    /// <summary>低电量阈值（%）。未充电设备的电量小于等于它时触发提醒，面板颜色也以此为红色界线。</summary>
    public int LowBatteryPercent { get; set; } = 20;

    /// <summary>
    /// 主设备 Id（空 = 未指定，托盘图标显示所有在线、已关注设备里的最低电量）。
    ///
    /// 主设备**只影响托盘图标上画哪个数字**。面板列出所有已关注设备；悬停提示优先列出
    /// 所有在线已关注设备，只有超过 Windows 63 字符限制时才降级 ——
    /// 用户把鼠标移到托盘图标上就是想一眼看全所有外设，只报一台反而更费解。
    /// </summary>
    public string PrimaryDeviceId { get; set; } = "";

    /// <summary>key = 设备 Id。</summary>
    public Dictionary<string, DeviceRecord> Devices { get; set; } = new();
}

/// <summary>配置读写：%APPDATA%\PeripheralBattery\config.json</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>配置、日志和诊断辅助文件使用的用户数据目录。</summary>
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeripheralBattery");

    /// <summary><see cref="Dir"/> 下的配置文件完整路径。</summary>
    public static string FilePath { get; } = Path.Combine(Dir, "config.json");

    /// <summary>
    /// 只读沙盒模式：为 true 时 Save() 什么都不写。
    ///
    /// 为什么必须有：--selftest 会构造 BatteryMonitor 并调用 SetPrimary / Remove，
    /// 而那些方法内部会 SaveNow()。以前自检跑一遍，就会把用户真实的 config.json
    /// 覆盖成一堆 "k:del""k:mouse" 之类的测试数据 —— 设备名、主设备、历史全没了。
    /// 诊断模式一律先打开这个开关。
    /// </summary>
    public static bool ReadOnly { get; set; }

    /// <summary>
    /// 读取并规范化配置。文件不存在、JSON 损坏或反序列化返回 null 时使用默认配置。
    /// </summary>
    /// <returns>可直接交给运行时代码使用的配置对象；不会返回 null。</returns>
    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg != null)
                {
                    if (cfg.PollSeconds < 5) cfg.PollSeconds = 5;   // 防止手改配置写出 0 导致忙轮询
                    if (cfg.LowBatteryPercent is < 1 or > 90) cfg.LowBatteryPercent = 20;
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏就当作首次运行，不让程序起不来
        }
        return new AppConfig();
    }

    /// <summary>
    /// 先写临时文件再替换正式文件，尽量避免崩溃或断电留下半截 JSON。
    /// 只读诊断模式会直接返回；目录创建或写盘失败也会静默忽略，不影响主流程。
    /// </summary>
    /// <param name="cfg">要持久化的配置对象。</param>
    public static void Save(AppConfig cfg)
    {
        // 诊断/自检模式下绝不碰真实配置文件（见 ReadOnly 的说明）
        if (ReadOnly) return;

        try
        {
            Directory.CreateDirectory(Dir);
            // 先写临时文件再替换，避免断电/崩溃留下半截 JSON
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // 写盘失败不影响运行
        }
    }
}
