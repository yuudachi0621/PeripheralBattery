using System;
using System.Collections.Generic;
using System.Text;

namespace PeripheralBattery.Native;

/// <summary>
/// Logitech HID++ 2.0 的极简实现：只做「按特性号取索引 → 调函数 → 拿回复」。
/// 协议要点：
///   请求 = [0x11, 设备索引, 特性索引, 函数&lt;&lt;4|swId, 参数...]（20 字节长报文）
///   回复 = 同结构，第 3 字节低 4 位回显 swId；若该字节为 0xFF 则是错误报文，第 4 字节是 swId。
/// 设备索引 0xFF 是接收器自身（有线直连设备也用 0xFF），1~6 是接收器下面挂的设备。
/// </summary>
internal sealed class HidppChannel : IDisposable
{
    private readonly HidHandle _dev;
    private byte _swId;

    public byte DeviceIndex { get; }
    public bool LongReports { get; }

    private HidppChannel(HidHandle dev, byte deviceIndex, bool longReports)
    {
        _dev = dev;
        DeviceIndex = deviceIndex;
        LongReports = longReports;
        _swId = 0x05;
    }

    /// <summary>在某个 HID 接口上针对指定设备索引打开一条 HID++ 通道。</summary>
    public static HidppChannel Create(HidHandle dev, byte deviceIndex)
        => new(dev, deviceIndex, longReports: dev.Caps.OutputReportByteLength >= 20);

