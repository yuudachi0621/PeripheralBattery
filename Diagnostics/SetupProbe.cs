using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PeripheralBattery.Diagnostics;

/// <summary>
/// 直接用 SetupAPI 读设备属性（命令行 --probe-setup [关键字]）。
///
/// 为什么不用 WinRT 的 DeviceInformation：它的属性过滤语法对自定义 DEVPKEY 支持很差，
/// 而 Windows 设置页给蓝牙耳机显示的电量，来源正是设备节点上的
/// DEVPKEY_Device_BatteryLevel（{2bd67d8b-...}, pid=2）/ BatteryLevel2（pid=3）。
/// 这两个属性存在就说明系统已经缓存了电量，我们有机会直接拿来用。
///
/// 注意：对不存在的属性调用 SetupDiGetDevicePropertyW 会返回 FALSE，
/// GetLastError 可能是 1168（找不到元素）而不是 122（缓冲区不够），两种都要当"不存在"处理。
/// </summary>
internal static class SetupProbe
{
    private const int DIGCF_PRESENT = 0x02;
    private const int SPDRP_DEVICEDESC = 0x00;
    private const int SPDRP_FRIENDLYNAME = 0x0C;

    // 三种蓝牙设备类 GUID：经典、BLE、音频端点
    private static readonly Guid[] Classes =
    {
        new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974"),   // Bluetooth
        new("781aee18-7733-4ce4-ad41-55c31374c07b"),   // Bluetooth LE
        new("4d36e96c-e325-11ce-bfc1-08002be10318"),   // Media / 音频端点
    };

    private static readonly Guid BatteryKey = new("2bd67d8b-8beb-48d5-87e0-6cda3428040a");
    private static readonly Guid BluetoothKey = new("10497b1b-ba51-44e5-8318-a65c837b6661");

    public static void Run(string? keyword)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var sb = new StringBuilder();
        var seen = new HashSet<string>();

        foreach (var classGuid in Classes)
        {
            sb.AppendLine($"===== 设备类 {classGuid} =====");
            foreach (var dev in Enumerate(classGuid))
            {
                if (!seen.Add(dev.InstanceId)) continue;
                if (!string.IsNullOrWhiteSpace(keyword)
                    && !dev.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;

                var props = ReadProps(dev.Set, dev.Data);
                sb.AppendLine($"· {dev.Name}");
                sb.AppendLine($"    {dev.InstanceId}");
                foreach (var (label, value) in props) sb.AppendLine($"    {label} = {value}");
                if (props.Count == 0) sb.AppendLine("    （没有电量相关属性）");
            }
            sb.AppendLine();
        }

        var text = sb.ToString();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pb_setup.txt");
        System.IO.File.WriteAllText(path, text, Encoding.UTF8);
        Console.WriteLine(text);
        Console.WriteLine("[报告已写入] " + path);
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

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data,
        int property, out int type, byte[]? buffer, int size, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data,
        StringBuilder? buffer, int size, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data,
        ref DEVPROPKEY key, out int type, byte[]? buffer, int size, out int required, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    private sealed class Dev
    {
        public required string Name { get; init; }
        public required string InstanceId { get; init; }
        public required IntPtr Set { get; init; }
        public required SP_DEVINFO_DATA Data { get; init; }
    }

    /// <summary>注意：这里不释放 set，由调用方在读属性后统一释放（Dev 里带着句柄）。</summary>
    private static List<Dev> Enumerate(Guid classGuid)
    {
        var result = new List<Dev>();
        var set = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var name = RegString(set, ref data, SPDRP_FRIENDLYNAME)
                           ?? RegString(set, ref data, SPDRP_DEVICEDESC) ?? "(无名)";
                result.Add(new Dev { Name = name, InstanceId = Instance(set, ref data), Set = set, Data = data });
            }
        }
        finally
        {
            // 读属性在 Run 里已经完成，这里可以安全释放
            // （Dev 只在 foreach 内部被立即使用，不跨迭代保存）
        }
        return result;
    }

    private static List<(string, string)> ReadProps(IntPtr set, SP_DEVINFO_DATA data)
    {
        var list = new List<(string, string)>();
        Try(list, set, ref data, BatteryKey, 2, "BatteryLevel");
        Try(list, set, ref data, BatteryKey, 3, "BatteryLevel2");
        Try(list, set, ref data, BatteryKey, 4, "BatteryChargingState");
        for (uint pid = 2; pid <= 10; pid++)
            Try(list, set, ref data, BluetoothKey, pid, $"Bluetooth#{pid}");
        return list;
    }

    private static void Try(List<(string, string)> list, IntPtr set, ref SP_DEVINFO_DATA data,
        Guid fmtid, uint pid, string label)
    {
        var key = new DEVPROPKEY { fmtid = fmtid, pid = pid };

        // 第一次只问长度。属性不存在时返回 false，此时不该当成"需要缓冲区"
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out var type, null, 0, out var required, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != 122) return;   // 122 = ERROR_INSUFFICIENT_BUFFER
        }
        if (required <= 0 || required > 8192) return;

        var buffer = new byte[required];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buffer, required, out _, 0)) return;
        list.Add((label, Format(type, buffer)));
    }

    private static string Format(int type, byte[] buffer)
    {
        // 只换行读真正有意义的类型；其余原样打印，便于人工判断
        try
        {
            return type switch
            {
                0x12 => Encoding.Unicode.GetString(buffer).TrimEnd('\0'),   // STRING
                0x13 => Encoding.Unicode.GetString(buffer).TrimEnd('\0'),   // STRING_LIST
                0x03 => $"byte {buffer[0]}",                                 // BYTE
                0x04 => $"ushort {BitConverter.ToUInt16(buffer, 0)}",        // UINT16
                0x05 => $"uint {BitConverter.ToUInt32(buffer, 0)}",          // UINT32
                0x07 => $"bool {BitConverter.ToBoolean(buffer, 0)}",         // BOOLEAN
                0x11 => Convert.ToHexString(buffer),                         // BINARY
                _ => $"type=0x{type:X2} {Convert.ToHexString(buffer)}",
            };
        }
        catch
        {
            return $"type=0x{type:X2} {Convert.ToHexString(buffer)}";
        }
    }

    private static string? RegString(IntPtr set, ref SP_DEVINFO_DATA data, int property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out var required);
        if (required <= 0) return null;
        var buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, required, out _)) return null;
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static string Instance(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        SetupDiGetDeviceInstanceIdW(set, ref data, null, 0, out var required);
        if (required <= 0) return "";
        var sb = new StringBuilder(required + 1);
        return SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out _) ? sb.ToString() : "";
    }
}
