using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace PeripheralBattery.Diagnostics;

/// <summary>
/// 经典蓝牙耳机的电量探针（命令行 --probe-hfp [关键字]）。
///
/// 背景：A2DP 耳机没有 0x180F 电池服务，BLE 侧也连不上，但 HFP（免提协议）里
/// 有一条苹果定义的 AT 指令 IPHONEACCEV，用来把电量推给主机：
///     AT+IPHONEACCEV=1,1,5       → 1 个参数，类型 1（电量），档位 5 → 5*10+10 = 60%
/// 很多非苹果的 TWS 耳机也实现了它（因为要兼容 iPhone）。
///
/// 这里做的事：连上 HFP 的 RFCOMM 通道，扮演"免提设备"，把耳机发来的每一行都记下来，
/// 顺便主动问一次 AT+XAPL / AT+IPHONEACCEV，看它会不会回。
/// </summary>
internal static class HfpProbe
{
    private static readonly Guid HandsfreeAudioGateway = new("0000111f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid Handsfree = new("0000111e-0000-1000-8000-00805f9b34fb");

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

                log.AppendLine($"===== {device.Name}  连接={device.ConnectionStatus} =====");
                var result = await device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
                log.AppendLine($"  RFCOMM 服务 {result.Services.Count} 个:");
                foreach (var svc in result.Services)
                    log.AppendLine($"    {svc.ServiceId.Uuid}  {svc.ConnectionServiceName}");

                // 逐个通道都试：标准 HFP 之外，厂商私有通道（99999999 / 0000079a）也可能藏电量
                foreach (var svc in result.Services)
                {
                    log.AppendLine($"  --> 连接 {svc.ServiceId.Uuid}");
                    await TryTalkAsync(svc, log);
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
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pb_hfp.txt");
        System.IO.File.WriteAllText(path, text, Encoding.UTF8);
        Console.WriteLine(text);
        Console.WriteLine("[报告已写入] " + path);
    }

    private static async Task TryTalkAsync(RfcommDeviceService service, StringBuilder log)
    {
        using var socket = new StreamSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        try
        {
            await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName);
            log.AppendLine("      已连接");
        }
        catch (Exception ex)
        {
            log.AppendLine($"      连接失败 {ex.Message}");
            return;
        }

        var buffer = new StringBuilder();

        // 一边读一边回 OK。HFP 里 AT 命令用 \r 结尾，回 "\r\nOK\r\n"。
        var reader = Task.Run(async () =>
        {
            var raw = new byte[512];
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var ibuf = new Windows.Storage.Streams.Buffer(512);
                    var read = await socket.InputStream.ReadAsync(ibuf, 512, InputStreamOptions.Partial);
                    if (read.Length == 0) break;

                    var bytes = new byte[read.Length];
                    DataReader.FromBuffer(read).ReadBytes(bytes);
                    var text = Encoding.UTF8.GetString(bytes);
                    log.AppendLine("      收 < " + text.Replace("\r", "\\r").Replace("\n", "\\n"));
                    buffer.Append(text);

                    // 简单回 OK，让耳机进入下一状态
                    await WriteAsync(socket, "OK");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("      读异常 " + ex.Message);
            }
        });

        // 主动敲几下门，有的耳机要收到 XAPL 才肯报电量
        await Task.Delay(600);
        await WriteAsync(socket, "AT+XAPL=ABCD-1234-0100,10");
        await Task.Delay(500);
        await WriteAsync(socket, "AT+IPHONEACCEV?");
        await Task.Delay(1500);

        await Task.WhenAny(reader, Task.Delay(6000));

        var all = buffer.ToString();
        var level = ParseAccev(all);
        log.AppendLine(level == null ? "      => 没收到 IPHONEACCEV 电量" : $"      => 电量 {level}%");
    }

    private static async Task WriteAsync(StreamSocket socket, string command)
    {
        var text = "\r\n" + command + "\r\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        var output = socket.OutputStream;
        using var writer = new DataWriter(output);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
    }

    /// <summary>从 AT+IPHONEACCEV=count,key,value... 里取电量。</summary>
    internal static int? ParseAccev(string text)
    {
        var index = text.IndexOf("IPHONEACCEV=", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var rest = text[(index + "IPHONEACCEV=".Length)..];
        var end = rest.IndexOfAny(new[] { '\r', '\n' });
        if (end >= 0) rest = rest[..end];

        var parts = rest.Split(',');
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[0], out var count) || count < 1) return null;

        // 参数成对出现：key,value。key=1 表示电量档位（0~9），百分比 = (档位 + 1) * 10
        for (int i = 1; i + 1 < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], out var key) || key != 1) continue;
            if (!int.TryParse(parts[i + 1], out var level)) return null;
            return Math.Clamp((level + 1) * 10, 0, 100);
        }
        return null;
    }
}
