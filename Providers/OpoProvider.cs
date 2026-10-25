using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeripheralBattery.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace PeripheralBattery.Providers;

/// <summary>
/// OPPO / OnePlus / realme 耳机通道（社区称 OPOv1 私有协议）。
///
/// 为什么需要它：这类 TWS 耳机只走经典蓝牙（A2DP + HFP），
/// 既不实现 BLE 电池服务 0x180F，也拒绝建立 LE 连接，
/// Windows 系统层也没给它们缓存 DEVPKEY_Device_BatteryLevel。
/// 唯一能拿到电量的地方是它们自己的控制通道：
///   RFCOMM 服务 0000079A-D102-11E1-9B23-00025B00A5A5（BBK 系共用）
///
/// 协议细节（外层帧怎么切、LEB128 长度、seq 怎么配对）不在这个文件里，
/// 收发与拆帧全在 <see cref="OpoLink"/>，那里有完整说明。这里只关心业务层：
///
/// 我们只发一条只读查询：
///   发 0x0106 → 收 0x8106（回复命令号 = 请求命令号 | 0x8000），
///   载荷 = 状态(1) + count(1) + (part, raw) × count
///   part：01 左耳 / 02 右耳 / 03 充电盒；raw 的 bit7 = 充电中，低 7 位 = 百分比。
///   取值规则见 <see cref="OpoLink.PickLevel"/>。
///
/// 省电：和 BleProvider 一样，读完立刻断开，不常驻链路。
/// </summary>
public sealed class OpoProvider : IBatteryProvider
{
    /// <summary>OPO 控制通道（BBK 生态通用）。</summary>
    private static readonly Guid OpoService = new("0000079a-d102-11e1-9b23-00025b00a5a5");

    /// <summary>另一个变体，个别机型用这个。</summary>
    private static readonly Guid OpoServiceAlt = new("00001107-d102-11e1-9b23-00025b00a5a5");

    private const ushort QueryBattery = 0x0106;

    public string Name => "OPPO 耳机协议";

    public async Task<ProviderResult> PollAsync(CancellationToken ct)
    {
        var devices = new List<DeviceSnapshot>();
        try
        {
            // 只查已连接的经典蓝牙设备：耳机没开或没连上时不必为它等超时
            var infos = await DeviceInformation.FindAllAsync(
                BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));

            foreach (var info in infos)
            {
                if (ct.IsCancellationRequested) break;

                BluetoothDevice? device = null;
                try
                {
                    device = await WinRtAsync.WithTimeout(BluetoothDevice.FromIdAsync(info.Id), 2500);
                    if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;

                    var name = string.IsNullOrWhiteSpace(device.Name) ? info.Name : device.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // 先看它有没有 OPO 控制通道，没有就说明不归这条路管。
                    // 用缓存模式查：这个 UUID 配对时就固定了，没必要每次让蓝牙重新做 SDP 查询。
                    var services = await WinRtAsync.WithTimeout(
                        device.GetRfcommServicesAsync(BluetoothCacheMode.Cached), 3000);
                    var target = services?.Services.FirstOrDefault(s =>
                        s.ServiceId.Uuid == OpoService || s.ServiceId.Uuid == OpoServiceAlt);
                    if (target == null) continue;

                    var level = await ReadBatteryAsync(target, ct);
                    if (level == null) continue;

                    devices.Add(new DeviceSnapshot
                    {
                        Id = $"bt:{device.BluetoothAddress:X12}",   // 与蓝牙通道同 Id，合并时自动去重
                        Name = name,
                        Kind = DeviceKindGuesser.Guess(name),
                        Link = LinkKind.Bluetooth,
                        Percent = level.Value.percent,
                        Charging = level.Value.charging,
                        Note = "左右耳取最低",
                    });
                }
                catch
                {
                    // 单台设备失败不影响其它设备
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

    /// <summary>连上控制通道，发一次电量查询，等回复。</summary>
    private static async Task<(int percent, bool charging)?> ReadBatteryAsync(
        RfcommDeviceService service, CancellationToken ct)
    {
        using var socket = new StreamSocket();
        using var link = new OpoLink(socket);

        try
        {
            var connect = socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName).AsTask();
            if (await Task.WhenAny(connect, Task.Delay(2500, ct)) != connect) return null;
            await connect;

            link.Start();

            // 耳机建链后可能先推一条事件（0x0204），紧接着才是我们的回复。
            // 没必要干等固定时长 —— 直接发查询，回复会排在事件后面一起被读出来。
            var reply = await link.QueryAsync(QueryBattery, 2200, ct);
            if (reply == null) return null;

            return OpoLink.PickLevel(reply);
        }
        catch
        {
            return null;
        }
    }
}
