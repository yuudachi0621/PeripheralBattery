using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBattery.Native;

/// <summary>一个 HID 接口的基本信息。</summary>
internal sealed class HidInfo
{
    public string Path = "";

    /// <summary>厂商号：罗技 0x046D、机械师键盘接收器 0x048D。</summary>
    public ushort VendorId;
    public ushort ProductId;
    public ushort VersionNumber;

    /// <summary>usage page：0xFF00 表示厂商自定义（HID++ 就走这个），0x01 是通用桌面设备。</summary>
    public ushort UsagePage;
    public ushort Usage;

    /// <summary>输入报告字节数。HID++ 长报文通常为 20 字节，Provider 还会计较输出报告长度。</summary>
    public int InputLength;
    public int OutputLength;
    public string Product = "";

    /// <summary>VID+PID 拼的短标识，日志里用。注意它不是唯一键（同一型号多个接口会重名）。</summary>
    public string Key => $"VID_{VendorId:X4}&PID_{ProductId:X4}";
    public override string ToString()
        => $"{Key} usage={UsagePage:X4}/{Usage:X4} in={InputLength} out={OutputLength} {Product}";
}

/// <summary>
/// 只封装本工具用得到的那部分 Windows HID 接口：
/// 枚举接口、打开设备、带超时的读写。没有第三方依赖，进程里也不用加载额外 DLL。
///
/// 枚举走的是 SetupAPI 那套（SP_DEVICE_INTERFACE_DATA → 接口详细路径）。
/// 之所以不用更简单的 HidD_GetHidGuid + 枚举，是因为接口路径才是 CreateFile 需要的入参，
/// 而路径里带着接口实例标识，能区分同一个接收器上的多个接口。
/// </summary>
internal static class Hid
{
    /// <summary>只要当前已插入的设备（不列历史幽灵设备）。</summary>
    private const uint DIGCF_PRESENT = 0x02;
    /// <summary>枚举的是"设备接口"而不是设备节点。</summary>
    private const uint DIGCF_DEVICEINTERFACE = 0x10;

    /// <summary>HID 设备接口类 GUID，固定值，所有 HID 设备都挂在这个类下面。</summary>
    private static readonly Guid GuidDevInterfaceHid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    /// <summary>SP_DEVICE_INTERFACE_DATA。cbSize 必须填，否则 API 直接返回失败。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfoSet, IntPtr devInfo,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devInfoSet, ref SpDeviceInterfaceData data,
        IntPtr detail, uint detailSize, out uint requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfoSet);

    /// <summary>
    /// 枚举当前系统里所有 HID 接口。
    /// 走的套路是 SetupAPI 的标准三段式：
    ///   SetupDiGetClassDevs 拿到一个"设备信息集" → 按索引逐个 SetupDiEnumDeviceInterfaces
    ///   → 对每个接口调两次 SetupDiGetDeviceInterfaceDetail（第一次问需要多大，第二次才真取）。
    /// 最后用 HidD_* 把 VID/PID/usage/报告长度读出来，才算拿到一条可用信息。
    /// 打不开的接口（被独占、无权限）直接跳过，不影响其它设备。
    /// </summary>
    public static List<HidInfo> Enumerate()
    {
        var result = new List<HidInfo>();
        var guid = GuidDevInterfaceHid;
        var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        // INVALID_HANDLE_VALUE 是 -1，不是 0；两个都要判，否则后面会拿无效句柄硬调
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

        try
        {
            var did = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out var need, IntPtr.Zero);
                if (need == 0) continue;

                var buf = Marshal.AllocHGlobal((int)need);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize：x64 = 8，x86 = 6（含 2 字节的 Path[1]）
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref did, buf, need, out _, IntPtr.Zero)) continue;

                    var path = Marshal.PtrToStringUni(buf + 4);
                    if (string.IsNullOrEmpty(path)) continue;

                    // 能枚举到不等于能打开：被别的程序独占的接口会在这里失败，跳过即可
                    var info = Describe(path);
                    if (info != null) result.Add(info);
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static HidInfo? Describe(string path)
    {
        using var dev = HidHandle.Open(path, write: false);
        if (dev == null) return null;

        var a = dev.Attributes;
        var c = dev.Caps;
        return new HidInfo
        {
            Path = path,
            VendorId = a.VendorID,
            ProductId = a.ProductID,
            VersionNumber = a.VersionNumber,
            UsagePage = c.UsagePage,
            Usage = c.Usage,
            InputLength = c.InputReportByteLength,
            OutputLength = c.OutputReportByteLength,
            Product = dev.GetProductString(),
        };
    }
}

