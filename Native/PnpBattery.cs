using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PeripheralBattery.Native;

/// <summary>
/// 用 SetupAPI 读蓝牙设备节点上的电池属性（系统层缓存）。
///
/// 为什么需要它：有些蓝牙外设（实测机械师 K600）不提供任何标准电量通道 ——
/// 没有 BLE 电池服务 0x180F、HID 报告里也没有 Battery Strength 用法，
/// 但**蓝牙栈可能把电量百分比写到设备节点的 PnP 属性上**（Windows 设置页的电量就来自这一层）：
///   fmtid {2bd67d8b-8beb-48d5-87e0-6cda3428040a}（设备电池属性组）
///     pid=2  DEVPKEY_Device_BatteryLevel —— 电量百分比（只有 BYTE 型才有意义）
///
/// ⚠️ 这个 fmtid 下还有一个 pid=4 的属性（BOOLEAN，语义上像"充电中"），
/// **实测 K600 在完全没插线、纯蓝牙连接时它也是 True** —— 恒为真，与物理插线无关。
/// 早先版本拿它推断"USB 供电"，结果拔了线还显示"充电中"，已被用户投诉并移除。
/// 判断有线/供电只认「USB 里有没有这台设备的直连接口」，见 BatteryMonitor.ApplyWiredConnections。
///
/// 注意这些属性是系统缓存：设备状态变化时蓝牙栈会更新它，
/// 轮询间隔（默认 60 秒）远大于更新延迟，读它足够新鲜。
/// 属性不存在 / 类型不对时一律当"没有该信息"，绝不猜。
/// </summary>
internal static class PnpBattery
{
    private const int DIGCF_PRESENT = 0x02;

    /// <summary>蓝牙设备类（经典蓝牙的 devnode 都挂在这个类下）。</summary>
    private static readonly Guid BluetoothClass = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");

    /// <summary>设备电池属性组（DEVPKEY_Device_BatteryLevel 所在的 fmtid）。</summary>
    private static readonly Guid BatteryKey = new("2bd67d8b-8beb-48d5-87e0-6cda3428040a");

    /// <summary>
    /// 读所有已存在的蓝牙设备节点的电量百分比。
    /// 返回：蓝牙地址 → 电量。只有"属性存在且是 BYTE 型且值在 1~100"的设备才会出现在结果里。
    /// </summary>
    public static Dictionary<ulong, byte> ReadBluetoothLevels()
    {
        var result = new Dictionary<ulong, byte>();
        try
        {
            var guid = BluetoothClass;
            var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    var instanceId = InstanceId(set, ref data);
                    var address = ExtractAddress(instanceId);
                    if (address == null || result.ContainsKey(address.Value)) continue;

                    var level = ReadLevel(set, ref data);
                    if (level != null) result[address.Value] = level.Value;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }
        catch
        {
            // PnP 读取失败不影响其它通道
        }
        return result;
    }

    /// <summary>
    /// 从 BTHENUM\DEV_A1B2C3D4E5F6\7&… 里提取蓝牙地址（此处地址为示例）。
    /// 地址是 12 位大写十六进制；解析失败返回 null（非蓝牙 devnode 不该出现在这个类里，但防御一下）。
    /// </summary>
    private static ulong? ExtractAddress(string instanceId)
    {
        var at = instanceId.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var hex = instanceId.Substring(at + 4, Math.Min(12, instanceId.Length - at - 4));
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var addr) && addr != 0 ? addr : null;
    }

    /// <summary>
    /// 读电量百分比（pid=2）。只认 BYTE 型 —— 有些设备上这个属性存在但类型是 FILETIME
    /// （实测 K600 就是 16 字节全零），那种当"没有电量"，不要硬解析。
    /// </summary>
    private static byte? ReadLevel(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        if (!TryGetProperty(set, ref data, BatteryKey, 2, out var type, out var buf)) return null;
        if (type != DEVPROP_TYPE_BYTE || buf.Length < 1) return null;
        return buf[0] is > 0 and <= 100 ? buf[0] : null;
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    private const int DEVPROP_TYPE_BYTE = 0x03;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data,
        StringBuilder? buffer, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data,
        ref DEVPROPKEY key, out int type, byte[]? buffer, int size, out int required, int flags);

    /// <summary>
    /// 读一个属性，两段式：先问长度，再按长度取。
    /// 第一次调用时 SetupDiGetDevicePropertyW 返回 FALSE 有两种含义，靠 GetLastError 区分：
    ///   · 122（ERROR_INSUFFICIENT_BUFFER）= 属性存在，required 里是所需字节数 → 继续取；
    ///   · 1168（ERROR_NOT_FOUND）等其它值 = 属性不存在 → 直接放弃。
    /// </summary>
    private static bool TryGetProperty(IntPtr set, ref SP_DEVINFO_DATA data, Guid fmtid, uint pid,
        out int type, out byte[] buffer)
    {
        type = 0;
        buffer = Array.Empty<byte>();
        var key = new DEVPROPKEY { fmtid = fmtid, pid = pid };

        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out type, null, 0, out var required, 0))
        {
            if (Marshal.GetLastWin32Error() != 122) return false;   // 不是"缓冲区不够"= 属性不存在
        }
        if (required <= 0 || required > 64) return false;

        buffer = new byte[required];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buffer, required, out _, 0);
    }

    private static string InstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        SetupDiGetDeviceInstanceIdW(set, ref data, null, 0, out var required);
        if (required <= 0) return "";
        var sb = new StringBuilder(required + 1);
        return SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out _) ? sb.ToString() : "";
    }
}
