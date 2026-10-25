using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PeripheralBattery.Providers;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;

namespace PeripheralBattery.Diagnostics;

/// <summary>
/// OPPO / OnePlus / realme 耳机私有协议探针（命令行 --probe-opo [关键字]）。
///
/// 用来验证 OpoProvider 的协议实现是否正确，把每条收发都打印出来。
/// 协议细节见 Providers/OpoLink.cs 的注释。
/// </summary>
internal static class OpoProbe
{
    private static readonly Guid OpoService = new("0000079a-d102-11e1-9b23-00025b00a5a5");
    private static readonly Guid OpoServiceAlt = new("00001107-d102-11e1-9b23-00025b00a5a5");

    public static async Task RunAsync(string? keyword)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var log = new StringBuilder();

        var paired = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        foreach (var info in paired)
        {
            if (!string.IsNullOrWhiteSpace(keyword)
                && !info.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;

            BluetoothDevice? device = null;
            try
            {
                device = await BluetoothDevice.FromIdAsync(info.Id);
                if (device == null) continue;

                var result = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
                var target = result.Services.FirstOrDefault(s =>
                    s.ServiceId.Uuid == OpoService || s.ServiceId.Uuid == OpoServiceAlt);
                if (target == null)
                {
                    log.AppendLine($"{device.Name}: 没有 OPO 控制通道");
                    continue;
                }

                log.AppendLine($"===== {device.Name}  通道 {target.ServiceId.Uuid} =====");

                using var socket = new StreamSocket();
                try
                {
                    await socket.ConnectAsync(target.ConnectionHostName, target.ConnectionServiceName);
                }
                catch (Exception ex)
                {
                    log.AppendLine($"  连接失败 {ex.Message}");
                    continue;
                }
                log.AppendLine("  已连接");

                using var link = new OpoLink(socket);
                link.Start();
                await Task.Delay(350);

                foreach (var (cmd, label) in new (ushort cmd, string label)[]
                         {
                             (0x0100, "能力位图"),
                             (0x0103, "产品ID"),
                             (0x0105, "固件版本"),
                             (0x0106, "电量"),
                         })
                {
                    var reply = await link.QueryAsync(cmd, 1500, default);
                    log.AppendLine(reply == null
                        ? $"  {label} (0x{cmd:X4}) → 无回复"
                        : $"  {label} (0x{cmd:X4}) → [{Convert.ToHexString(reply)}]");

                    if (cmd == 0x0106 && reply != null)
                    {
                        var level = OpoLink.PickLevel(reply);
                        log.AppendLine(level == null
                            ? "    => 解析失败"
                            : $"    => 电量 {level.Value.percent}%{(level.Value.charging ? " (充电中)" : "")}");
                    }
                    await Task.Delay(220);
                }
                log.AppendLine();
            }
            catch (Exception ex)
            {
                log.AppendLine($"  异常 {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                device?.Dispose();
            }
        }

        var text = log.ToString();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pb_opo.txt");
        System.IO.File.WriteAllText(path, text, Encoding.UTF8);
        Console.WriteLine(text);
        Console.WriteLine("[报告已写入] " + path);
    }
}