/// <summary>
/// 带超时的 HID 句柄。用 Overlapped I/O 实现超时，
/// 这样设备拔掉/不回应时不会把线程卡死（同步 ReadFile 会永久阻塞）。
///
/// 为什么要自己写这一层：HID 设备（尤其是无线接收器）在最糟的情况下，
/// 一次 ReadFile 能一直挂着不返回。而我们要在轮询线程里按秒级节奏工作，
/// 一旦卡住就是整轮轮询停摆、托盘数字不动。所以这里统一用
/// CreateFile(FILE_FLAG_OVERLAPPED) + 事件等待 + CancelIoEx 兜住超时。
///
/// 契约：Read 超时返回 null，Write 失败返回 false，都不抛异常 —— 调用方按"没读到"处理。
/// </summary>
internal sealed class HidHandle : IDisposable
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    /// <summary>允许别的程序同时读这个接口（键盘/鼠标本体可能正被系统占用）。</summary>
    private const uint FILE_SHARE_READ_WRITE = 0x03;
    private const uint OPEN_EXISTING = 3;
    /// <summary>异步 I/O 的关键标志，没有它就实现不了超时。</summary>
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_IO_PENDING = 997;
    private const int ERROR_OPERATION_ABORTED = 995;
    private const uint WAIT_OBJECT_0 = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct HiddAttributes
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        // 17 个 ushort 的保留区，逐字段展开以保证布局与 C 结构体完全一致
        public ushort R0, R1, R2, R3, R4, R5, R6, R7, R8, R9, R10, R11, R12, R13, R14, R15, R16;
        public uint NumberLinkCollectionNodes;
        public uint NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public uint NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public uint NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, int toRead, out int read,
        ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, int toWrite, out int written,
        ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle h, ref NativeOverlapped overlapped,
        out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr sa, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HiddAttributes attributes);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsed, ref HidpCaps caps);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int bufferLength);

    // Feature Report 是"设备主动定义的一组可读写的寄存器"，不走输入/输出报告通道。
    // 很多厂商把电池电量、充电状态藏在自定义的 Feature Report 里 —— 这是逆向私有协议的正门。
    // 注意 HidD_GetFeature 是同步调用，设备不响应时可能阻塞；因此只用于诊断探针，
    // 不放进正常轮询路径。
    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] reportBuffer, int reportBufferLength);

    public string Path { get; }
    public HiddAttributes Attributes { get; }
    public HidpCaps Caps { get; }

    /// <summary>裸句柄，仅供诊断模式发 IOCTL 用。</summary>
    internal SafeFileHandle RawHandle => _handle;

    private readonly SafeFileHandle _handle;

    /// <summary>
    /// 打开句柄的同时把属性（VID/PID）和能力（usage、报告长度）一起读出来缓存住。
    /// 这些值在句柄生命周期内不会变，每次用时再调一次 API 纯属浪费。
    /// </summary>
    private HidHandle(SafeFileHandle handle, string path)
    {
        _handle = handle;
        Path = path;

        var a = new HiddAttributes { Size = (uint)Marshal.SizeOf<HiddAttributes>() };
        HidD_GetAttributes(handle, ref a);
        Attributes = a;

        var caps = new HidpCaps();
        if (HidD_GetPreparsedData(handle, out var pp))
        {
            HidP_GetCaps(pp, ref caps);
            HidD_FreePreparsedData(pp);
        }
        Caps = caps;
    }

    /// <summary>
    /// 打开 HID 接口；write=false 时只读（某些键盘接口不允许写）。
    ///
    /// 要写权限的接口经常被系统或驱动以独占方式打开，直接失败。
    /// 这时退回"只读"再试一次：Feature Report 读取通常仍可用，
    /// 但 HID++ 需要用输出报告发请求，拿不到写权限时这条通道无法完成轮询。
    /// </summary>
    public static HidHandle? Open(string path, bool write = true)
    {
        var access = write ? GENERIC_READ | GENERIC_WRITE : GENERIC_READ;
        var h = CreateFileW(path, access, FILE_SHARE_READ_WRITE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);

        if (h.IsInvalid && write)
        {
            // 写权限被独占占用时，退回只读再试一次
            h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ_WRITE, IntPtr.Zero,
                OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        }
        if (h.IsInvalid) return null;
        return new HidHandle(h, path);
    }

    public string GetProductString() => GetString(HidD_GetProductString);

    /// <summary>
    /// 读一个 Feature Report。buffer[0] 填 reportId，返回的数组是完整报告（含 ID 在 [0]）。
    /// 设备不支持该 ID 或读取失败返回 null。该调用只读取报告、不修改设备状态，
    /// 但可能阻塞，因此只用于诊断流程中的全量扫描。
    /// </summary>
    public byte[]? GetFeature(byte reportId)
    {
        // Caps.FeatureReportByteLength 是描述符里声明的最大长度；至少给 2 字节
        //（1 字节 ID + 1 字节数据），避免长度 0 的接口上分配出无效缓冲
        var len = Math.Max((int)Caps.FeatureReportByteLength, 2);
        var buf = new byte[len];
        buf[0] = reportId;
        try
        {
            return HidD_GetFeature(_handle, buf, buf.Length) ? buf : null;
        }
        catch
        {
            return null;
        }
    }

    private string GetString(Func<SafeFileHandle, byte[], int, bool> api)
    {
        var buf = new byte[256];
        if (!api(_handle, buf, buf.Length)) return "";
        var len = 0;
        while (len + 1 < buf.Length && (buf[len] != 0 || buf[len + 1] != 0)) len += 2;
        return System.Text.Encoding.Unicode.GetString(buf, 0, len).Trim();
    }

    /// <summary>
    /// 写一个输出报告（数组第 0 字节是报告 ID）。
    ///
    /// 流程：先建事件 → 发重叠写 → 若返回 ERROR_IO_PENDING 说明在异步排队，
    /// 就等事件（1 秒）。等不到就 CancelIoEx 把这次 I/O 作废并**等它真正结束**，
    /// 之后才能释放缓冲区 —— 否则内核还在往已释放的内存里写，进程会崩。
    /// 这也是 finally 里 FreeHGlobal 之前必须 GetOverlappedResult(wait:true) 的原因。
    /// </summary>
    public bool Write(byte[] report)
    {
        var ev = CreateEventW(IntPtr.Zero, true, false, null);
        var buf = Marshal.AllocHGlobal(report.Length);
        var ov = new NativeOverlapped { EventHandle = ev };
        try
        {
            Marshal.Copy(report, 0, buf, report.Length);
            if (WriteFile(_handle, buf, report.Length, out _, ref ov)) return true;
            if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
            if (WaitForSingleObject(ev, 1000) != WAIT_OBJECT_0)
            {
                CancelIoEx(_handle, ref ov);
                GetOverlappedResult(_handle, ref ov, out _, true);
                return false;
            }
            return GetOverlappedResult(_handle, ref ov, out _, false);
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            CloseHandle(ev);
        }
    }

    /// <summary>
    /// 读一个输入报告；超时返回 null。
    ///
    /// 缓冲区至少 64 字节：短报文设备只回 7 字节，但有些接收器会在长报文模式下发 20 字节，
    /// 按 InputReportByteLength 给（可能比真实报文小）会导致读不全。
    /// 超时处理与 Write 相同，取消后必须等 I/O 真正结束才能释放缓冲区。
    /// </summary>
    public byte[]? Read(int timeoutMs)
    {
        var size = Math.Max((int)Caps.InputReportByteLength, 64);
        var ev = CreateEventW(IntPtr.Zero, true, false, null);
        var buf = Marshal.AllocHGlobal(size);
        var ov = new NativeOverlapped { EventHandle = ev };
        try
        {
            int read;
            if (!ReadFile(_handle, buf, size, out read, ref ov))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) return null;

                if (WaitForSingleObject(ev, (uint)timeoutMs) != WAIT_OBJECT_0)
                {
                    CancelIoEx(_handle, ref ov);
                    GetOverlappedResult(_handle, ref ov, out read, true);   // 等取消真正完成再释放缓冲区
                    return null;
                }
                if (!GetOverlappedResult(_handle, ref ov, out read, false)) return null;
            }

            if (read <= 0) return null;
            var data = new byte[read];
            Marshal.Copy(buf, data, 0, read);
            return data;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            CloseHandle(ev);
        }
    }

    public void Dispose() => _handle.Dispose();
}
