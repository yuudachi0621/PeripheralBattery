using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using PeripheralBattery.Native;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace PeripheralBattery.Diagnostics;

/// <summary>
/// 诊断模式（命令行 --probe）：把当前机器的 HID / 蓝牙设备真相打印出来。
/// 仅用于开发期验证协议，正常使用不会跑。结果同时写进 %TEMP%\pb_probe.txt。
///
/// 为什么需要它：外设读不到电量时，原因可能在很底层的位置 ——
/// 接口选错了、特性不支持、接收器没醒、蓝牙设备根本没连上。
/// 界面通常只会显示“电量未知”图标或简短原因，看不出是哪一层的问题。这个探针把每一层都摊开打印：
///   1. 系统里所有 HID 接口（VID/PID/usage/报告长度/路径）；
///   2. 罗技接收器上每个设备索引的探活结果、特性表、每个函数的原始回复字节；
///   3. 机械师键盘接收器（VID 048D）的报告描述符，看它有没有把电量声明成 HID 用法；
///   4. 蓝牙：经典设备列表 + BLE 全量服务/特征，并尝试对经典设备走 BLE 读电池服务。
///
/// 判断顺序建议：先看第 2 段能不能探活（探不到时先排查休眠、连接状态和接口选择），
/// 再看待打印出的 0x1004 回复原始字节，对照 <see cref="HidppChannel.ReadBattery"/> 的注释看 [6]/[7] 位。
/// </summary>
internal static class Probe
{
    /// <summary>所有输出先攒在内存里，最后统一写文件 + 打控制台，避免中途异常丢日志。</summary>
    private static readonly StringBuilder Sb = new();

