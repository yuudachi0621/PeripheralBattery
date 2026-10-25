using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace PeripheralBattery.Models;

/// <summary>设备类型，只用于选图标和分组。</summary>
public enum DeviceKind { Mouse, Keyboard, Headset, Gamepad, Other }

/// <summary>连接方式，界面上的小徽标。</summary>
public enum LinkKind { Receiver24G, Bluetooth, Wired }

/// <summary>
/// 某个 Provider 一次轮询里看到的「一台设备」。
/// 这是纯数据，不带任何界面状态。
/// </summary>
public sealed class DeviceSnapshot
{
    public required string Id { get; init; }        // 稳定标识，用于记忆自定义名称
    public required string Name { get; init; }      // 设备自报的名称
    public DeviceKind Kind { get; init; } = DeviceKind.Other;
    public LinkKind Link { get; init; } = LinkKind.Receiver24G;
    public int? Percent { get; init; }              // null = 设备在线但没给电量
    public bool Charging { get; init; }

    /// <summary>
    /// 是否被 USB 线直接供电（罗技 0x1004 的 [7] 外部供电状态）。
    ///
    /// 和 Charging 的区别：Charging 表示"电池正在被充"，externalPower 表示"现在吃的是 USB 的电"。
    /// 插着线且已充满时，Charging 会变回 false，但设备仍然是 USB 供电。
    ///
    /// 界面表现：电量旁显示一个充电小图标，**电量数字照实显示**（插线读到 79% 就显示 79%）。
    /// 别改回"一律显示 100%"—— 试过了，用户会以为程序读数不准。
    /// </summary>
    public bool ExternalPower { get; init; }

    public string? Note { get; init; }              // 无法读电量时给用户的说明
}

/// <summary>一个 Provider 的轮询结果。Ok=false 表示这轮整条通道失败，不能据此判定设备离线。</summary>
public sealed class ProviderResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public List<DeviceSnapshot> Devices { get; init; } = new();
    public static ProviderResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>
/// 界面上的设备条目：把快照 + 用户自定义名称 + 在线状态合到一起。
/// 实现 INotifyPropertyChanged 以便数据绑定自动刷新。
/// </summary>
public sealed class DeviceState : INotifyPropertyChanged
{
    public string Id { get; }

    /// <summary>由哪条通道负责它。合并时按这个判断"通道正常但设备没出现"。</summary>
    public string ProviderName { get; set; }

    public DeviceState(string id, string providerName)
    {
        Id = id;
        ProviderName = providerName;
    }

    private string _customName = "";
    /// <summary>用户自定义名称，空则用 DefaultName。</summary>
    public string CustomName
    {
        get => _customName;
        set { if (_customName == value) return; _customName = value ?? ""; Raise(); Raise(nameof(Name)); }
    }

    private string _defaultName = "未知设备";
    /// <summary>设备自己报的名字（每轮轮询都会覆盖）。改名后旧名字仍在，恢复默认名称靠它。</summary>
    public string DefaultName
    {
        get => _defaultName;
        set { if (_defaultName == value) return; _defaultName = value ?? ""; Raise(); Raise(nameof(Name)); }
    }

    /// <summary>界面上真正显示的名字：用户改过就用改过的，没改就用设备自报的。</summary>
    public string Name => string.IsNullOrWhiteSpace(_customName) ? _defaultName : _customName;

    private DeviceKind _kind;
    /// <summary>设备类型，只决定图标和排序（鼠标 → 键盘 → 耳机 → 手柄 → 其他）。</summary>
    public DeviceKind Kind { get => _kind; set { _kind = value; Raise(); Raise(nameof(IconGeometry)); } }

    private LinkKind _link;
    /// <summary>连接方式，显示成副标题里的"2.4G / 蓝牙 / 有线"。</summary>
    public LinkKind Link { get => _link; set { _link = value; Raise(); Raise(nameof(LinkText)); } }

    private int? _percent;
    /// <summary>
    /// 电池的**原始**读数（null = 在线但没给电量）。
    /// 界面上要用电量时走 <see cref="EffectivePercent"/>（目前它就是这个值，见那里的说明）——
    /// 两者分开保留，是为了将来如果真需要"对外呈现值"与"原始值"不一致时有地方下手。
    /// </summary>
    public int? Percent
    {
        get => _percent;
        set
        {
            _percent = value;
            Raise(); Raise(nameof(PercentText)); Raise(nameof(LevelBrush)); Raise(nameof(HasLevel));
            Raise(nameof(IsLow)); Raise(nameof(EffectivePercent)); Raise(nameof(SubText));
            Raise(nameof(LevelHint)); Raise(nameof(ShowLevelUnknown));
        }
    }

