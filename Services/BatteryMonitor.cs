using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using PeripheralBattery.Models;
using PeripheralBattery.Native;
using PeripheralBattery.Providers;

namespace PeripheralBattery.Services;

/// <summary>
/// 轮询调度 + 状态合并的中央大脑。
///
/// 低功耗设计要点：
/// 1. 用 System.Threading.Timer（到点才唤醒一次），关掉面板时不做任何渲染；
/// 2. 用户空闲超过 5 分钟时只跑 1/4 的轮询次数（蓝牙射频唤醒很贵）；
/// 3. 同一时刻只允许一轮在跑（_busy 互斥），不会因为手动刷新而堆叠；
/// 4. 托盘图标按 5% 档位缓存；设备属性通过 INotifyPropertyChanged 推送给可见界面。
/// </summary>
public sealed class BatteryMonitor : INotifyPropertyChanged
{
    private readonly AppConfig _config;
    private readonly Dispatcher _ui;
    private readonly IBatteryProvider[] _providers;
    private readonly List<DeviceState> _all = new();

    private Timer? _timer;
    private int _busy;
    private int _pendingRefresh;
    private int _tick;
    private bool _structureChanged;
    private DateTime _lastSave = DateTime.MinValue;
    private DateTime _lastQuickRetry = DateTime.MinValue;
    private string _lastSummary = "";

    /// <summary>当前在线、且被用户关注的设备，按设备类型和名称排序。</summary>
    public ObservableCollection<DeviceState> Online { get; } = new();

    /// <summary>当前离线、且被用户关注的设备，按最后在线时间倒序排列。</summary>
    public ObservableCollection<DeviceState> Offline { get; } = new();

    /// <summary>面板里所有设备（在线 + 离线），设置页的"主设备"下拉框要用。</summary>
    public IReadOnlyList<DeviceState> AllDevices => _all;

    /// <summary>
    /// 仅供 --selftest：把设备直接塞进内部逻辑列表，跳过真实轮询。
    /// 直接往 Online 里 Add 是没用的 —— 那是界面集合，逻辑判断走的是 _all。
    /// </summary>
    internal void SeedForTest(params DeviceState[] devices)
    {
        foreach (var d in devices) _all.Add(d);
        RebuildCollections();
    }

    /// <summary>一轮刷新完成、主设备变化或设备增删后触发，调用方据此同步托盘和提醒状态。</summary>
    public event Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public BatteryMonitor(AppConfig config, Dispatcher ui)
    {
        _config = config;
        _ui = ui;
        // 数组顺序**不代表执行顺序**（谁先跑由 RefreshAsync 的分组决定：罗技独占先跑，其余并行），
        // 但它**确实影响合并优先级**：Merge 按这个顺序逐个 Upsert，同 Id 的后写覆盖前写。
        // 经典蓝牙排在最前、OPPO 排在最后，是因为两者可能报同一副耳机（bt:地址）——
        // OPPO 私有协议能读到真实百分比和充电位，应该让它覆盖蓝牙通道那一份"没有电量"的空快照。
        // 调整顺序前请确认这个覆盖关系。
        _providers = new IBatteryProvider[]
        {
            new BluetoothClassicProvider(),   // 经典蓝牙：在线状态 + 系统可提供的 PnP 电量
            new LogitechProvider(),           // 罗技 2.4G（HID++ 2.0），电量最准的一条
            new BleProvider(),                // 蓝牙 GATT（0x180F 电池服务）
            new OpoProvider(),                // OPPO/OnePlus/realme 耳机私有协议（RFCOMM）
        };

        // 上一次运行留下的设备先按离线状态加载出来，避免界面空白。
        // 只加载用户收进面板的（Tracked）：被删掉的设备不再出现，但下次扫描还能找回来。
        foreach (var (id, record) in config.Devices)
        {
            if (!record.Tracked) continue;
            var state = new DeviceState(id, record.Provider)
            {
                CustomName = record.CustomName,
                DefaultName = record.DefaultName,
                LowThreshold = config.LowBatteryPercent,
            };
            state.Kind = (DeviceKind)record.Kind;
            state.Link = (LinkKind)record.Link;
            state.Percent = record.LastPercent;
            state.LastSeen = record.LastSeenUtc.ToLocalTime();
            state.Online = record.WasOnline;   // 先按上次的状态显示，等这一轮读完再校正
            state.IsPrimary = id == config.PrimaryDeviceId;
            _all.Add(state);
        }
        RebuildCollections();
    }

    /// <summary>当前主设备（未指定或已不在面板里则返回 null）。</summary>
    public DeviceState? Primary =>
        string.IsNullOrEmpty(_config.PrimaryDeviceId)
            ? null
            : _all.FirstOrDefault(d => d.Id == _config.PrimaryDeviceId);

    /// <summary>把它设为主设备，并立即保存配置。</summary>
    /// <param name="state">目标设备；传 null 表示恢复"所有在线设备里最低"。</param>
    /// <remarks>会更新所有设备的 IsPrimary 标记、触发 Changed，并通过 SaveNow 持久化。</remarks>
    public void SetPrimary(DeviceState? state)
    {
        _config.PrimaryDeviceId = state?.Id ?? "";
        foreach (var d in _all) d.IsPrimary = d.Id == _config.PrimaryDeviceId;
        SaveNow();
        Raise(nameof(PrimaryCaption));
        Raise(nameof(HasPrimary));
        Changed?.Invoke();
    }

    /// <summary>配置被外部改过（设置窗口保存）后，把界面上的主设备标记重新对齐，但不再次保存。</summary>
    public void SyncPrimaryFromConfig()
    {
        foreach (var d in _all) d.IsPrimary = d.Id == _config.PrimaryDeviceId;
        Raise(nameof(PrimaryCaption));
        Raise(nameof(HasPrimary));
        Changed?.Invoke();
    }