    public static async Task RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;   // 控制台默认不是 UTF-8，中文会花
        try
        {
            Log("===== HID 设备 =====");
            var all = Hid.Enumerate();
            foreach (var i in all) Log(i + "\n    " + i.Path);

            Log("\n===== 罗技 HID++ 探测 =====");
            ProbeLogitech(all);

            Log("\n===== 048D 键盘接收器 报告描述符 =====");
            DumpDescriptor(all.FirstOrDefault(i => i.VendorId == 0x048D));

            Log("\n===== 048D 键盘接收器 Feature Report 扫描 =====");
            DumpFeatures(all.Where(i => i.VendorId == 0x048D).ToList());

            Log("\n===== 蓝牙设备 =====");
            await ProbeBluetoothAsync();
        }
        catch (Exception ex)
        {
            Log("探针异常: " + ex);
        }
        finally
        {
            var path = Path.Combine(Path.GetTempPath(), "pb_probe.txt");
            File.WriteAllText(path, Sb.ToString(), Encoding.UTF8);
            Console.WriteLine(Sb.ToString());
            Console.WriteLine("\n[报告已写入] " + path);
        }
    }

    private static void ProbeLogitech(List<HidInfo> all)
    {
        // 罗技的 HID++ 只走 vendor 自定义 usage page 0xFF00 的接口，
        // 同一个接收器会被系统枚举出好几个接口（键盘、鼠标、厂商自定义），只有这个能说话
        var candidates = all.Where(i => i.VendorId == 0x046D && i.UsagePage == 0xFF00).ToList();
        Log($"候选接口 {candidates.Count} 个");
        foreach (var c in candidates)
        {
            Log($"\n-- {c.Key} usage={c.Usage:X4} in={c.InputLength} out={c.OutputLength}");
            var dev = HidHandle.Open(c.Path);
            if (dev == null)
            {
                Log("   打不开");
                continue;
            }
            try
            {
                // 0xFF 是接收器自己，1~6 是挂在它下面的设备。逐个探活，
                // "无响应"通常意味着那台设备在休眠（按一下键/动一下鼠标再试）
                foreach (byte idx in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
                {
                    var ch = HidppChannel.Create(dev, idx);
                    if (!ch.Ping(150))
                    {
                        Log($"   idx=0x{idx:X2} 无响应");
                        continue;
                    }
                    Log($"   idx=0x{idx:X2} 在线 (long={ch.LongReports})");

                    var name = ch.ReadDeviceName();
                    Log($"      名称: {name ?? "(读不到)"}");
                    var info = ch.ReadDeviceInfo();
                    Log(info == null
                        ? "      DEVICE_INFO: 读不到"
                        : $"      unitId=0x{info.Value.unitId:X8} modelId=0x{info.Value.modelId:X4}");

                    // 把电量相关的特性挨个问一遍，并把原始字节打出来。
                    // 之所以不直接调 ReadBattery：真出问题时需要看到"到底回了什么"，
                    // 而不是只看一个最终结论。0x0003 是 DEVICE_FW_VERSION，顺手确认链路通。
                    foreach (var fid in new ushort[] { 0x1004, 0x1000, 0x1001, 0x0003 })
                    {
                        var fi = ch.FeatureIndex(fid);
                        Log($"      特性 0x{fid:X4} -> idx {(fi == null ? "不支持" : fi.Value.ToString())}");
                        if (fi == null) continue;
                        // fn0/fn1 各问一次：0x1004 的电量在 fn1，0x1000/0x1001 在 fn0
                        for (byte fn = 0; fn <= 1; fn++)
                        {
                            var r = ch.Request(fi.Value, fn);
                            if (r != null) Log($"         fn{fn}: {Convert.ToHexString(r, 0, Math.Min(r.Length, 12))}");
                        }
                    }
                    // 最后跑一遍真实逻辑，输出和界面/托盘完全一致的结果
                    var bat = ch.ReadBattery();
                    Log(bat == null
                        ? "      => 电量: 读不到"
                        : $"      => 电量: {bat.Value.percent}% 充电={bat.Value.charging} USB供电={bat.Value.externalPower}");
                }
            }
            finally
            {
                dev.Dispose();
            }
        }
    }

    private const uint IOCTL_HID_GET_REPORT_DESCRIPTOR = 0x000B0000;

    /// <summary>
    /// 对 048D 接收器的每个接口全量扫 Feature Report（reportId 0~255）。
    ///
    /// 为什么扫它：接收器的 FF89/0010 集合没有输入/输出报告、只有 Feature Report，
    /// 厂商私有数据（电量、充电状态最常见）通常就藏在这里。描述符解析太麻烦，
    /// 直接逐个 ID 读是最快的验证方式；GetFeature 是只读操作，但可能阻塞，所以只放在诊断流程里。
    /// 全零的响应也过滤掉（有些设备对任意 ID 都回全零，没有信息量）。
    /// </summary>
    private static void DumpFeatures(List<HidInfo> devices)
    {
        if (devices.Count == 0)
        {
            Log("没有 048D 设备");
            return;
        }

        foreach (var info in devices)
        {
            Log($"\n-- {info.Key} usage={info.UsagePage:X4}/{info.Usage:X4}");

            // 只读就够：GetFeature 不写任何东西
            var dev = HidHandle.Open(info.Path, write: false);
            if (dev == null)
            {
                Log("   打不开");
                continue;
            }
            try
            {
                var flen = dev.Caps.FeatureReportByteLength;
                if (flen <= 0)
                {
                    Log("   描述符未声明 Feature Report，跳过");
                    continue;
                }
                Log($"   FeatureReport 长度 {flen}，扫描 reportId 0x00~0xFF：");

                int found = 0;
                for (int id = 0; id <= 255; id++)
                {
                    var r = dev.GetFeature((byte)id);
                    if (r == null) continue;

                    // 全零 = 无信息（很多设备对未知 ID 也回成功但内容全零）
                    var hasData = false;
                    for (int i = 1; i < r.Length; i++)
                        if (r[i] != 0) { hasData = true; break; }
                    if (!hasData) continue;

                    Log($"     id=0x{id:X2}: {Convert.ToHexString(r)}");
                    found++;
                }
                if (found == 0) Log("     （没有任何非零的 Feature Report）");
            }
            finally
            {
                dev.Dispose();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code,
        IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    /// <summary>
    /// 读键盘接收器的 HID 报告描述符，看它有没有把电量声明成标准用法。
    ///
    /// 背景：有些键鼠把电量塞进"厂商自定义"的输入报告里，格式不公开；
    /// 但如果它的描述符里出现了 Generic Device Controls 的 Battery Strength
    /// （usage page 0x06 + usage 0x20），就说明系统能按标准 HID 解析出电量，
    /// 我们也有机会直接读，不用逆向私有协议。
    /// 注意 HID 描述符是紧凑字节码，这里只做"字面上找 05 06 / 09 20"的粗筛，
    /// 命中只是"有希望"，不命中也不代表一定没有（usage 可能用 16/32 位编码）。
    /// </summary>
    private static void DumpDescriptor(HidInfo? info)
    {
        if (info == null)
        {
            Log("没有 048D 设备");
            return;
        }
        Log($"接口: {info.Key} usage={info.UsagePage:X4}/{info.Usage:X4} in={info.InputLength}");

        // 用只读方式重开一次，专门发 IOCTL
        var dev = HidHandle.Open(info.Path, write: false);
        if (dev == null)
        {
            Log("打不开设备");
            return;
        }
        try
        {
            var buf = Marshal.AllocHGlobal(512);
            try
            {
                if (DeviceIoControl(dev.RawHandle, IOCTL_HID_GET_REPORT_DESCRIPTOR, IntPtr.Zero, 0,
                        buf, 512, out var got, IntPtr.Zero))
                {
                    var bytes = new byte[got];
                    Marshal.Copy(buf, bytes, 0, got);
                    Log($"描述符长度 {got}：\n{Convert.ToHexString(bytes)}");
                    // 找 Battery Strength：UsagePage 0x06 (Generic Device Controls) + Usage 0x20
                    var s = Convert.ToHexString(bytes);
                    Log(s.Contains("0506") && s.Contains("0920") ? "含 0x06/0x20：有希望" : "未见 Battery Strength 用法");
                }
                else
                {
                    Log("IOCTL 失败 " + Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            dev.Dispose();
        }
    }

    /// <summary>
    /// 蓝牙部分：先把"已配对"和"已连接"两种状态都列出来，再逐个翻服务。
    ///
    /// 重点是第 3 段那个实验 —— 经典蓝牙（A2DP）外设能不能从 BLE 侧连上去读 0x180F。
    /// 如果能，就等于多了一条通用电量通道；如果连不上（多数 TWS 耳机是这样），
    /// 通常只能继续尝试厂商私有协议（见 OpoProvider / HfpProbe）。
    /// </summary>
    private static async Task ProbeBluetoothAsync()
    {
        // ---- 1. 经典蓝牙（A2DP / 老式 HID）----
        Log("\n[经典蓝牙设备]");
        IReadOnlyList<DeviceInformation> classic;
        try
        {
            classic = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
        }
        catch (Exception ex)
        {
            Log("枚举失败: " + ex.Message);
            classic = Array.Empty<DeviceInformation>();
        }
        Log($"已配对经典蓝牙设备 {classic.Count} 个");

        var addresses = new List<(string name, ulong addr)>();
        foreach (var di in classic)
        {
            BluetoothDevice? bd = null;
            try
            {
                bd = await WithTimeout(BluetoothDevice.FromIdAsync(di.Id), 3000);
                if (bd == null)
                {
                    Log($"  {di.Name}: FromIdAsync 超时");
                    continue;
                }
                Log($"  {bd.Name}  地址=0x{bd.BluetoothAddress:X12} 连接={bd.ConnectionStatus}");
                if (bd.BluetoothAddress != 0) addresses.Add((bd.Name, bd.BluetoothAddress));
            }
            catch (Exception ex)
            {
                Log($"  {di.Name}: {ex.GetType().Name} {ex.Message}");
            }
            finally
            {
                bd?.Dispose();
            }
        }

        // ---- 2. 已配对的 BLE 设备 ----
        IReadOnlyList<DeviceInformation> infos;
        try
        {
            infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        }
        catch (Exception ex)
        {
            Log("枚举 BLE 设备失败: " + ex.Message);
            infos = Array.Empty<DeviceInformation>();
        }
        Log($"\n[已配对 BLE 设备] {infos.Count} 个");
        foreach (var di in infos) DumpBleDevice(di.Name, di.Id);
        foreach (var di in infos) await InspectBleAsync(di.Name, null, di.Id);

        // ---- 3. 关键实验：经典蓝牙外设能不能从 BLE 侧连上去读电池服务 ----
        Log("\n[尝试对经典蓝牙外设走 BLE GATT]");
        foreach (var (name, addr) in addresses)
        {
            if (infos.Any(i => i.Name == name)) continue;
            await InspectBleAsync(name, addr, null);
        }
    }

    /// <summary>
    /// 连上一个 BLE 设备，把它所有的服务和特征都列出来，遇到 0x2A19 就顺手读电量。
    /// 用全量枚举而不是只查电池服务 UUID，是为了看到厂商私有服务里藏了什么
    /// （很多 TWS 耳机的左右耳电量就藏在私有特征里）。
    /// </summary>
    private static async Task InspectBleAsync(string name, ulong? address, string? id)
    {
        Log($"\n-- {name} {(address != null ? $"addr=0x{address:X12}" : id)}");
        BluetoothLEDevice? dev = null;
        try
        {
            dev = address != null
                ? await WithTimeout(BluetoothLEDevice.FromBluetoothAddressAsync(address.Value), 5000)
                : await WithTimeout(BluetoothLEDevice.FromIdAsync(id!), 5000);
            if (dev == null)
            {
                Log("   连接对象获取超时（设备可能不支持 BLE 或未广播）");
                return;
            }
            Log($"   名称={dev.Name} 连接状态={dev.ConnectionStatus}");

            var svcRes = await WithTimeout(dev.GetGattServicesAsync(BluetoothCacheMode.Uncached), 8000);
            if (svcRes == null)
            {
                Log("   服务发现超时");
                return;
            }
            Log($"   服务发现: {svcRes.Status}, {svcRes.Services.Count} 个服务");
            foreach (var s in svcRes.Services)
            {
                Log($"     [{s.Uuid}]");
                var chRes = await WithTimeout(s.GetCharacteristicsAsync(BluetoothCacheMode.Uncached), 3000);
                if (chRes == null || chRes.Status != GattCommunicationStatus.Success)
                {
                    Log("        特征读取失败");
                    continue;
                }
                foreach (var ch in chRes.Characteristics)
                {
                    string extra = "";
                    if (ch.Uuid == GattCharacteristicUuids.BatteryLevel)
                    {
                        var rd = await WithTimeout(ch.ReadValueAsync(BluetoothCacheMode.Uncached), 3000);
                        extra = rd != null && rd.Status == GattCommunicationStatus.Success && rd.Value.Length > 0
                            ? $"  => 电量 {ReadByte(rd.Value)}%"
                            : "  => 读电量失败";
                    }
                    Log($"        {ch.Uuid}{extra}");
                }
            }
        }
        catch (Exception ex)
        {
            Log("   异常: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            dev?.Dispose();
        }
    }

    private static void DumpBleDevice(string name, string id) => Log($"  {name}  [{id}]");

    private static byte ReadByte(Windows.Storage.Streams.IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes.Length > 0 ? bytes[0] : (byte)0;
    }

    /// <summary>
    /// 给一个 WinRT 异步操作套超时。蓝牙 API 在设备关机/不可达时可能永远不返回，
    /// 不兜超时的话探针会卡死在某一个设备上，后面的设备全看不到。
    /// 超时返回 null —— 探针只关心"能不能读到"，不关心为什么读不到。
    /// </summary>
    private static async Task<T?> WithTimeout<T>(Windows.Foundation.IAsyncOperation<T> op, int ms) where T : class
    {
        var task = op.AsTask();
        var done = await Task.WhenAny(task, Task.Delay(ms));
        if (done != task) return null;
        try { return await task; } catch { return null; }
    }

    private static void Log(string s) => Sb.AppendLine(s);
}