    private int _lowThreshold = 20;
    /// <summary>低电量阈值（%），由配置同步进来。低于等于它时界面转红。</summary>
    public int LowThreshold
    {
        get => _lowThreshold;
        set { if (_lowThreshold == value) return; _lowThreshold = value; Raise(); Raise(nameof(LevelBrush)); Raise(nameof(IsLow)); }
    }

    /// <summary>
    /// 是否低电量（用于告警：托盘标红、弹气泡）。正在充电 / USB 供电时永远不算低电量 ——
    /// 插着线还弹"电量低"是最糟糕的体验。注意这不影响数字颜色，颜色照实按电量分档。
    /// </summary>
    public bool IsLow => !IsChargingLike && _percent is int p && p <= _lowThreshold;

    private bool _charging;
    /// <summary>电池正在被充（不代表现在吃的是线的电，见 <see cref="ExternalPower"/>）。</summary>
    public bool Charging
    {
        get => _charging;
        set
        {
            if (_charging == value) return;
            _charging = value;
            Raise(); Raise(nameof(SubText)); Raise(nameof(LevelHint));
            Raise(nameof(ShowChargeIcon)); Raise(nameof(PercentText)); Raise(nameof(IsLow));
            // LevelBrush 依赖 IsChargingLike：无电量时"未充电=灰、充电中=绿"，所以也要通知；
            // ShowLevelUnknown 同理（充电时不显示"未知"图标，改显示"充电中"）
            Raise(nameof(LevelBrush)); Raise(nameof(ShowLevelUnknown));
        }
    }

    private bool _externalPower;
    /// <summary>
    /// 是否由 USB 线直接供电。为 true 时界面在电量旁显示一个充电小图标，
    /// 但**电量数字照实显示**（插线时读到 79% 就显示 79%）——
    /// 早先的版本在这里固定报 100%，结果用户插着线看到"充电中 100%"、
    /// 实际电池才 79%，反而像是程序在骗人。
    /// </summary>
    public bool ExternalPower
    {
        get => _externalPower;
        set
        {
            if (_externalPower == value) return;
            _externalPower = value;
            Raise();
            Raise(nameof(PercentText)); Raise(nameof(EffectivePercent)); Raise(nameof(HasLevel));
            Raise(nameof(IsLow)); Raise(nameof(LevelBrush)); Raise(nameof(SubText)); Raise(nameof(LevelHint));
            Raise(nameof(ShowChargeIcon)); Raise(nameof(ShowLevelUnknown));
        }
    }

    /// <summary>
    /// 是否该显示"充电中"的小图标。两种情况都算：电池正在被充（<see cref="Charging"/>），
    /// 或者设备直接吃 USB 线的电（<see cref="ExternalPower"/>）。判据见 <see cref="DeviceDisplay.IsCharging"/>。
    /// </summary>
    public bool ShowChargeIcon => DeviceDisplay.ShowCharge(_charging, _externalPower);

    private bool _online;
    /// <summary>
    /// 这一轮是否读到它。由 Monitor 维护：读到 → true；
    /// 通道正常但连续 2 轮没出现 → false（只漏一轮不算，避免无线设备唤醒时界面乱闪）。
    /// </summary>
    public bool Online
    {
        get => _online;
        set { _online = value; Raise(); Raise(nameof(SubText)); Raise(nameof(OfflineText)); }
    }

    private DateTime _lastSeen = DateTime.Now;
    /// <summary>最后一次读到的本地时间。离线时用来显示"3 小时前"。</summary>
    public DateTime LastSeen
    {
        get => _lastSeen;
        set { _lastSeen = value; Raise(); Raise(nameof(OfflineText)); }
    }

    private string? _note;
    /// <summary>"为什么没电量"之类的一句说明，比如"未提供电量接口""左右耳取最低"。</summary>
    public string? Note { get => _note; set { _note = value; Raise(); Raise(nameof(SubText)); } }

    private bool _isPrimary;
    /// <summary>是不是用户指定的主设备（托盘图标显示它的电量）。</summary>
    public bool IsPrimary { get => _isPrimary; set { if (_isPrimary == value) return; _isPrimary = value; Raise(); } }

    /// <summary>连续多少轮没被读到。用来避免无线设备偶发不应答就判定离线。</summary>
    public int Misses { get; set; }

