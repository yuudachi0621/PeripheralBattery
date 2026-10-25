using System;
using System.Threading;
using System.Threading.Tasks;
using PeripheralBattery.Models;

namespace PeripheralBattery.Providers;

/// <summary>
/// 电量来源的抽象。一个 Provider 负责一类硬件通道（罗技 HID++ / 蓝牙 GATT / ...），
/// 以后要加 XBOX 有线手柄，只要再实现一个 Provider 并在 Monitor 里注册即可。
///
/// 约定（新写一个 Provider 时必须遵守，否则会拖垮整轮轮询）：
///   · 只返回**这一轮看到的设备**，不要自己缓存上次的结果 —— 合并与离线判定由 Monitor 负责；
///   · 同一个物理设备在不同通道里必须用同一个 Id（如蓝牙都写 $"bt:{地址}"），
///     否则合并时会被当成两台设备；
///   · 能省电就省电：读完立刻断开连接，别让射频常驻（BleProvider / OpoProvider 是范例）；
///   · 整条通道不可用时返回 Ok=false，不要抛异常，也不要用"读到 0 台"表达失败 ——
///     两者含义不同：Ok=false 表示"不知道"，不会让已有设备被判离线。
/// </summary>
public interface IBatteryProvider
{
    /// <summary>通道名，用于日志、界面副标题和「哪个通道失败」的判断。</summary>
    string Name { get; }

    /// <summary>轮询一次。实现方要保证不抛异常（抛了也不致命，Monitor 会兜住）。</summary>
    Task<ProviderResult> PollAsync(CancellationToken ct);
}

/// <summary>
/// 从设备名称猜类型，只影响图标和分组，猜错也无害。
///
/// 为什么靠猜而不是靠查表：Windows 没有统一的"外设类别"API，
/// 而设备名里几乎总带着型号关键词（"SUPERLIGHT""K600""Enco"）。
/// 匹配顺序有讲究：**先手柄、再耳机、再键盘、最后鼠标** ——
/// 因为 "Xbox 无线控制器" 里含 controller、"耳机"常常带 "headset" 而 headset 里又含 "set"，
/// 顺序反过来就会误判。没猜出来返回 Other，经典蓝牙通道会据此把陌生设备过滤掉。
/// </summary>
public static class DeviceKindGuesser
{
    public static DeviceKind Guess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return DeviceKind.Other;
        var n = name.ToLowerInvariant();

        if (n.Contains("xbox") || n.Contains("controller") || n.Contains("gamepad") || n.Contains("手柄"))
            return DeviceKind.Gamepad;
        if (n.Contains("headset") || n.Contains("earbud") || n.Contains("buds") || n.Contains("airpods")
            || n.Contains("enco") || n.Contains("耳机"))
            return DeviceKind.Headset;
        if (n.Contains("keyboard") || n.Contains("k600") || n.Contains("keys") || n.Contains("键盘"))
            return DeviceKind.Keyboard;
        if (n.Contains("mouse") || n.Contains("superlight") || n.Contains("g pro") || n.Contains("鼠标"))
            return DeviceKind.Mouse;

        return DeviceKind.Other;
    }
}
