using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace PeripheralBattery.Providers;

/// <summary>
/// OPO 私有协议的收发封装（只读用途：发查询、收回复）。
///
/// 后台任务持续读字节流，按下面的格式拆帧：
///   外层帧  AA | Len(LEB128) | 00 | 00 | Cmd(2) | Seq(1) | PayLen(2) | Payload
/// 其中 Len = 其后所有字节数 = 2 + 内层消息长度；内层消息长度 = 5 + Payload 长度。
/// 回复命令号 = 请求命令号 | 0x8000，用 (Seq, Cmd&amp;0x7FFF) 配对。
/// </summary>
internal sealed class OpoLink : IDisposable
{
    private readonly StreamSocket _socket;
    private readonly Dictionary<(byte seq, ushort cmd), TaskCompletionSource<byte[]>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private byte _seq;

    public OpoLink(StreamSocket socket) => _socket = socket;

    public void Start() => _ = Task.Run(ReadLoopAsync);

    private async Task ReadLoopAsync()
    {
        var buffer = new List<byte>(512);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var ibuf = new Windows.Storage.Streams.Buffer(1024);
                var read = await _socket.InputStream.ReadAsync(ibuf, 1024, InputStreamOptions.Partial);
                if (read.Length == 0) break;

                var chunk = new byte[read.Length];
                DataReader.FromBuffer(read).ReadBytes(chunk);
                buffer.AddRange(chunk);

                while (TryTakeFrame(buffer, out var cmd, out var seq, out var payload))
                {
                    if (seq == 0xFF) continue;   // 耳机主动推送，这里不需要
                    if (_pending.TryGetValue((seq, (ushort)(cmd & 0x7FFF)), out var tcs))
                        tcs.TrySetResult(payload);
                }
            }
            catch
            {
                break;   // 设备断开 / 关盖都会走到这里，静默退出
            }
        }

        foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
        _pending.Clear();
    }

    /// <summary>从缓冲区里取出一个完整帧，顺便解析出命令号与载荷。</summary>
    private static bool TryTakeFrame(List<byte> buffer, out ushort cmd, out byte seq, out byte[] payload)
    {
        cmd = 0;
        seq = 0;
        payload = Array.Empty<byte>();

        while (true)
        {
            if (buffer.Count < 2) return false;

            // 找帧头；找不到就把缓冲区清空（流已经乱了，留着没用）
            var start = buffer.IndexOf(0xAA);
            if (start < 0) { buffer.Clear(); return false; }
            if (start > 0) buffer.RemoveRange(0, start);

            // LEB128 长度
            int len = 0, shift = 0, pos = 1;
            bool more = true;
            while (more)
            {
                if (pos >= buffer.Count) return false;   // 长度字段还没收全
                var b = buffer[pos++];
                len |= (b & 0x7F) << shift;
                shift += 7;
                more = (b & 0x80) != 0;

                if (shift > 21)   // 长度编码明显异常，丢掉这个 AA 重新找
                {
                    buffer.RemoveAt(0);
                    break;
                }
            }
            if (more) continue;

            if (len < 7 || len > 4096)   // 7 = 2(切片/保留) + 5(消息头)
            {
                buffer.RemoveAt(0);
                continue;
            }
            if (buffer.Count < pos + len) return false;   // 帧还没收全

            // pos 起：00(整包) 00(保留) Cmd Seq PayLen Payload
            var body = buffer.GetRange(pos, len);
            buffer.RemoveRange(0, pos + len);

            cmd = (ushort)(body[2] | (body[3] << 8));
            seq = body[4];
            var payLen = body[5] | (body[6] << 8);
            var available = body.Count - 7;
            var take = Math.Clamp(payLen, 0, Math.Max(available, 0));
            payload = body.GetRange(7, take).ToArray();
            return true;
        }
    }

    /// <summary>发一条查询并等回复；超时返回 null。</summary>
    public async Task<byte[]?> QueryAsync(ushort cmd, int timeoutMs, CancellationToken ct)
    {
        var seq = NextSeq();
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[(seq, cmd)] = tcs;

        try
        {
            await SendAsync(cmd, seq, Array.Empty<byte>());

            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct));
            return done == tcs.Task ? tcs.Task.Result : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _pending.Remove((seq, cmd));
        }
    }

    private byte NextSeq() => _seq = _seq >= 0xFE ? (byte)1 : (byte)(_seq + 1);

    private async Task SendAsync(ushort cmd, byte seq, byte[] data)
    {
        // 内层消息
        var message = new byte[5 + data.Length];
        message[0] = (byte)(cmd & 0xFF);
        message[1] = (byte)(cmd >> 8);
        message[2] = seq;
        message[3] = (byte)(data.Length & 0xFF);
        message[4] = (byte)(data.Length >> 8);
        data.CopyTo(message, 5);

        // 外层帧：AA | LEB128(2 + 消息长度) | 00 | 00 | 消息
        var frame = new List<byte> { 0xAA };
        var len = 2 + message.Length;
        while (true)
        {
            var b = (byte)(len & 0x7F);
            len >>= 7;
            if (len > 0) { frame.Add((byte)(b | 0x80)); continue; }
            frame.Add(b);
            break;
        }
        frame.Add(0x00);
        frame.Add(0x00);
        frame.AddRange(message);

        var output = _socket.OutputStream;
        using var writer = new DataWriter(output);
        writer.WriteBytes(frame.ToArray());
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
    }

    /// <summary>
    /// 解析 0x8106 电量回复：状态(1) + count(1) + (part, raw) × count。
    /// 有耳机就取耳机里的最低值（左右耳差得多时，低的那个才是真续航），
    /// 只有充电盒在线时才用盒子的电量。
    /// </summary>
    public static (int percent, bool charging)? PickLevel(byte[] payload)
    {
        if (payload.Length < 2) return null;

        int count = payload[1];
        if (2 + count * 2 > payload.Length) return null;

        int? buds = null;
        int? caseLevel = null;
        bool charging = false;

        for (int i = 0; i < count; i++)
        {
            var part = payload[2 + i * 2];
            var raw = payload[3 + i * 2];
            var level = raw & 0x7F;
            if (level == 0) continue;   // 0 表示"未知"，关盖时的盒子经常这样

            if (part is 1 or 2)
            {
                buds = buds is null ? level : Math.Min(buds.Value, level);
                charging |= (raw & 0x80) != 0;
            }
            else if (part == 3)
            {
                caseLevel = level;
            }
        }

        if (buds is int b) return (b, charging);
        if (caseLevel is int c) return (c, false);
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
        _pending.Clear();
    }
}