    /// <summary>
    /// 当前在线状态是否由「USB 有线兜底」标记的（而不是无线通道读到的）。
    /// 有线拔掉后要靠这个标志把状态清掉，让正常离线判定接管。
    /// 纯内部状态，不进界面。
    /// </summary>
    public bool WiredMarked { get; set; }

    /// <summary>
    /// 进入"有线模式"之前的连接方式。拔线/切回无线时要还原回去，
    /// 否则会一直显示"有线"。纯内部状态。
    /// </summary>
    public LinkKind? LinkBeforeWired { get; set; }

    // ---- 派生显示属性 ----

    /// <summary>
    /// 界面上应当显示的电量 —— 就是电池的真实读数。
    ///
    /// 这里**不再**对 USB 供电的设备返回 100：插着线时 HID++ 依然会回真实电量
    /// （实测 [4] 从 0x4E 到 0x4F，充电时还在往上走），强制显示 100 反而是错的。
    /// 电量条、托盘图标、排序都用这个值。
    /// </summary>
    public int? EffectivePercent => _percent;

    /// <summary>
    /// 电量栏文字。三种形态见 <see cref="DeviceDisplay.LevelText"/>：
    /// 有读数写数字、在充电但没有读数写"充电中"、都没有则留空交给"未知"图标。
    /// </summary>
    public string PercentText => DeviceDisplay.LevelText(_percent, _charging, _externalPower);

    /// <summary>
    /// 是否该显示"电量未知"图标（读不到电量、也不在充电）。
    /// 和 <see cref="PercentText"/> 的空字符串是一对：文字让位给图标。
    /// </summary>
    public bool ShowLevelUnknown => DeviceDisplay.ShowUnknown(_percent, _charging, _externalPower);

    /// <summary>把"充电中"的两种来源合成一个判断，界面各处口径统一。</summary>
    public bool IsChargingLike => DeviceDisplay.IsCharging(_charging, _externalPower);

    /// <summary>
    /// 悬停提示里表示"正在充电"的短标记。**只用于系统提示框** ——
    /// 那里不能画矢量图，只能用字符，所以退化成 ASCII 的 "+"。
    ///
    /// 为什么是 "+" 而不是 "⚡"：系统提示框用 Segoe UI，
    /// 实测 "⚡"(U+26A1) 在这个字体里没有字形，会被渲染成**空心方框**（豆腐块），
    /// 反而更难看懂。ASCII 的 "+" 全字体覆盖，而且"数字前一个加号"表达
    /// "正在往里充"很直觉，还只占 1 个字符。
    ///
    /// 面板里不用这个 —— 那里直接用 <see cref="DeviceIcons.Charge"/> 画真正的闪电图标。
    /// </summary>
    public const string ExternalPowerBadge = "+";

    public bool HasLevel => EffectivePercent.HasValue;

    /// <summary>连接方式的中文短名，用来拼副标题。</summary>
    public string LinkText => _link switch
    {
        LinkKind.Bluetooth => "蓝牙",
        LinkKind.Wired => "有线",
        _ => "2.4G",
    };

    /// <summary>
    /// 名字下面那行小字。优先级是刻意排的：
    /// 离线 → 只说离线多久（说别的没意义）；
    /// **有线** → 只说"有线"。这时电量栏已经在说"充电中"/显示电量了，副标题重复一遍反而啰嗦，
    ///   而且用户要的是"知道它当前是有线还是无线"（K600 插线后会断开蓝牙，
    ///   早先版本这里写"USB 供电"，让人分不清它到底走的哪条路）；
    /// USB 供电（罗技那种还在用无线通道、只是插着线取电的）→ 说清供电方式，正在充再补"充电中"；
    /// 正在充电 → 说充电中。它排在"为什么没电量"这种长期说明**之前**；
    /// 然后才是说明文本；最后是纯连接方式。
    /// </summary>
    public string SubText
    {
        get
        {
            if (!_online) return OfflineText;
            if (_link == LinkKind.Wired) return "有线";
            if (_externalPower) return _charging ? "USB 供电 · 充电中" : "USB 供电";
            if (_charging) return LinkText + " · 充电中";
            if (_note != null) return LinkText + " · " + _note;
            return LinkText;
        }
    }

    /// <summary>电量栏 / 那个小图标的悬停说明，见 <see cref="DeviceDisplay.LevelHint"/>。</summary>
    public string LevelHint => DeviceDisplay.LevelHint(_percent, _charging, _externalPower, _note);

    /// <summary>离线设备显示的文字："离线 · 3 小时前"。</summary>
    public string OfflineText => "离线 · " + Humanize(DateTime.Now - _lastSeen);

