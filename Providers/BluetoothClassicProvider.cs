using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PeripheralBattery.Models;
using PeripheralBattery.Native;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace PeripheralBattery.Providers;

/// <summary>
/// 经典蓝牙（Classic / A2DP）通道：报"在不在线"，电量尽量从系统层 PnP 属性拿。
///
/// 电量：经典蓝牙协议本身没有统一的电量查询命令。但 Windows 的蓝牙栈会把
/// 支持上报的设备的电量缓存到设备节点的 PnP 属性上（就是设置页里显示的那个来源），
/// 所以这里再读一遍 <see cref="PnpBattery"/> —— 有就白拿，没有也不损失什么。
///
/// ⚠️ 这里**不判断充电/供电状态**。
/// PnP 属性组里那个语义像"充电中"的 pid=4，实测 K600 在纯蓝牙、没插线时也是 True，
/// 拿它推断"USB 供电"会导致拔了线还显示充电中（用户实际反馈过）。
/// 有线/供电只由 USB 直连接口的存在来判定，见 BatteryMonitor.ApplyWiredConnections。
///
/// 与其假装有数据，不如明确告诉用户「在线 · 未提供电量接口」。
///
/// 同一条蓝牙设备在 BLE 通道里也能被读到时，两者用同一个 Id（bt:地址），
/// 合并时会自动去重，有百分比的会覆盖这里的空值。
/// </summary>
public sealed class BluetoothClassicProvider : IBatteryProvider
{
    public string Name => "经典蓝牙";

    public async Task<ProviderResult> PollAsync(CancellationToken ct)
    {
        var devices = new List<DeviceSnapshot>();
        try
        {
            // 只取"已连接"的经典蓝牙设备，已断开的不会出现，省掉一轮轮连接探测
            var infos = await DeviceInformation.FindAllAsync(
                BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));

            // 系统层电量一次读完（按蓝牙地址索引），下面逐台设备查
            var pnpLevels = PnpBattery.ReadBluetoothLevels();

            foreach (var info in infos)
            {
                if (ct.IsCancellationRequested) break;

                BluetoothDevice? device = null;
                try
                {
                    device = await WinRtAsync.WithTimeout(BluetoothDevice.FromIdAsync(info.Id), 2000);
                    if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;

                    var name = string.IsNullOrWhiteSpace(device.Name) ? info.Name : device.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var kind = DeviceKindGuesser.Guess(name);
                    if (kind == DeviceKind.Other) continue;   // 不认识的外设不乱列出来

                    var level = pnpLevels.TryGetValue(device.BluetoothAddress, out var lv) ? (int?)lv : null;

                    devices.Add(new DeviceSnapshot
                    {
                        Id = $"bt:{device.BluetoothAddress:X12}",
                        Name = name,
                        Kind = kind,
                        Link = LinkKind.Bluetooth,
                        Percent = level,
                        // 经典蓝牙拿不到可靠的充电状态，就别瞎报 —— 让 USB 直连检测去判断
                        Charging = false,
                        ExternalPower = false,
                        Note = level == null ? "未提供电量接口" : null,
                    });
                }
                catch
                {
                    // 忽略单台设备的异常
                }
                finally
                {
                    device?.Dispose();
                }
            }
            return new ProviderResult { Ok = true, Devices = devices };
        }
        catch (Exception ex)
        {
            return ProviderResult.Fail(ex.Message);
        }
    }
}