    /// <summary>
    /// 发一个请求并等回复。返回的 byte[] 是完整报告，参数从下标 4 开始。
    /// 超时或设备不支持该特性时返回 null。
    /// </summary>
    public byte[]? Request(byte featureIndex, byte function, int timeoutMs = 400, params byte[] parameters)
    {
        // swId 在 1..15 之间轮转，避免和上一次请求的迟到回复混淆
        _swId = (byte)((_swId % 15) + 1);
        var swId = _swId;

        var len = LongReports ? 20 : 7;
        var msg = new byte[len];
        msg[0] = (byte)(LongReports ? 0x11 : 0x10);
        msg[1] = DeviceIndex;
        msg[2] = featureIndex;
        msg[3] = (byte)((function << 4) | swId);
        var copy = Math.Min(parameters.Length, len - 4);
        Array.Copy(parameters, 0, msg, 4, copy);

        if (!_dev.Write(msg)) return null;

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var left = (int)(deadline - Environment.TickCount64);
            var resp = _dev.Read(Math.Max(left, 1));
            if (resp == null || resp.Length < 5) continue;
            if (resp[1] != DeviceIndex || resp[2] != featureIndex) continue;

            if (resp[3] == 0xFF)
            {
                if (resp[4] == swId) return null;   // 明确的错误回复（特性不支持等）
                continue;
            }
            if ((resp[3] & 0x0F) == swId) return resp;
        }
        return null;
    }

    /// <summary>
    /// 探活：向 IRoot(0x0000) 请求特性 0x0000 本身，能回来说明这个设备索引存在。
    /// 无线设备从省电状态唤醒时经常把第一个请求吞掉，所以要连发几次（tries）。
    /// </summary>
    public bool Ping(int timeoutMs = 120, int tries = 3)
    {
        for (int i = 0; i < tries; i++)
            if (Request(0x00, 0x00, timeoutMs, 0x00, 0x00) != null) return true;
        return false;
    }

    /// <summary>特性发现：拿某个特性号在本设备上的索引，没有则 null。</summary>
    public byte? FeatureIndex(ushort featureId, int timeoutMs = 300)
    {
        var r = Request(0x00, 0x00, timeoutMs, (byte)(featureId >> 8), (byte)(featureId & 0xFF));
        if (r == null || r[4] == 0) return null;
        return r[4];
    }

    // ---------------- 常用特性 ----------------

    /// <summary>0x0005 DEVICE_NAME：读设备自报的名字。</summary>
    public string? ReadDeviceName()
    {
        var fi = FeatureIndex(0x0005);
        if (fi == null) return null;

        var head = Request(fi.Value, 0x00);
        if (head == null) return null;
        int total = head[4];

        var sb = new StringBuilder();
        for (int offset = 0; offset < total && offset < 64; offset += 16)
        {
            var chunk = Request(fi.Value, 0x01, 300, (byte)offset);
            if (chunk == null) break;
            int n = Math.Min(16, total - offset);
            sb.Append(Encoding.UTF8.GetString(chunk, 4, n));
        }
        var name = sb.ToString().TrimEnd('\0', ' ');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// 0x0002 DEVICE_INFO：返回 (unitId, modelId)。
    /// unitId 对多数设备是唯一序列号，用来做稳定的设备标识（自定义名称才能记住）。
    /// </summary>
    public (uint unitId, ushort modelId)? ReadDeviceInfo()
    {
        var fi = FeatureIndex(0x0002);
        if (fi == null) return null;
        var r = Request(fi.Value, 0x00);
        if (r == null) return null;

        // 应答： [4]=entityCnt, [5..8]=unitId(LE), [9..10]=modelId(LE), [11..12]=extModelId
        uint unitId = (uint)(r[5] | (r[6] << 8) | (r[7] << 16) | (r[8] << 24));
        ushort modelId = (ushort)(r[9] | (r[10] << 8));
        return (unitId, modelId);
    }

    /// <summary>
    /// 读电量。返回 (百分比, 是否在充电, 是否由外部直接供电)；读不到返回 null。
    /// 优先级：0x1004 统一电量（最准，带充电 + 外部供电状态）→ 0x1000 电量百分比 → 0x1001 电压换算。
    /// </summary>
    public (int percent, bool charging, bool externalPower)? ReadBattery()
    {
        // 1) UNIFIED_BATTERY：func1 getStatus → [4]=电量% [5]=原始档位 [6]=充电状态 [7]=外部供电状态
        var fi = FeatureIndex(0x1004);
        if (fi != null)
        {
            var st = Request(fi.Value, 0x01);
            if (st != null && st[4] <= 100)
            {
                var charging = st[6] == 1 || st[6] == 2;   // 1=充电中 2=已充满
                // [7] = 外部供电状态。实测值：
                //   0 = 靠电池供电
                //   1 = 接了 USB 线（有线模式 / 正在充电）
                //   2 = 有线模式下已充满
                // 注意它和 [6] 不是同一件事：插着线但电池已经满了的时候，[6] 可能回到 0（不充电），
                // [7] 仍然是 1/2 —— 正是"要在电量旁显示充电闪电"的那种情况。
                // 电量数字仍然是 [4] 的真实读数，不会因为插了线被改写成 100%。
                var externalPower = st[7] != 0;
                if (st[4] > 0) return (st[4], charging, externalPower);
            }
        }

        // 2) BATTERY_LEVEL_STATUS：func0 → [4]=电量% [5]=状态位（bit1 表示充电中）
        //    这一档没有外部供电状态，只能报 false。
        fi = FeatureIndex(0x1000);
        if (fi != null)
        {
            var st = Request(fi.Value, 0x00);
            if (st != null && st[4] <= 100 && st[4] > 0)
                return (st[4], (st[5] & 0x02) != 0, false);
        }

        // 3) BATTERY_VOLTAGE：func0 → [4..5]=电压毫伏（大端），锂电池经验曲线换算
        fi = FeatureIndex(0x1001);
        if (fi != null)
        {
            var st = Request(fi.Value, 0x00);
            if (st != null)
            {
                var mv = (st[4] << 8) | st[5];
                if (mv > 2500 && mv < 5000) return (VoltageToPercent(mv), false, false);
            }
        }

        return null;
    }

    /// <summary>
    /// 单节锂电池电压 → 百分比，查表线性插值。
    /// 端点取 4.20V 满 / 3.30V 空（3.40V 约 2%，3.30V 才是 0%）——
    /// 对齐手机厂商常用的放电曲线，比"3.4V 就当 0%"更接近实际续航感受。
    /// </summary>
    private static readonly (int mv, int pct)[] LiIonCurve =
    {
        (4200, 100), (4100, 92), (4000, 80), (3900, 66), (3800, 50),
        (3700, 33), (3600, 18), (3500, 8), (3400, 2), (3300, 0),
    };

    public static int VoltageToPercent(int mv)
    {
        if (mv >= LiIonCurve[0].mv) return 100;
        for (int i = 1; i < LiIonCurve.Length; i++)
        {
            var (hiMv, hiPct) = LiIonCurve[i - 1];
            var (loMv, loPct) = LiIonCurve[i];
            if (mv >= loMv)
            {
                var t = (double)(mv - loMv) / (hiMv - loMv);
                return (int)Math.Round(loPct + t * (hiPct - loPct));
            }
        }
        return 0;
    }

    public void Dispose() => _dev.Dispose();
}