    /// <summary>
    /// 电量条 / 数字的颜色：高电量暖绿、中等琥珀、低电量警示红、读不到灰色。
    ///
    /// 充电中不特殊处理 —— 颜色只反映"还剩多少电"，是否在充电由旁边的小图标表达。
    /// 插着线但只剩 10% 时该红还是红，那才是真实情况。
    /// （不弹告警另说，见 <see cref="IsLow"/>。）
    ///
    /// 唯一例外：完全没有电量读数、但正在充电（有线模式的键盘）。这时没有"多少电"可言，
    /// 用灰色会把唯一的有效信息也压暗，所以给它暖绿。
    /// </summary>
    public Brush LevelBrush
    {
        get
        {
            if (_percent is not int p) return IsChargingLike ? Palettes.LevelHigh : Palettes.Muted;
            if (p <= _lowThreshold) return Palettes.LevelLow;
            if (p <= 40) return Palettes.LevelMid;
            return Palettes.LevelHigh;
        }
    }

    /// <summary>
    /// 充电小图标的颜色，固定用暖绿。
    ///
    /// 刻意**不跟随**数字颜色：12% 且正在充电时，数字该是警示红（电量确实低），
    /// 但闪电不该也变红 —— 红色闪电会被读成"充电出问题了"。
    /// 红数字 + 绿闪电 = "电量低，但正在充"，两个信息各自成立、互不干扰。
    /// </summary>
    public Brush ChargeBrush => Palettes.LevelHigh;

    /// <summary>按类型返回矢量图标（细线风格），避免依赖任何图标字体。</summary>
    public Geometry IconGeometry => DeviceIcons.For(Kind);

    // ---- 重命名交互 ----
    // 双击（或右键"重命名"）进入编辑态：IsEditing 控制"文字/输入框"二选一，
    // EditName 是输入框的缓冲值 —— 只有按回车或失焦时才写回 CustomName，
    // 中途按 Esc 直接把 IsEditing 关掉即可，改到一半的内容自然丢弃。

    private bool _isEditing;
    public bool IsEditing { get => _isEditing; set { _isEditing = value; Raise(); } }

