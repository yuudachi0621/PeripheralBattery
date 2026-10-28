using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PeripheralBattery.Native;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace PeripheralBattery.Diagnostics;

/// <summary>
/// 耳机专项探针（命令行 --probe-headset）：专门啃「某副蓝牙耳机读不到电量」。
///
/// 思路是把所有可能藏电量的地方都翻一遍，再看哪条路真的能出数：
///   1. 系统层：用 WinRT AEP 查询蓝牙设备，打印它们暴露的电量属性
///      （Windows 设置页里显示的电量就是从这些属性来的，能读到就说明系统已经缓存了）
///   2. BLE：不限 0x180F，做全量服务发现，读取每个可读特征值（厂商私有服务往往就在这里）
///   3. BLE：对已配对设备尝试建立 LE 连接，确认它到底支不支持 BLE
///   4. 经典蓝牙：确认连接状态（A2DP 走的就是这条路，没有标准电量命令）
/// </summary>
internal static class HeadsetProbe
{
    private static readonly System.Text.StringBuilder Sb = new();

    public static async Task RunAsync(string? keyword)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            Log("===== 1. 系统层：蓝牙设备节点与其电量属性 =====");
            DumpSystemBluetoothNodes(keyword);

            Log("\n===== 2. BLE：全量服务 / 特征枚举 =====");
            await DumpBleAsync(keyword);

            Log("\n===== 3. BLE：对未连接的已配对设备强行连接 =====");
            await ForceBleAsync(keyword);

