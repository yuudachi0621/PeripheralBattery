using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PeripheralBattery.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace PeripheralBattery.Providers;

/// <summary>
/// 蓝牙 LE 通道：标准电池服务（0x180F）里的电量特征（0x2A19）。
/// 覆盖范围：Xbox 手柄（Series 手柄在 BLE 下自带 0x180F）、大部分 BLE 键盘鼠标、
/// 以及实现了 BAS 的 TWS 耳机。
///
/// 省电做法：只按服务 UUID 查一次电池服务（不做全量服务发现），读完立刻 Dispose 掉
/// BluetoothLEDevice 释放连接，避免一直占着射频链路。
/// </summary>
public sealed class BleProvider : IBatteryProvider
{
    public string Name => "蓝牙 GATT";

    public async Task<ProviderResult> PollAsync(CancellationToken ct)
    {
        var devices = new List<DeviceSnapshot>();
        try
        {
            // 只查"当前已连接"的 BLE 设备：已关闭的设备不会出现在结果里，
            // 也就不需要为它等连接超时（之前一轮要 8 秒就是等出来的）。
            var infos = await DeviceInformation.FindAllAsync(
                BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));

            foreach (var info in infos)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var snapshot = await ReadDeviceAsync(info);
                    if (snapshot != null) devices.Add(snapshot);
                }
                catch
                {
                    // 单台设备失败不影响其它设备
                }
            }
            return new ProviderResult { Ok = true, Devices = devices };
        }
        catch (Exception ex)
        {
            return ProviderResult.Fail(ex.Message);
        }
    }

    private static async Task<DeviceSnapshot?> ReadDeviceAsync(DeviceInformation info)
    {
        BluetoothLEDevice? device = null;
        try
        {
            device = await WinRtAsync.WithTimeout(BluetoothLEDevice.FromIdAsync(info.Id), 4000);
            if (device == null) return null;   // 设备关机 / 不在范围内

            var name = string.IsNullOrWhiteSpace(device.Name) ? info.Name : device.Name;
            if (string.IsNullOrWhiteSpace(name)) return null;

            var services = await WinRtAsync.WithTimeout(
                device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached), 6000);

            if (services == null || services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                return null;   // 没有标准电池服务，交给经典蓝牙通道去显示"在线但无电量"

            int? level = null;

            // 有些 TWS 耳机为左右耳各开一个电池服务，取最小值更符合直觉
            foreach (var service in services.Services)
            {
                var chars = await WinRtAsync.WithTimeout(
                    service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached), 3000);
                if (chars == null || chars.Status != GattCommunicationStatus.Success) continue;

                foreach (var characteristic in chars.Characteristics)
                {
                    var read = await WinRtAsync.WithTimeout(
                        characteristic.ReadValueAsync(BluetoothCacheMode.Uncached), 3000);
                    if (read == null || read.Status != GattCommunicationStatus.Success || read.Value.Length == 0) continue;

                    var value = ReadFirstByte(read.Value);
                    if (value > 100) continue;
                    level = level is null ? value : Math.Min(level.Value, value);
                }
            }

            if (level == null) return null;

            return new DeviceSnapshot
            {
                Id = $"bt:{device.BluetoothAddress:X12}",
                Name = name,
                Kind = DeviceKindGuesser.Guess(name),
                Link = LinkKind.Bluetooth,
                Percent = level,
            };
        }
        finally
        {
            device?.Dispose();   // 尽快断开，别让射频一直连着
        }
    }

    private static byte ReadFirstByte(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes.Length > 0 ? bytes[0] : (byte)0;
    }
}