    private string _editName = "";
    public string EditName { get => _editName; set { _editName = value; Raise(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>把时间差说成人话："刚刚 / 12 分钟前 / 3 小时前 / 2 天前"。</summary>
    private static string Humanize(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " 分钟前";
        if (span.TotalHours < 24) return (int)span.TotalHours + " 小时前";
        return (int)span.TotalDays + " 天前";
    }
}

/// <summary>
/// 全局配色：电量条与电量数字用的四档颜色（高 / 中 / 低 / 未知）。
/// 具体色值由主题在启动时注入（见 UI/Theme.cs），这样浅色/深色各有一套，
/// 而不是代码里写死一版。Theme.Apply() 之前访问到的是下面这组浅色默认值。
/// </summary>
public static class Palettes
{
    // 默认值 = 浅色主题（Claude 风格：暖绿 / 琥珀 / 警示红）
    private static Brush _high = Freeze(Color.FromRgb(0x6D, 0x9E, 0x5C));
    private static Brush _mid = Freeze(Color.FromRgb(0xC8, 0x8B, 0x3A));
    private static Brush _low = Freeze(Color.FromRgb(0xC0, 0x4B, 0x3F));
    private static Brush _muted = Freeze(Color.FromRgb(0x9A, 0x94, 0x88));

    public static Brush LevelHigh => _high;
    public static Brush LevelMid => _mid;
    public static Brush LevelLow => _low;
    public static Brush Muted => _muted;

    /// <summary>
    /// 由主题调用，一次性换掉四档颜色（电量高 / 中 / 低 / 未知）。
    /// 传入的是 System.Drawing.Color（Theme 从注册表和十六进制字面量里读的都是它），
    /// 这里转成 WPF 的 SolidColorBrush 并 Freeze —— 冻结后可以跨线程共享，也不用重复分配。
    /// </summary>
    public static void SetLevels(System.Drawing.Color high, System.Drawing.Color mid,
        System.Drawing.Color low, System.Drawing.Color muted)
    {
        _high = Freeze(Color.FromRgb(high.R, high.G, high.B));
        _mid = Freeze(Color.FromRgb(mid.R, mid.G, mid.B));
        _low = Freeze(Color.FromRgb(low.R, low.G, low.B));
        _muted = Freeze(Color.FromRgb(muted.R, muted.G, muted.B));
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// 四种设备的线条图标（24x24 视图框，描边渲染）。
/// 用矢量路径而不是图标字体或位图：不依赖任何外部字体，缩放到任何尺寸都清晰，
/// 也方便跟主题的描边色绑定（Stroke="{DynamicResource TextSecondaryBrush}"）。
/// </summary>
public static class DeviceIcons
{
    private static readonly Geometry Mouse = Geometry.Parse(
        "M12 2.5 C8.4 2.5 5.5 5.6 5.5 9.4 V14.1 C5.5 18.2 8.4 21.5 12 21.5 " +
        "C15.6 21.5 18.5 18.2 18.5 14.1 V9.4 C18.5 5.6 15.6 2.5 12 2.5 Z M12 6.2 V10.2");

    private static readonly Geometry Keyboard = Geometry.Parse(
        "M3.6 7.4 H20.4 A2 2 0 0 1 22.4 9.4 V14.6 A2 2 0 0 1 20.4 16.6 H3.6 " +
        "A2 2 0 0 1 1.6 14.6 V9.4 A2 2 0 0 1 3.6 7.4 Z M5.4 13 H18.6 " +
        "M5.6 10.3 H6.9 M8.9 10.3 H10.2 M12.2 10.3 H13.5 M15.5 10.3 H16.8");

    private static readonly Geometry Headset = Geometry.Parse(
        "M4.6 14.2 V11.6 A7.4 7.4 0 0 1 19.4 11.6 V14.2 " +
        "M4.6 12.9 H4 A1.6 1.6 0 0 0 2.4 14.5 V17.1 A1.6 1.6 0 0 0 4 18.7 H6.1 V12.9 Z " +
        "M19.4 12.9 H20 A1.6 1.6 0 0 1 21.6 14.5 V17.1 A1.6 1.6 0 0 1 20 18.7 H17.9 V12.9 Z");

    private static readonly Geometry Gamepad = Geometry.Parse(
        "M7.9 7.8 H16.1 C19.3 7.8 21.5 10.4 21.5 13.6 C21.5 16.6 19.9 18.3 18.1 18.3 " +
        "C16.6 18.3 15.5 17.3 14.4 16.1 H9.6 C8.5 17.3 7.4 18.3 5.9 18.3 " +
        "C4.1 18.3 2.5 16.6 2.5 13.6 C2.5 10.4 4.7 7.8 7.9 7.8 Z " +
        "M7.6 11 V13.6 M6.3 12.3 H8.9 M15.7 11.9 H15.9 M17.7 13.7 H17.9");

    private static readonly Geometry Generic = Geometry.Parse(
        "M12 3.2 A8.8 8.8 0 1 0 12 20.8 A8.8 8.8 0 1 0 12 3.2 Z M12 8 V12.3 L15 14.3");

    /// <summary>
    /// 充电小图标：一道实心闪电。
    ///
    /// 用矢量而不是字符 —— 面板是 WPF 渲染，画形状没有字形缺失的风险，
    /// 而且能跟随主题色（绑定 ChargeBrush，固定暖绿）。
    /// 提示框里那个 ASCII 的 "+" 是不得已的退化方案，见 DeviceState.ExternalPowerBadge。
    /// </summary>
    public static readonly Geometry Charge = Geometry.Parse(
        "M13.2 2.2 L5.4 13.2 H10.8 L9.4 21.8 L18.2 10.4 H12.4 Z");

    /// <summary>
    /// "电量未知"图标：一个电池轮廓 + 一道斜杠，表示"这设备读不到电量"。
    ///
    /// 用来统一替代以前的破折号（"—"）。破折号的问题是在比例字体里跟对齐补白的空格
    /// 混在一起，看起来像是"少打了个字"；图标至少能说明这是一台**在线但无读数**的设备。
    /// 用描边渲染（Stroke），配色走次要文字色，存在感比电量数字低一档 —— 它传达的是"没有信息"。
    /// </summary>
    public static readonly Geometry Unknown = Geometry.Parse(
        "M4.5 7 H15.5 A2 2 0 0 1 17.5 9 V15 A2 2 0 0 1 15.5 17 H4.5 " +
        "A2 2 0 0 1 2.5 15 V9 A2 2 0 0 1 4.5 7 Z " +
        "M17.5 10.4 H20 V13.6 H17.5 M4.6 16.6 L15.4 7.4");

    public static Geometry For(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => Mouse,
        DeviceKind.Keyboard => Keyboard,
        DeviceKind.Headset => Headset,
        DeviceKind.Gamepad => Gamepad,
        _ => Generic,
    };
}