            Log("\n===== 4. 经典蓝牙：连接状态 =====");
            await DumpClassicAsync(keyword);
        }
        catch (Exception ex)
        {
            Log("异常: " + ex);
        }
        finally
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pb_headset.txt");
            System.IO.File.WriteAllText(path, Sb.ToString(), System.Text.Encoding.UTF8);
            Console.WriteLine(Sb.ToString());
            Console.WriteLine("\n[报告已写入] " + path);
        }
    }

    private static bool Match(string? name, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        return name != null && name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- 1. 系统层

    /// <summary>
    /// 枚举所有蓝牙相关设备接口，打印 DEVPKEY_Device_BatteryLevel 等属性。
    /// Windows 设置的「蓝牙和其他设备」里显示的耳机电量，来源就是设备节点的这些属性。
    /// </summary>
    private static void DumpSystemBluetoothNodes(string? keyword)
    {
        // 直接搜蓝牙相关的属性键，把系统里所有带电量/蓝牙属性的设备节点都过一遍
        string[] selectors =
        {
            "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"",   // Bluetooth AEP
            "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"",   // Bluetooth LE AEP
        };

        foreach (var selector in selectors)
        {
            try
            {
                var found = DeviceInformation.FindAllAsync(selector, Props).GetAwaiter().GetResult();
                Log($"\n$ {selector}\n  → {found.Count} 个");
                foreach (var di in found)
                {
                    var isTarget = Match(di.Name, keyword);
                    var hasBattery = di.Properties.Any(p => p.Key.Contains("Battery", StringComparison.OrdinalIgnoreCase)
                                                           || p.Key.Contains("2bd67d8b", StringComparison.OrdinalIgnoreCase));
                    if (!isTarget && !hasBattery) continue;

                    Log($"  · {di.Name}{(di.Pairing.IsPaired ? " [已配对]" : "")}");
                    Log($"     id = {di.Id}");
                    foreach (var pair in di.Properties.Where(p => Interesting(p.Key)))
                        Log($"     {pLabel(pair.Key)} = {pair.Value}");
                }
            }
            catch (Exception ex)
            {
                Log($"  查询失败: {ex.GetType().Name} {ex.Message}");
            }
        }
    }

    private static string pLabel(string key) => key;

    private static readonly string[] PropKeys =
    {
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.IsPaired",
        "System.Devices.Aep.Bluetooth.Cod.Major",
        "System.Devices.Aep.Bluetooth.Cod.Minor",
        "System.Devices.Aep.Bluetooth.Le.IsConnectable",
        "System.Devices.Aep.SignalStrength",
    };

    private static string[] Props => PropKeys;

    private static string? Read(DeviceInformation di, string key)
        => di.Properties.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static bool Interesting(string key)
        => key.Contains("Battery", StringComparison.OrdinalIgnoreCase)
           || key.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
           || key.Contains("Bth", StringComparison.OrdinalIgnoreCase)
           || key.Contains("Container", StringComparison.OrdinalIgnoreCase)
           || key.Contains("2bd67d8b", StringComparison.OrdinalIgnoreCase)
           || key.Contains("10497b1b", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- 2. BLE 全量

    private static async Task DumpBleAsync(string? keyword)
    {
        var infos = await DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));

        Log($"已配对 BLE 设备 {infos.Count} 个");
        foreach (var info in infos)
        {
            if (!Match(info.Name, keyword)) continue;
            await DumpOneAsync(info.Name, info.Id);
        }

        // 已连接的也可能没进"已配对"列表，补一遍
        var connected = await DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
        foreach (var info in connected)
        {
            if (!Match(info.Name, keyword)) continue;
            if (infos.Any(i => i.Id == info.Id)) continue;
            await DumpOneAsync(info.Name, info.Id);
        }
    }

    private static async Task DumpOneAsync(string name, string id)
    {
        BluetoothLEDevice? dev = null;
        try
        {
            dev = await WithTimeout(BluetoothLEDevice.FromIdAsync(id), 6000);
            if (dev == null)
            {
                Log($"\n-- {name}: 连接对象获取超时");
                return;
            }
            Log($"\n-- {name}  状态={dev.ConnectionStatus}  地址=0x{dev.BluetoothAddress:X12}");

            var result = await WithTimeout(dev.GetGattServicesAsync(BluetoothCacheMode.Uncached), 10000);
            if (result == null)
            {
                Log("   服务发现超时");
                return;
            }
            Log($"   服务发现 {result.Status}，共 {result.Services.Count} 个");
            foreach (var service in result.Services)
            {
                Log($"     [{service.Uuid}]");
                var chars = await WithTimeout(service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached), 4000);
                if (chars == null || chars.Status != GattCommunicationStatus.Success)
                {
                    Log("        特征枚举失败");
                    continue;
                }
                foreach (var ch in chars.Characteristics)
                {
                    string text = "";
                    if (ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                    {
                        var rd = await WithTimeout(ch.ReadValueAsync(BluetoothCacheMode.Uncached), 2500);
                        text = rd != null && rd.Status == GattCommunicationStatus.Success
                            ? " = " + Convert.ToHexString(ToArray(rd.Value))
                            : " = (读失败)";
                    }
                    Log($"        {ch.Uuid}  {ch.CharacteristicProperties}{text}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"   异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            dev?.Dispose();
        }
    }

    // ---------------------------------------------------------------- 3. 强行连 BLE

    private static async Task ForceBleAsync(string? keyword)
    {
        var classic = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        foreach (var info in classic)
        {
            if (!Match(info.Name, keyword)) continue;

            BluetoothDevice? bd = null;
            try
            {
                bd = await WithTimeout(BluetoothDevice.FromIdAsync(info.Id), 3000);
                if (bd == null || bd.BluetoothAddress == 0)
                {
                    Log($"{info.Name}: 拿不到地址");
                    continue;
                }
                Log($"\n{info.Name}  经典连接={bd.ConnectionStatus}  地址=0x{bd.BluetoothAddress:X12}");

                var le = await WithTimeout(BluetoothLEDevice.FromBluetoothAddressAsync(bd.BluetoothAddress), 6000);
                if (le == null)
                {
                    Log("  BLE 连接失败（多半是设备根本不广播 LE）");
                    continue;
                }
                Log($"  BLE 连接状态={le.ConnectionStatus}");

                // 保持连接的会话，有时能逼设备把服务表吐出来
                using var session = await GattSession.FromDeviceIdAsync(le.BluetoothDeviceId);
                if (session != null)
                {
                    session.MaintainConnection = true;
                    Log($"  GattSession CanMaintainConnection={session.CanMaintainConnection}");
                    await Task.Delay(1500);
                }

                var svc = await WithTimeout(le.GetGattServicesAsync(BluetoothCacheMode.Uncached), 8000);
                Log(svc == null ? "  服务发现超时" : $"  服务发现 {svc.Status}，{svc.Services.Count} 个");
                if (svc != null)
                {
                    foreach (var s in svc.Services) Log($"     [{s.Uuid}]");
                }
                le.Dispose();
            }
            catch (Exception ex)
            {
                Log($"  异常 {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                bd?.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------- 4. 经典蓝牙

    private static async Task DumpClassicAsync(string? keyword)
    {
        var all = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        foreach (var info in all)
        {
            if (!Match(info.Name, keyword)) continue;
            BluetoothDevice? bd = null;
            try
            {
                bd = await WithTimeout(BluetoothDevice.FromIdAsync(info.Id), 3000);
                if (bd == null) { Log($"{info.Name}: 超时"); continue; }
                Log($"{info.Name}  连接={bd.ConnectionStatus}  地址=0x{bd.BluetoothAddress:X12}");
                Log($"  ClassOfDevice=0x{bd.ClassOfDevice.RawValue:X6} ({bd.ClassOfDevice.MajorClass}/{bd.ClassOfDevice.MinorClass})");
            }
            catch (Exception ex)
            {
                Log($"{info.Name}: {ex.Message}");
            }
            finally
            {
                bd?.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------- 工具

    private static byte[] ToArray(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task<T?> WithTimeout<T>(Windows.Foundation.IAsyncOperation<T> op, int ms) where T : class
    {
        var task = op.AsTask();
        var done = await Task.WhenAny(task, Task.Delay(ms));
        if (done != task) return null;
        try { return await task; } catch { return null; }
    }

    private static void Log(string s) => Sb.AppendLine(s);
}