    /// <summary>标题下面那行小字：有主设备时说明图标跟着谁；未指定主设备时返回空字符串。</summary>
    public string PrimaryCaption
    {
        get
        {
            var primary = Primary;
            return primary == null ? "" : "托盘图标：" + primary.Name;
        }
    }

    public bool HasPrimary => Primary != null;

    /// <summary>设置里改了低电量阈值后调用，让已有设备行立刻换色。</summary>
    public void ApplyLowThreshold()
    {
        foreach (var state in _all) state.LowThreshold = _config.LowBatteryPercent;
    }

    public string LastRefreshText { get; private set; } = "尚未刷新";
    public string FooterText => $"轮询间隔 {_config.PollSeconds} 秒 · 共 {_all.Count} 台设备";
    public string OfflineHeader => $"历史设备（{Offline.Count}）";
    public bool ShowOfflineSection => _config.ShowOffline && Offline.Count > 0;
    public bool HasOnline => Online.Count > 0;

    /// <summary>启动轮询计时器；重复调用等同于重新安排计时器。</summary>
    public void Start() => RestartTimer();

    /// <summary>销毁旧计时器并按当前配置重建：约 300 ms 后首轮，之后按配置间隔执行。</summary>
    public void RestartTimer()
    {
        _timer?.Dispose();
        var period = TimeSpan.FromSeconds(Math.Max(_config.PollSeconds, 5));
        // 启动时立刻跑一轮，之后按间隔走
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMilliseconds(300), period);
    }

    private async Task TickAsync()
    {
        _tick++;
        // 空闲降频：每 4 个周期才真正读一次
        if (_config.ThrottleWhenIdle && Power.IdleSeconds() > 300 && _tick % 4 != 0) return;
        await RefreshAsync(manual: false);
    }

    /// <summary>立即执行一轮刷新。</summary>
    /// <param name="manual">true 表示用户主动刷新；若此时已有轮询在跑，会登记一次待刷新而不是并发执行。</param>
    /// <remarks>本轮结果在 UI Dispatcher 上合并，并在结束后触发 <see cref="Changed"/>。</remarks>
    public async Task RefreshAsync(bool manual)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            // 已经有一轮在跑：记下来，等这轮结束再补一轮，别让"立即刷新"点了没反应
            if (manual) Volatile.Write(ref _pendingRefresh, 1);
            return;
        }

        try
        {
            // 分两组跑，不是图省事，是实测出来的：
            //   第一组 = HID/HID++ 通道。它走 USB，跟蓝牙射频没直接关系，
            //   但实测只要同时有蓝牙在密集建链，罗技那 9 秒的长超时就全跑满，而且读不到设备
            //   （对照实验：禁用 OPPO 通道后罗技稳定 2 秒、100% 读到；启用后变 9.4 秒、0 台）。
            //   所以让它独占先跑完，再放蓝牙类通道并行。
            var hidProviders = _providers.Where(p => p is LogitechProvider).ToList();
            var radioProviders = _providers.Where(p => p is not LogitechProvider).ToList();

            var results = new ProviderResult[_providers.Length];
            foreach (var provider in hidProviders)
                results[Array.IndexOf(_providers, provider)] = await SafePollAsync(provider);

            // USB 有线检测和蓝牙通道并行跑：它要逐个打开 HID 接口读产品名，
            // 遇到被独占的设备可能变慢，绝不能放进 UI 线程（Merge 在 Dispatcher 上执行）。
            // 再兜一个 5 秒超时：极端情况下枚举卡住也不能拖死整轮轮询，
            // 代价只是这一轮不做有线检测（下一轮再试）。
            var usbNamesTask = Task.Run(GetUsbProductNames);

            var radioResults = await Task.WhenAll(radioProviders.Select(SafePollAsync));
            for (int i = 0; i < radioProviders.Count; i++)
                results[Array.IndexOf(_providers, radioProviders[i])] = radioResults[i];

            var usbDone = await Task.WhenAny(usbNamesTask, Task.Delay(5000));
            var usbNames = usbDone == usbNamesTask
                ? usbNamesTask.Result
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (usbDone != usbNamesTask)
                Log.Write("USB 有线检测超时（>5s），本轮跳过");

            await _ui.InvokeAsync(() => Merge(results, usbNames));

            LastRefreshText = DateTime.Now.ToString("HH:mm:ss") + " 更新";
            Raise(nameof(LastRefreshText));
            Changed?.Invoke();
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            if (Interlocked.Exchange(ref _pendingRefresh, 0) == 1) _ = RefreshAsync(manual: true);
        }
    }

    /// <summary>给单个 Provider 套 25 秒取消上限，并把异常转换成通道失败结果。</summary>
    /// <param name="provider">要执行的通道。</param>
    /// <returns>Provider 的结果；抛异常时返回 <c>Ok=false</c>。</returns>
    /// <remarks>耗时超过 1.5 秒的通道会写一条慢通道日志。</remarks>
    private static async Task<ProviderResult> SafePollAsync(IBatteryProvider provider)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));   // 单通道最多 25 秒
            return await provider.PollAsync(cts.Token);
        }
        catch (Exception ex)
        {
            return ProviderResult.Fail(ex.Message);
        }
        finally
        {
            // 只记慢的通道，便于发现"某类设备在拖慢轮询"
            if (watch.ElapsedMilliseconds > 1500)
                Log.Write($"慢通道 {provider.Name}: {watch.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>把各通道结果合并进界面集合，并更新在线、历史和持久化状态。</summary>
    /// <param name="results">与 <c>_providers</c> 顺序一一对应的本轮结果。</param>
    /// <param name="usbNames">本轮从 USB HID 收集到的产品名，用于有线连接兜底。</param>
    /// <remarks>只有成功通道才会参与离线判定；未被跟踪的设备不会因本轮被发现而自动加入面板。</remarks>
    private void Merge(ProviderResult[] results, ISet<string> usbNames)
    {
        var okProviders = new HashSet<string>();
        var seen = new HashSet<string>();
        var needQuickRetry = false;

        for (int i = 0; i < results.Length; i++)
        {
            var provider = _providers[i];
            var result = results[i];
            if (!result.Ok) continue;   // 整条通道失败（比如蓝牙服务异常），不能据此判断设备离线
            okProviders.Add(provider.Name);

            // 通道本身没问题却一台都没读到，而它名下原来是有设备的 → 值得马上再试一次
            if (result.Devices.Count == 0 && _all.Any(d => d.ProviderName == provider.Name))
                needQuickRetry = true;

            foreach (var snapshot in result.Devices)
            {
                // 用户没收进面板的设备（被删掉的），这轮读到了也不算"在线"，
                // 否则删掉后下一轮又被 Upsert 加回来，等于删不掉。
                if (!IsTracked(snapshot.Id)) continue;

                var state = Upsert(snapshot, provider.Name);
                state.Online = true;
                state.Misses = 0;
                state.LastSeen = DateTime.Now;
                seen.Add(state.Id);
            }
        }

        // 通道正常但设备没出现 → 连续两轮都没出现才判离线。
        // 无线设备从省电状态唤醒偶尔会漏掉一两次请求，只漏一轮就标灰会让界面乱闪。
        foreach (var state in _all)
        {
            if (!okProviders.Contains(state.ProviderName) || seen.Contains(state.Id)) continue;
            if (!state.Online || ++state.Misses < 2) continue;

            state.Online = false;
            state.Misses = 0;
            _structureChanged = true;
        }

        // USB 有线兜底：设备从无线切到有线（比如 K600 插线后自动断开蓝牙）时，
        // 无线通道再也读不到它，上面那段会把它判成离线 —— 但键盘明明就在手边好好用着。
        // 这里按设备名去 USB HID 里找一遍，找到就把它拉回在线并标记"充电中"。
        // （usbNames 由调用方在后台线程预先收集好，这里只做内存匹配，不碰硬件。）
        ApplyWiredConnections(usbNames, seen);

        RebuildCollections();
        Remember();

        // 只在通道状态变化时记一笔，避免日志被每分钟一次的轮询刷满
        var summary = string.Join(" | ", results.Select((r, i) =>
            $"{_providers[i].Name}={(r.Ok ? r.Devices.Count + "台" : "失败:" + r.Error)}"));
        if (summary != _lastSummary)
        {
            _lastSummary = summary;
            Log.Write("轮询: " + summary);
        }

        // 设备增减时立刻落盘（自定义名称/历史记录不能靠正常退出才保存，
        // 用户可能直接结束进程）；其余情况最多 10 分钟写一次，避免频繁写盘。
        if (_structureChanged || DateTime.Now - _lastSave > TimeSpan.FromMinutes(10)) SaveNow();

        // 某条通道这轮一台都没读到、但它名下本来有设备：3 秒后补一轮。
        // 实测罗技接收器刚被打开时首个请求常常被吞掉，紧接着的第二次访问基本都能成功，
        // 与其让用户等到下一个轮询周期，不如马上补一次。
        if (needQuickRetry && DateTime.Now - _lastQuickRetry > TimeSpan.FromSeconds(30))
        {
            _lastQuickRetry = DateTime.Now;
            Log.Write("有通道本轮空手而归，3 秒后补一轮");
            _ = Task.Delay(3000).ContinueWith(_ => RefreshAsync(manual: false), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 收集 USB 直连 HID 设备的产品名（去重）。
    ///
    /// 判别"USB 直连"的依据是设备路径：USB 的形如 <c>hid#vid_048d&amp;…</c>，
    /// 蓝牙的形如 <c>hid#{00001124-…}_vid&amp;…</c>（带着蓝牙 HID 服务的 GUID）。
    /// 罗技设备跳过 —— 它有自己的通道，而且有线直连时 [7] 位比名字匹配准得多。
    /// </summary>
    private static HashSet<string> GetUsbProductNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var info in Hid.Enumerate())
            {
                if (!info.Path.Contains("hid#vid_", StringComparison.OrdinalIgnoreCase)) continue;
                if (info.VendorId == 0x046D) continue;
                if (!string.IsNullOrWhiteSpace(info.Product)) names.Add(info.Product);
            }
        }
        catch
        {
            // 枚举失败就当这一轮没有有线设备，不影响其它通道
        }
        return names;
    }

    /// <summary>
    /// USB 有线兜底（可注入名单，便于自检）。
    ///
    /// 场景：双模键盘从蓝牙切到有线（或插线充电时蓝牙断开）后，无线通道把它判离线，
    /// 但它明明插在本机 USB 上 —— 这时按**设备名**在 USB HID 里找，找到就拉回在线，
    /// **把连接方式改成"有线"**（副标题显示"有线"），并标记为"充电中"（有线直连必然在供电）。
    ///
    /// 这个判据是唯一可信的"有线/无线"来源：
    /// PnP 属性里那个语义像充电状态的 pid=4，实测纯蓝牙没插线时也是 True，完全不能用。
    ///
    /// <paramref name="channelSeen"/> 是本轮被无线通道读到的设备 Id：
    ///   · 通道读到的设备由通道自己负责（它报的连接方式更准），有线逻辑只负责把
    ///     上一轮的有线标记清干净；
    ///   · 上一轮靠有线标记、这一轮 USB 里找不到了 → 拔线/切模式，
    ///     还原连接方式、清充电标记，让正常的"连续 2 轮没读到才离线"逻辑接管。
    ///
    /// 用名字关联而不是设备 Id，是因为 USB HID 拿不到蓝牙地址，而**设备名
    /// （CustomName / DefaultName）在两种模式下是一致的**。同名设备极端情况下会互相串，
    /// 但普通人不会同时接两台同型号键盘，这个近似可接受。
    /// </summary>
    internal void ApplyWiredConnections(ISet<string> usbNames, HashSet<string> channelSeen)
    {
        foreach (var d in _all)
        {
            // 本轮通道读到了它：状态归通道管（连接方式由通道按实际链路设置），
            // 但要把上一轮的有线标记清掉，否则"有线"会赖着不走
            if (channelSeen.Contains(d.Id))
            {
                if (d.WiredMarked) ClearWired(d);
                continue;
            }

            var match = usbNames.Contains(d.Name) || usbNames.Contains(d.DefaultName);
            if (match)
            {
                if (!d.WiredMarked)
                {
                    Log.Write($"检测到 USB 有线连接: {d.Name}（无线通道已读不到它）");
                    d.LinkBeforeWired = d.Link;
                }
                d.WiredMarked = true;
                if (!d.Online)
                {
                    d.Online = true;
                    _structureChanged = true;
                }
                d.Link = LinkKind.Wired;      // 副标题显示"有线"
                d.ExternalPower = true;       // 有线直连必然在供电
                d.Charging = true;            // 电池在被充（没有读数可验证，按供电推断）
                d.Misses = 0;
                d.LastSeen = DateTime.Now;
            }
            else if (d.WiredMarked)
            {
                Log.Write($"USB 有线连接断开: {d.Name}");
                ClearWired(d);
            }
        }
    }

    /// <summary>把"有线兜底"加上去的状态全部还原，交给正常轮询重新判定。</summary>
    private static void ClearWired(DeviceState d)
    {
        d.WiredMarked = false;
        d.ExternalPower = false;
        d.Charging = false;
        if (d.LinkBeforeWired is LinkKind prev) d.Link = prev;
        d.LinkBeforeWired = null;
    }

    /// <summary>
    /// 按稳定 Id 新建或更新设备状态。同一 Id 被后来的 Provider 命中时会覆盖类型、链路和状态字段；
    /// 新快照没有百分比时不会清掉已有百分比，只补充 Note。
    /// </summary>
    /// <param name="snapshot">本轮某个通道返回的设备快照。</param>
    /// <param name="providerName">本次负责该设备的通道名。</param>
    /// <returns>更新后的设备状态对象。</returns>
    private DeviceState Upsert(DeviceSnapshot snapshot, string providerName)
    {
        var state = _all.FirstOrDefault(d => d.Id == snapshot.Id);
        if (state == null)
        {
            state = new DeviceState(snapshot.Id, providerName);
            _config.Devices.TryGetValue(snapshot.Id, out var record);
            state.CustomName = record?.CustomName ?? "";
            state.LowThreshold = _config.LowBatteryPercent;
            _all.Add(state);
            _structureChanged = true;
        }
        state.ProviderName = providerName;
        state.DefaultName = snapshot.Name;
        state.Kind = snapshot.Kind;
        state.Link = snapshot.Link;
        // 有百分比就覆盖，同时清掉"无电量接口"的说明；没百分比时只补说明
        if (snapshot.Percent != null)
        {
            state.Percent = snapshot.Percent;
            state.Note = null;
        }
        else if (snapshot.Note != null)
        {
            state.Note = snapshot.Note;
        }
        state.Charging = snapshot.Charging;
        state.ExternalPower = snapshot.ExternalPower;
        return state;
    }

    /// <summary>重建两个集合。尽量复用已有对象，只做增删，避免界面闪烁。</summary>
    private void RebuildCollections()
    {
        Sync(Online, _all.Where(d => d.Online).OrderBy(d => Order(d.Kind)).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase));
        Sync(Offline, _all.Where(d => !d.Online).OrderByDescending(d => d.LastSeen));
        Raise(nameof(FooterText));
        Raise(nameof(OfflineHeader));
        Raise(nameof(ShowOfflineSection));
        Raise(nameof(HasOnline));
    }

    private static void Sync(ObservableCollection<DeviceState> target, IEnumerable<DeviceState> desired)
    {
        var list = desired.ToList();
        for (int i = target.Count - 1; i >= 0; i--)
            if (!list.Contains(target[i])) target.RemoveAt(i);

        for (int i = 0; i < list.Count; i++)
        {
            var index = target.IndexOf(list[i]);
            if (index < 0) target.Insert(Math.Min(i, target.Count), list[i]);
            else if (index != i) target.Move(index, i);
        }
    }

    private static int Order(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => 0,
        DeviceKind.Keyboard => 1,
        DeviceKind.Headset => 2,
        DeviceKind.Gamepad => 3,
        _ => 4,
    };

    /// <summary>把当前状态写回配置（只更新内存，磁盘另外择机落盘）。</summary>
    private void Remember()
    {
        foreach (var d in _all)
        {
            // Tracked 必须保留：被用户删掉的设备虽然不在 _all 里，
            // 但它的记录还在配置里（Tracked=false），这里不能顺手写成 true 把它救回来。
            _config.Devices.TryGetValue(d.Id, out var old);
            _config.Devices[d.Id] = new DeviceRecord
            {
                CustomName = d.CustomName,
                DefaultName = d.DefaultName,
                Kind = (int)d.Kind,
                // 存"真实链路"而不是当前的临时值：有线模式下 Link 会被改成 Wired，
                // 但那是运行时推断出来的，不该写进配置 —— 否则重启后第一轮轮询完成前，
                // 一台其实处在蓝牙模式的键盘会先闪一下"有线"。
                Link = (int)(d.LinkBeforeWired ?? d.Link),
                LastPercent = d.Percent,
                LastSeenUtc = d.LastSeen.ToUniversalTime(),
                WasOnline = d.Online,
                Provider = d.ProviderName,
                Tracked = old?.Tracked ?? true,
            };
        }
    }

    /// <summary>把当前设备状态同步到配置对象并立即落盘；只读诊断模式下由 ConfigStore 忽略写入。</summary>
    public void SaveNow()
    {
        Remember();
        ConfigStore.Save(_config);
        _structureChanged = false;
        _lastSave = DateTime.Now;
    }

    /// <summary>设置里改了间隔后，刷新底部文案。</summary>
    public void RefreshTimerHint() => Raise(nameof(FooterText));

    /// <summary>重命名设备并立即保存配置。</summary>
    /// <param name="state">要修改的设备。</param>
    /// <param name="newName">新名称；null、空字符串或纯空白表示恢复设备自报名称。</param>
    public void Rename(DeviceState state, string? newName)
    {
        state.CustomName = (newName ?? "").Trim();
        state.IsEditing = false;
        Raise(nameof(PrimaryCaption));
        SaveNow();
    }

    // ---------------- 跟踪列表：扫描 / 添加 / 移除 ----------------

    private bool IsTracked(string id) =>
        _config.Devices.TryGetValue(id, out var record) ? record.Tracked : true;

    /// <summary>设备是否已经在面板里（给"扫描其他设备"窗口判断显示"已添加"还是"添加"）。</summary>
    public bool IsTrackedByUser(string id) => IsTracked(id);

    /// <summary>
    /// 扫描：把四条通道完整跑一遍，返回**所有**看到的设备（包括本来就在面板里的）。
    ///
    /// 和常规轮询的区别：
    ///   · 不受"是否已收纳"过滤，用户能重新找回之前删掉的设备；
    ///   · 不受空闲降频影响，也不写配置；
    ///   · 只在用户点"扫描"时执行，所以不需要为省电做妥协。
    /// </summary>
    public async Task<List<ScanItem>> ScanAsync()
    {
        // HID 先独占跑完再放蓝牙类并行，原因见 RefreshAsync 里的对照实验说明。
        // 这里同样遵守，否则扫描会把罗技那 9 秒长超时全跑满。
        var hid = _providers.Where(p => p is LogitechProvider).ToList();
        var radio = _providers.Where(p => p is not LogitechProvider).ToList();

        var results = new (string Provider, ProviderResult Result)[_providers.Length];
        foreach (var provider in hid)
            results[Array.IndexOf(_providers, provider)] = (provider.Name, await SafePollAsync(provider));

        var radioResults = await Task.WhenAll(radio.Select(SafePollAsync));
        for (int i = 0; i < radio.Count; i++)
            results[Array.IndexOf(_providers, radio[i])] = (radio[i].Name, radioResults[i]);

        // 同一个 Id 可能被两条通道看到（经典蓝牙 + OPPO 私有协议），合并时优先保留有电量的那条
        var map = new Dictionary<string, ScanItem>();
        foreach (var (provider, result) in results)
        {
            if (!result.Ok) continue;
            foreach (var snapshot in result.Devices)
            {
                if (map.TryGetValue(snapshot.Id, out var existing))
                {
                    if (existing.Level == null && snapshot.Percent != null)
                    {
                        existing.Level = snapshot.Percent;
                        existing.Note = snapshot.Note;
                        existing.Charging = snapshot.Charging;
                        existing.ExternalPower = snapshot.ExternalPower;
                    }
                    continue;
                }
                map[snapshot.Id] = new ScanItem
                {
                    Id = snapshot.Id,
                    Name = snapshot.Name,
                    Kind = snapshot.Kind,
                    Link = snapshot.Link,
                    Level = snapshot.Percent,
                    Charging = snapshot.Charging,
                    ExternalPower = snapshot.ExternalPower,
                    Note = snapshot.Note,
                    Provider = provider,
                    AlreadyAdded = IsTracked(snapshot.Id),
                };
            }
        }

        return map.Values
            .OrderBy(i => i.AlreadyAdded)              // 未添加的排前面，方便直接勾
            .ThenBy(i => Order(i.Kind))
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>把扫描结果里的设备收进面板，并立即保存配置。</summary>
    /// <param name="items">用户在扫描窗口勾选的条目；调用方应只传尚未跟踪的设备。</param>
    /// <returns>实际新增或恢复跟踪的设备数量。</returns>
    /// <remarks>方法本身不按 Id 去重；当前 ScanWindow 会过滤 AlreadyAdded 条目后再调用。</remarks>
    public int AddScanned(IEnumerable<ScanItem> items)
    {
        int added = 0;
        foreach (var item in items)
        {
            if (_all.Any(d => d.Id == item.Id))
            {
                // 同一 Id 仍在内存但配置记录存在：保留旧名称并重新标记为跟踪。
                _config.Devices.TryGetValue(item.Id, out var record);
                if (record == null) continue;
                record.Tracked = true;
                var restored = new DeviceState(item.Id, item.Provider)
                {
                    CustomName = record.CustomName,
                    DefaultName = string.IsNullOrWhiteSpace(item.Name) ? record.DefaultName : item.Name,
                    Kind = item.Kind,
                    Link = item.Link,
                    Percent = item.Level,
                    ExternalPower = item.ExternalPower,
                    Note = item.Note,
                    LowThreshold = _config.LowBatteryPercent,
                    Online = true,
                    LastSeen = DateTime.Now,
                };
                _all.Add(restored);
                added++;
                continue;
            }

            _config.Devices[item.Id] = new DeviceRecord
            {
                CustomName = "",
                DefaultName = item.Name,
                Kind = (int)item.Kind,
                Link = (int)item.Link,
                LastPercent = item.Level,
                LastSeenUtc = DateTime.Now.ToUniversalTime(),
                WasOnline = true,
                Provider = item.Provider,
                Tracked = true,
            };

            _all.Add(new DeviceState(item.Id, item.Provider)
            {
                DefaultName = item.Name,
                Kind = item.Kind,
                Link = item.Link,
                Percent = item.Level,
                ExternalPower = item.ExternalPower,
                Note = item.Note,
                LowThreshold = _config.LowBatteryPercent,
                Online = true,
                LastSeen = DateTime.Now,
            });
            added++;
        }

        _structureChanged = true;
        RebuildCollections();
        SaveNow();
        Changed?.Invoke();
        return added;
    }

    /// <summary>
    /// 从面板里删掉一台设备（右键菜单）。
    /// 在线时只标记为不跟踪（下次还读得到，但不再显示）；
    /// 已离线时连记录一起删，免得配置里攒一堆垃圾。
    /// </summary>
    /// <param name="state">要移除的设备。若它是主设备，会同时清空主设备配置。</param>
    /// <remarks>会重建在线/离线集合、保存配置并触发 Changed。</remarks>
    public void Remove(DeviceState state)
    {
        var wasPrimary = state.Id == _config.PrimaryDeviceId;

        if (_config.Devices.TryGetValue(state.Id, out var record))
        {
            if (state.Online) record.Tracked = false;
            else _config.Devices.Remove(state.Id);
        }

        _all.Remove(state);
        if (wasPrimary) _config.PrimaryDeviceId = "";

        _structureChanged = true;
        RebuildCollections();
        SaveNow();
        Raise(nameof(PrimaryCaption));
        Changed?.Invoke();
    }

    /// <summary>
    /// 托盘悬停提示：优先把面板里所有在线设备逐行列出，电量前置并用竖线分栏。
    ///
    /// 三条约束在这里打架，处理顺序很重要：
    ///   1. 产品要求：尽量列出全部在线设备，并且**不受主设备设置影响**；
    ///   2. Windows 硬限制：NotifyIcon.Text 最多 63 个字符，超了直接抛异常；
    ///   3. 对齐要求：电量栏要落在同一列，名字才能对齐成一条线。
    ///
    /// 关于第 3 点 —— 这里踩过两次坑，记下来免得重犯：
    ///   第一次：按"显示列数"补空格（中文=2 列、英文=1 列），用户截图显示
    ///   "OPPO Enco Free4 20%" 的 20% 在 x=123，而 "GPW 2  41%" 的 41% 在 x=98，差了 25px。
    ///   原因是系统提示框用比例字体，空格和数字不一样宽，按字符补根本补不准。
    ///
    ///   第二次：改成"电量前置、数字右对齐"以后，本以为补到同样的字符数就完事了，
    ///   结果 Segoe UI 12px 下数字本身也**不是严格等宽**（'1' 比 '5' 窄、'0' 比 '3' 宽），
    ///   `PadLeft(3)` 让字符数一致，像素宽度仍差 3~6px。用户截图里一眼就看出来了。
    ///
    ///   结论：对齐只能按**像素**算 —— 见 <see cref="TryBuildAligned"/> 与 TextMetrics。
    ///
    /// 关于可读性 —— 电量前置后，` 41%  GPW 2` 里电量和名字之间只有空格，
    /// 系统提示框又是比例字体，整行糊在一起不容易一眼分清哪段是数字、哪段是名字。
    /// 所以电量后面加一个 `|` 竖线做分栏标记（见 ValueSuffix）：
    ///   · 竖线是**非数字**字符，用户扫一眼就知道"竖线右边才是设备名"；
    ///   · 它每行都跟在电量后面，本身也是定宽渲染，不会破坏对齐；
    ///   · 只占 1 个字符 + 1 个空格，比换成 " — " 之类的分隔符省得多。
    ///
    /// 常规显示时设备名上限为 DeviceNameLimit = 20 字符；空间不足时还会进一步收窄，
    /// 最后才减少设备数量并在末尾标注"…等 N 台"。
    /// </summary>
    public string BuildTooltip()
    {
        // 不管有没有指定主设备，都优先列出全部在线设备；63 字符限制下的降级见下方。
        // 主设备只影响**托盘图标**显示哪个数字，不影响悬停提示的内容 ——
        // 用户把鼠标移上来就是想一眼看全所有外设，这里只报一台反而更费解。
        //
        // 排序按电量升序（最需要关注的在最上面）。用的是 EffectivePercent ——
        // 它现在就是电池真实读数，充电中的设备也按真实值排，不会因为"插着线"被顶到最后。
        var list = _all
            .Where(d => d.Online)
            .OrderBy(d => d.EffectivePercent ?? 999)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0) return "外设电量\n暂无在线设备";

        // ---------- 主方案：电量前置 + 电量栏像素级右对齐 + 竖线分栏 ----------
        // 实际长相（真实设备）：
        //     "  5% | OPPO Enco Free4"
        //     " 30% | Machenike K600"
        //     " 41% | GPW 2"
        //     "+ 79% | GPW 2"          ← 插着 USB 线充电（数字仍是真实读数），见 ExternalPowerBadge
        //
        // 为什么把电量放前面 —— 这是踩了三个坑之后才想通的：
        //   1. 最初"名字在前 + 按列数补空格"：截图实测两行差 25px，因为空格和数字不同宽；
        //   2. 改成"按像素补空格"：对齐准了，但补空格本身要占字符，
        //      3 台设备补完 75 字符，直接顶破 63 上限；
        //   3. 电量前置后，右对齐的补位量大幅下降（最宽的那行不用补），
        //      2 台设备只要 35 字符左右 —— "对齐"和"省字符"不再是死对头。
        // 注意仍然需要补位：数字不等宽，只是补得比"名字在前"时少得多。
        var aligned = TryBuildAligned(list);
        if (aligned != null) return aligned;

        // ---------- 兜底：设备太多，改用紧凑格式 ----------
        // 走到这里说明"标准格式（4 位电量栏 + ' | '）"顶破了 63（比如 6 台以上就是）。
        //
        // 降级目标是优先保留"全部设备"：先保持较完整的名字，同时压缩电量栏和分隔符；
        // 仍放不下时才逐步收窄名字，最后减少台数并明确告知"…等 N 台"。
        // 这样普通 2~6 台设备通常不需要牺牲名称，极端数量下也不会悄悄漏掉设备。

        string Compose(int take, bool withMore, int nameLimit, int digits, string gap)
        {
            var lines = list.Take(take)
                .Select(d => Line(d, nameLimit, gap, digits))
                .ToList();
            if (withMore && list.Count > take) lines.Add($"…等 {list.Count} 台");
            return string.Join("\n", lines);
        }

        var attempts = new List<string>();
        // ① 先保住"全部列出"：外层逐个收窄名称上限，内层再压缩电量栏和分隔符。
        //    名称上限从 20 逐步降到 6，兼顾"可辨认"和"尽量不漏设备"。
        for (var limit = DeviceNameLimit; limit >= 6; limit -= 2)
        {
            foreach (var digits in ValueDigitOptions)
            {
                foreach (var gap in Gaps)
                    attempts.Add(Compose(list.Count, false, limit, digits, gap));
            }
        }
        // ② 还不行就减少台数，但必须明确告知还有几台
        foreach (var take in new[] { 5, 4, 3 })
        {
            for (var limit = 16; limit >= 6; limit -= 2)
            {
                foreach (var digits in ValueDigitOptions)
                {
                    foreach (var gap in Gaps)
                        attempts.Add(Compose(take, true, limit, digits, gap));
                }
            }
        }

        foreach (var candidate in attempts)
            if (candidate.Length <= MaxTooltipLength) return candidate;

        // 极端情况（名字里全是宽字符、台数又多）：退到最少信息，仍然硬截断兜底
        var last = attempts[^1];
        return last.Length > MaxTooltipLength ? last[..MaxTooltipLength] : last;
    }

    /// <summary>
    /// 设备名字符数上限。超出部分用省略号替代，避免超长型号把电量挤出屏幕。
    /// 20 是个折中：常见的 "OPPO Enco Free4"(15)、"Machenike K600"(14) 都能完整放下，
    /// 而 "PRO X SUPERLIGHT 2 SE"(21) 会截成 "PRO X SUPERLIGHT 2 …"，仍可辨认。
    /// </summary>
    private const int DeviceNameLimit = 20;

    /// <summary>
    /// 电量栏的常规宽度（数字位数，不含 % 号），也是"标准格式"用的宽度。
    /// 3 位能容纳 100%。补位只解决**字符数**，真正的像素对齐还要靠
    /// <see cref="TextMetrics.PadToWidth"/> 二次微调 —— 见 <see cref="Value"/>。
    /// </summary>
    private const int ValueDigits = 3;

    /// <summary>
    /// 电量栏的候选数字位数，按"宽松优先"的顺序尝试。
    ///
    /// 为什么先按**数字位数**补位、再由 TextMetrics 按像素收尾 —— 这里踩过两次坑：
    ///
    /// 坑一：一开始用 `(p + "%").PadLeft(4)`，字符数是齐了（" 41%" 和 "100%" 都 4 个字符），
    ///   但补在了错误的位置：`"5%"` 被补成 `"  5%"`（2 空格 + 3 字符），
    ///   而 `"100%"` 本身就是 4 个字符 —— 一个含 2 个空格、一个含 0 个空格，
    ///   像素宽度当然对不上。所以现在补的是**数字部分**，`%` 统一跟在后面，
    ///   保证每行的"%位置"语义一致。
    ///
    /// 坑二：以为数字在比例字体里是等宽的，补完位数就该严丝合缝。实测发现不是：
    ///   "  5"=13.04px、" 30"=16.22px、"100"=19.41px（'1' 比 '5' 窄、'0' 比 '3' 宽）。
    ///   所以位数补齐之后，**仍然要在 TextMetrics 里按像素补几个空格**收尾。
    ///   这一步不能省，省了用户截图就会看到名字起点差 3~6px。
    ///
    /// 位数 3 是常规值（容纳 100%），位数 2 只在设备多到 3 位数挤不下时才启用
    /// （那时 p=100 会多占 1 位，但只影响那一行，其余仍对齐）。
    /// </summary>
    private static readonly int[] ValueDigitOptions = { 3, 2 };

    /// <summary>分隔符候选，宽松优先：带空格的好看，不带空格的省字符。</summary>
    private static readonly string[] Gaps = { " | ", "|" };

    /// <summary>标准分隔符：竖线 + 两侧空格，把"电量"和"名字"切成两块。</summary>
    private const string ValueSuffix = " | ";

    /// <summary>
    /// 生成"电量前置 + 像素级对齐"的一版提示；塞不进 63 字符就返回 null。
    ///
    /// 格式：` 41% | GPW 2` / `  5% | OPPO Enco Free4` / `+ 79% | GPW 2`（充电中，数字为真实读数）
    ///
    /// 对齐做法：先算出这批电量文本里最宽的那个的像素宽度（target），
    /// 再给较窄的那些在**左侧补空格**补到同样的像素宽度，最后拼上分隔符和名字。
    ///
    /// 为什么不能只按字符数补（务必别再"优化"回去）：
    ///   Segoe UI 12px 下数字并不严格等宽 —— "  5"=13.04px、" 30"=16.22px、"100"=19.41px。
    ///   光靠 <see cref="Value"/> 里的 PadLeft(3) 让字符数一致，像素宽度仍差 3~6px。
    ///
    /// 代价：补空格要花字符预算，所以台数一多这一版就会塞不下，
    /// 这时退到兜底链：在当前名称上限内压缩电量栏和分隔符，再逐步收窄名称，
    /// 最后减少台数，见 BuildTooltip。
    /// </summary>
    private static string? TryBuildAligned(List<DeviceState> list)
    {
        // 1) 先把每行还没补位的电量部分算出来
        var values = new List<string>(list.Count);
        foreach (var d in list) values.Add(Value(d, ValueDigits));

        // 2) 找到最宽的那个作为统一右边缘
        var target = 0f;
        foreach (var v in values) target = Math.Max(target, TextMetrics.Width(v));

        // 3) 逐行补空格到同一像素宽度
        var lines = new List<string>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var name = Truncate(list[i].Name, DeviceNameLimit);
            lines.Add(TextMetrics.PadToWidth(values[i], target) + ValueSuffix + name);
        }

        var text = string.Join("\n", lines);
        return text.Length <= MaxTooltipLength ? text : null;
    }

    /// <summary>
    /// 电量栏内容：**数字**补到 <paramref name="digits"/> 位，然后统一跟一个 "%"。
    ///
    /// 这里只负责"字符数"层面的对齐，像素层面的微调在
    /// <see cref="TextMetrics.PadToWidth"/> 里做（因为数字不等宽，位数一样宽不一定一样）。
    ///
    /// 充电中的行在电量前带一个 ExternalPowerBadge（ASCII 的 "+"），
    /// 它占 1 个字符，所以那一行比不充电的行宽一点点 —— 这是有意的取舍：
    /// 只用 1 个字符就说明"正在充"，比写全 "充电中"（3 个汉字、6 个字符）划算得多，
    /// 多设备时正是这几字符的差别决定能不能全列出来。
    ///
    /// 唯一例外是"充着电但读不到电量"的设备（比如有线模式的键盘）：
    /// 这时没有数字可显示，只能写"充电中"三个字 —— 有字数总比显示破折号强。
    /// </summary>
    private static string Value(DeviceState d, int digits)
    {
        if (d.Percent is int p)
            return (d.ShowChargeIcon ? DeviceState.ExternalPowerBadge : "") + Pad(p, digits) + "%";

        // 没有读数但在充电：写清状态。宽度比数字栏宽，TryBuildAligned 会给同组其它行
        // 补空格对齐（多设备时若因此塞不下，会走降级链减少台数）。
        if (d.ShowChargeIcon) return "充电中";

        // 读不到电量：提示框只能写字、画不了面板里那个"电池+斜杠"图标，
        // 所以这里是全项目唯一还保留破折号的地方（面板/扫描窗口都已改成图标）。
        return "—".PadLeft(digits);
    }

    /// <summary>
    /// 数字左补空格到指定位数（右对齐）。
    /// 注意是补**空格**不是补零：没电量的行由 <see cref="Value"/> 单独处理成破折号，不会显示 0。
    /// </summary>
    private static string Pad(int value, int digits) => value.ToString().PadLeft(digits);

    /// <summary>按字符数截断，超出用省略号收尾；当 limit &gt; 0 时保证结果长度不超过 limit。</summary>
    internal static string Truncate(string name, int limit)
    {
        if (limit <= 0 || name.Length <= limit) return name;
        return name[..(limit - 1)] + "…";
    }

    /// <summary>
    /// 兜底版的单行。格式与主方案一致（电量前置 + 竖线分栏），只是可选更紧凑：
    /// digits 是电量数字补到几位，gap 是分隔符（标准 " | " 或紧凑 "|"）。
    /// </summary>
    private static string Line(DeviceState d, int nameLimit, string gap, int digits = ValueDigits)
    {
        var name = Truncate(d.Name, nameLimit > 0 ? nameLimit : DeviceNameLimit);
        return Value(d, digits) + gap + name;
    }

    /// <summary>NotifyIcon.Text 的硬上限是 63 字符，超了会抛异常。</summary>
    private const int MaxTooltipLength = 63;

    /// <summary>
    /// 托盘图标要显示的电量。
    ///
    /// 指定了主设备就只看它：它离线/没电量时返回 null（图标画成"灰电池 + 一道横杠"，
    /// 见 <see cref="UI.TrayIcon.RenderIcon"/>），
    /// 而不是悄悄退回"所有设备最低值"—— 那会让用户以为主设备是那个数。
    /// 没指定主设备时保持老行为：所有在线设备里最低的那个。
    ///
    /// 一律用 EffectivePercent，也就是电池的真实读数 —— 充电中不特殊处理，
    /// 插着线读到 79% 就画 79%。是否标红由 <see cref="TrayIsLow"/> 单独判断。
    /// </summary>
    public int? TrayPercent()
    {
        if (Primary is { } primary) return primary.Online ? primary.EffectivePercent : null;

        var values = _all.Where(d => d.Online && d.EffectivePercent.HasValue)
            .Select(d => d.EffectivePercent!.Value).ToList();
        return values.Count == 0 ? null : values.Min();
    }

    /// <summary>
    /// 托盘图标是否用低电量红色。
    ///
    /// 判据是"有没有设备真的低电量"，而不是"托盘上那个数字是否低于阈值"：
    /// 否则最低的那台正在充电（比如 5% 正在充）会把图标标红，
    /// 而它明明正在被充上，这时候报警告纯属添乱。
    /// <see cref="DeviceState.IsLow"/> 已经排除了充电中的设备，并且用的是
    /// 每台设备自己的阈值（由 <see cref="ApplyLowThreshold"/> 从配置同步）。
    ///
    /// 指定主设备时**只看主设备**：其它设备低电量不会让托盘标红
    /// （用户既然指定了主设备，就是要以它为准；否则图标说"红"、面板却找不到哪台红了）。
    /// </summary>
    public bool TrayIsLow()
    {
        if (Primary is { } primary) return primary.Online && primary.IsLow;
        return _all.Any(d => d.Online && d.IsLow);
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
