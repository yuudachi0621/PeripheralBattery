using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeripheralBattery.Models;
using PeripheralBattery.Native;

namespace PeripheralBattery.Providers;

/// <summary>
/// 罗技 HID++ 2.0 通道，主要覆盖 Lightspeed / Unifying 等 2.4G 设备；
/// 同一套 HID++ 接口也可能枚举到直连设备。
/// 实测（GPW2 / PRO X SUPERLIGHT 2 SE）：
///   · 能说话的接口是 usage page 0xFF00、输入输出报告都是 20 字节的那个；
///   · 在这台被测接收器上，接收器自身（索引 0xFF）不响应 HID++，真正的设备在索引 0x01；
///   · 该鼠标不支持 0x1000/0x1001，只支持 0x1004 统一电量。
/// </summary>
public sealed class LogitechProvider : IBatteryProvider
{
    private const ushort LogitechVid = 0x046D;
    private const ushort VendorUsagePage = 0xFF00;

    public string Name => "罗技 2.4G";

    /// <summary>上次成功读到的设备索引。下次优先按它去问，避免每次全量扫描。</summary>
    private readonly HashSet<byte> _knownIndices = new();

    /// <summary>
    /// 常驻的接口句柄。为什么要一直开着：实测刚打开接口的瞬间发请求，接收器经常收不到回复
    /// （无线设备正在从省电状态唤醒），要反复唤醒一段时间才稳定。复用句柄可以只付一次代价，
    /// 之后每轮轮询都是"热"的，几十毫秒就能读完。
    /// </summary>
    private HidHandle? _handle;
    private string? _handlePath;

    public Task<ProviderResult> PollAsync(CancellationToken ct) => Task.Run(() => Poll(ct), ct);

    private ProviderResult Poll(CancellationToken ct)
    {
        try
        {
            // 优先挑长报文接口（HID++ 2.0 走 20 字节报告），短报文接口只作兜底
            var candidates = Hid.Enumerate()
                .Where(i => i.VendorId == LogitechVid && i.UsagePage == VendorUsagePage)
                .OrderByDescending(i => i.OutputLength >= 20)
                .ThenByDescending(i => i.OutputLength)
                .ToList();

            if (candidates.Count == 0)
            {
                DropHandle();   // 没有可用的 HID++ 接口：接收器拔出或设备未暴露
                return new ProviderResult { Ok = true };
            }

            // 接口路径变了（拔插过接收器）就把旧句柄放掉
            if (_handle != null && (_handlePath == null || !candidates.Any(c => c.Path == _handlePath)))
                DropHandle();

            // 1) 复用上次那个接口句柄（热状态）
            if (_handle != null && _handlePath != null)
            {
                var devices = ScanChannels(_handle, ct);
                if (devices.Count > 0) return new ProviderResult { Ok = true, Devices = devices };
                DropHandle();   // 复用的句柄读不到东西，可能接口被系统重置过
            }

            // 2) 新开候选接口。接收器从省电状态唤醒需要时间，刚打开时的请求经常石沉大海，
            //    所以主接口多试几轮（300ms 间隔），备用接口试一次就够了。
            for (int c = 0; c < candidates.Count; c++)
            {
                if (ct.IsCancellationRequested) break;

                var info = candidates[c];
                var handle = HidHandle.Open(info.Path);
                if (handle == null) continue;

                int attempts = c == 0 ? 3 : 1;
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    if (ct.IsCancellationRequested) break;

                    var devices = ScanChannels(handle, ct);
                    if (devices.Count > 0)
                    {
                        _handle = handle;      // 成功了就常驻，后续轮询直接复用这个"热"接口
                        _handlePath = info.Path;
                        return new ProviderResult { Ok = true, Devices = devices };
                    }
                    if (attempt + 1 < attempts) Thread.Sleep(300);
                }

                handle.Dispose();
            }

            return new ProviderResult { Ok = true };   // 接口存在，但没有可识别出电量的设备
        }
        catch (Exception ex)
        {
            return ProviderResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 扫描设备索引，收集在线设备的电量。分两段：
    ///   1. 先按**上次已知在线**的索引、用长超时（700ms）问一遍 ——
    ///      无线设备休眠后首次唤醒要几百毫秒，直接给 90ms 会把正常设备误判成掉线；
    ///   2. 再用短超时（90ms，接收器自己用 0xFF 走 150ms）扫剩下的索引，
    ///      用来发现"新配对/刚插上的设备"——它们大概率是醒着的，短超时足够。
    /// 更进一步的"主接口整体超时后重试"在 <see cref="PollAsync"/> 外层，不在这里。
    /// </summary>
    private List<DeviceSnapshot> ScanChannels(HidHandle handle, CancellationToken ct)
    {
        var devices = new List<DeviceSnapshot>();
        var alive = new HashSet<byte>();

        foreach (var index in _knownIndices.OrderBy(i => i).ToList())
            TryReadChannel(handle, index, 700, devices, alive, ct);

        foreach (byte index in Indexes)
        {
            if (alive.Contains(index)) continue;
            TryReadChannel(handle, index, index == 0xFF ? 150 : 90, devices, alive, ct);
        }

        // 索引可能因为重新配对而变，这里以本轮结果为准
        _knownIndices.Clear();
        foreach (var index in alive) _knownIndices.Add(index);

        return devices;
    }

    private static readonly byte[] Indexes = { 0xFF, 1, 2, 3, 4, 5, 6 };

    private void DropHandle()
    {
        _handle?.Dispose();
        _handle = null;
        _handlePath = null;
    }

    private static void TryReadChannel(HidHandle handle, byte index, int pingTimeoutMs,
        List<DeviceSnapshot> devices, HashSet<byte> alive, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        var channel = HidppChannel.Create(handle, index);
        if (!channel.Ping(pingTimeoutMs)) return;
        alive.Add(index);

        var battery = channel.ReadBattery();
        var name = channel.ReadDeviceName();

        // 接收器本身没有电量特性：读不到电量且名字像接收器，就跳过
        if (battery == null && (string.IsNullOrEmpty(name) || LooksLikeReceiver(name))) return;

        var info = channel.ReadDeviceInfo();
        var displayName = string.IsNullOrWhiteSpace(name) ? "罗技设备" : name!;

        devices.Add(new DeviceSnapshot
        {
            // 有序列号就用序列号（换 USB 口不丢自定义名称），否则退回型号名
            Id = info is { } fi && fi.unitId > 0 ? $"logi:{fi.unitId:X8}" : $"logi:{displayName}",
            Name = displayName,
            Kind = DeviceKindGuesser.Guess(name),
            // HID++ 路径本身无法可靠区分接收器与直连；统一显示 2.4G，
            // 有线供电状态由 ExternalPower / USB 兜底表达。
            Link = LinkKind.Receiver24G,
            Percent = battery?.percent,
            Charging = battery?.charging ?? false,
            // 0x1004 的 [7]：设备由 USB 线直接供电（有线模式 / 插着线充电）。
            // 透传给 Monitor 后，界面会在电量旁显示一个充电图标 ——
            // 电量数字仍然是 [4] 里的真实读数，不会被改写。
            ExternalPower = battery?.externalPower ?? false,
            Note = battery == null ? "未提供电量接口" : null,
        });
    }

    private static bool LooksLikeReceiver(string name)
        => name.Contains("Receiver", StringComparison.OrdinalIgnoreCase) || name.Contains("接收器");
}
