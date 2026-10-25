using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PeripheralBattery.Models;

/// <summary>
/// "扫描其他设备"窗口里的一行：某条通道这次看到的设备。
///
/// 它和 DeviceState 的区别：这是一次性的扫描结果，不进轮询、不落盘，
/// 只有用户勾选并点"添加"后才会变成 DeviceState。
/// </summary>
public sealed class ScanItem : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DeviceKind Kind { get; init; }
    public LinkKind Link { get; init; }
    public int? Level { get; set; }
    public bool Charging { get; set; }

    /// <summary>
    /// 由 USB 线直接供电（有线模式 / 插着线充电）。
    /// 为 true 时在电量旁显示充电图标；**电量数字仍按真实值显示**，不会被改写成 100%。
    /// </summary>
    public bool ExternalPower { get; set; }

    public string? Note { get; set; }

    /// <summary>由哪条通道看到它，显示在副标题里，方便用户判断"为什么这台没电量"。</summary>
    public required string Provider { get; init; }

    /// <summary>是否已经在面板里了。已添加的默认不勾选。</summary>
    public bool AlreadyAdded { get; init; }

    private bool _selected;
    /// <summary>勾选状态。默认值 = 未添加，让用户点开就能直接勾。</summary>
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Raise(); }
    }

    /// <summary>充电图标的颜色，固定暖绿（与主面板的 DeviceState.ChargeBrush 同口径）。</summary>
    public System.Windows.Media.Brush ChargeBrush => Palettes.LevelHigh;

    /// <summary>副标题：通道 +（没电量时）原因。连接方式只在和通道名不重复时才拼上前缀。</summary>
    public string SubText
    {
        get
        {
            // 通道名和连接方式常常撞车（"罗技 2.4G" + "2.4G"、"经典蓝牙" + "蓝牙"），
            // 直接拼会读成"罗技 2.4G · 2.4G"，所以撞了就不重复写。
            var text = Provider.Contains(LinkText, StringComparison.OrdinalIgnoreCase)
                ? Provider
                : Provider + " · " + LinkText;

            // 充电状态优先于 Note：Note 说明的是"为什么没有数字"这种长期情况，
            // 而"正在充电"是当下更重要的事实（尤其有线模式的键盘，两者会同时成立）。
            if (ExternalPower) text += Charging ? " · USB 供电 · 充电中" : " · USB 供电";
            else if (Charging) text += " · 充电中";
            else if (Note != null) text += " · " + Note;
            return text;
        }
    }

    public string LinkText => Link switch
    {
        LinkKind.Bluetooth => "蓝牙",
        LinkKind.Wired => "有线",
        _ => "2.4G",
    };

    /// <summary>
    /// 电量文字。判断规则与主面板共用 <see cref="DeviceDisplay.LevelText"/>，
    /// 保证两个界面的口径永远一致。
    /// </summary>
    public string LevelText => DeviceDisplay.LevelText(Level, Charging, ExternalPower);

    /// <summary>读不到电量也不在充电：显示统一的"电量未知"图标（与主面板一致）。</summary>
    public bool ShowLevelUnknown => DeviceDisplay.ShowUnknown(Level, Charging, ExternalPower);

    /// <summary>是否显示充电图标（与主面板同口径）。</summary>
    public bool ShowChargeIcon => DeviceDisplay.ShowCharge(Charging, ExternalPower);

    /// <summary>电量栏的悬停说明。</summary>
    public string LevelHint => DeviceDisplay.LevelHint(Level, Charging, ExternalPower, Note);

    public string StatusText => AlreadyAdded ? "已添加" : "未添加";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>设置页里"托盘图标显示哪个设备"的一个选项。</summary>
public sealed class ScanOption
{
    public ScanOption(string id, string display)
    {
        Id = id;
        Display = display;
    }

    /// <summary>空字符串 = 自动（所有设备最低值）。</summary>
    public string Id { get; }

    public string Display { get; }

    public override string ToString() => Display;
}
