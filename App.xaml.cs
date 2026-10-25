using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using PeripheralBattery.Models;
using PeripheralBattery.Services;
using PeripheralBattery.UI;

namespace PeripheralBattery;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private TrayIcon? _tray;
    private PopupWindow? _popup;
    private BatteryMonitor? _monitor;
    private AppConfig _config = new();
    private bool _settingsOpen;
    private readonly LowBatteryWatcher _lowBattery = new();

    public App()
    {
        // 托盘程序没有控制台，任何异常都要留痕，并且尽量别让进程直接消失。
        // 三个入口覆盖三类异常：
        //   · DispatcherUnhandledException —— UI 线程上的异常（最常见），标记 Handled 让程序活下去；
        //   · UnhandledException —— 非 UI 线程上没人接的异常，此时进程必死，只能抢救日志；
        //   · UnobservedTaskException —— 后台 Task 里被吞掉的异常，记下来免得静默失败。
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI 线程", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未捕获", args.ExceptionObject as Exception ?? new Exception("unknown"));
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            Log.Error("后台任务", args.Exception);

        // 排查"进程静默消失"用的痕迹：托管路径的退出（Environment.Exit / 主窗口关闭 /
        // 未处理异常后的卸载）都会触发 ProcessExit；如果日志里有这行，说明走的是
        // 托管退出，去查谁调用的；如果连这行都没有，就是被强杀或原生层崩溃。
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log.Write($"进程退出（托管路径）线程={Environment.CurrentManagedThreadId}");
        };
    }

    /// <summary>
    /// 程序入口。先处理一排"诊断模式"命令行参数（跑完就退出，不进托盘），
    /// 全部没命中才是正常启动：单实例检查 → 主题 → 配置 → 托盘 + 轮询。
    ///
    /// 诊断模式的共同点：都必须在**建托盘图标之前**返回，否则会残留一个图标；
    /// 且都打开了 ConfigStore.ReadOnly，避免跑一次诊断就把用户真实配置覆盖掉。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Write("启动: " + string.Join(' ', e.Args));

        // 诊断类模式（--probe / --selftest / --ui-shot）一律不写配置：
        // 它们会构造 Monitor 并调用 SetPrimary / Remove 这些带 SaveNow 的方法，
        // 不拦的话跑一次诊断就把用户真实的设备名和主设备设置覆盖了。
        if (e.Args.Any(a => a.StartsWith("--probe") || a is "--selftest" or "--ui-shot"))
            ConfigStore.ReadOnly = true;

        // 诊断模式：只输出设备信息，用于排查"某台设备读不到电量"
        if (e.Args.Contains("--probe"))
        {
            // 必须丢到线程池：WinRT 的 await 续体想回 WPF Dispatcher，
            // 这里同步等待会让 Dispatcher 永远不转，直接死锁。
            System.Threading.Tasks.Task.Run(() => Diagnostics.Probe.RunAsync().GetAwaiter().GetResult())
                .GetAwaiter().GetResult();
            Shutdown(0);
            return;
        }

        // 自检：验证低电量判定、托盘提示这类"看不出对错"的逻辑（--selftest）
        if (e.Args.Contains("--selftest"))
        {
            Diagnostics.SelfTest.Run();
            Shutdown(0);
            return;
        }

        // 诊断模式：OPPO/OnePlus/realme 私有协议探针（--probe-opo [关键字]）
        var opoIndex = Array.IndexOf(e.Args, "--probe-opo");
        if (opoIndex >= 0)
        {
            var keyword = opoIndex + 1 < e.Args.Length ? e.Args[opoIndex + 1] : null;
            System.Threading.Tasks.Task.Run(() => Diagnostics.OpoProbe.RunAsync(keyword).GetAwaiter().GetResult())
                .GetAwaiter().GetResult();
            Shutdown(0);
            return;
        }

        // 诊断模式：HFP AT 指令探针（--probe-hfp [关键字]）
        var hfpIndex = Array.IndexOf(e.Args, "--probe-hfp");
        if (hfpIndex >= 0)
        {
            var keyword = hfpIndex + 1 < e.Args.Length ? e.Args[hfpIndex + 1] : null;
            System.Threading.Tasks.Task.Run(() => Diagnostics.HfpProbe.RunAsync(keyword).GetAwaiter().GetResult())
                .GetAwaiter().GetResult();
            Shutdown(0);
            return;
        }

        // 诊断模式：SetupAPI 属性探针（--probe-setup [关键字]）
        var setupIndex = Array.IndexOf(e.Args, "--probe-setup");
        if (setupIndex >= 0)
        {
            Diagnostics.SetupProbe.Run(setupIndex + 1 < e.Args.Length ? e.Args[setupIndex + 1] : null);
            Shutdown(0);
            return;
        }

        // 诊断模式：耳机专项探针（--probe-headset [关键字]）
        var headsetIndex = Array.IndexOf(e.Args, "--probe-headset");
        if (headsetIndex >= 0)
        {
            var keyword = headsetIndex + 1 < e.Args.Length ? e.Args[headsetIndex + 1] : null;
            System.Threading.Tasks.Task.Run(() => Diagnostics.HeadsetProbe.RunAsync(keyword).GetAwaiter().GetResult())
                .GetAwaiter().GetResult();
            Shutdown(0);
            return;
        }

        // 开发用：把面板离屏渲染成图片，核对排版（不弹窗）
        var shotIndex = Array.IndexOf(e.Args, "--ui-shot");
        if (shotIndex >= 0 && shotIndex + 1 < e.Args.Length)
        {
            Theme.Apply();
            var path = e.Args[shotIndex + 1];
            var mode = e.Args.Contains("settings") ? "settings"
                     : e.Args.Contains("scan") ? "scan"
                     : e.Args.Contains("demo") ? "demo" : "";
            // 不阻塞 UI 线程，交给 Dispatcher 排队执行，跑完再退出
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { await Diagnostics.UiShot.RunAsync(path, mode); }
                catch (Exception ex) { Log.Error("ui-shot", ex); }
                finally { Shutdown(0); }
            });
            return;
        }

        if (!TakeSingleInstance())
        {
            Log.Write("已有实例在运行，本次启动退出");
            return;
        }

        Theme.Apply();
        _config = ConfigStore.Load();
        StartupManager.Apply(_config.AutoStart);   // 让注册表和配置保持一致
        Log.Write($"配置就绪: 间隔={_config.PollSeconds}s 自启={_config.AutoStart} 已知设备={_config.Devices.Count}");

        _monitor = new BatteryMonitor(_config, Dispatcher);
        _monitor.Changed += UpdateTray;

        _tray = new TrayIcon();
        _tray.Clicked += TogglePopup;
        _tray.RefreshRequested += () => _ = _monitor.RefreshAsync(true);
        _tray.SettingsRequested += ShowSettings;
        _tray.ExitRequested += ExitApp;

        // 休眠唤醒 / 解锁后立刻补一次数据，别让用户看到过期电量
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _monitor.Start();
        Log.Write("托盘与轮询已启动");

        // 手动双击启动时把面板弹出来；开机自启带 --tray 参数，静默驻留
        if (!e.Args.Contains("--tray")) TogglePopup();
    }

    /// <summary>
    /// 单实例：第二次启动只负责把已有实例的面板叫出来。
    ///
    /// 用两个内核对象配合：一个 Mutex 判"是不是第一个"，一个 EventWaitHandle 传"请把面板弹出来"。
    /// 为什么不用窗口消息：两个实例可能在不同权限/不同桌面上，消息容易被吞；
    /// 命名事件是 Local\ 命名空间（同一登录会话内可见），足够且更可靠。
    /// Mutex 用 Local\ 而不是 Global\：每个用户各一份，多用户登录互不干扰。
    /// </summary>
    private bool TakeSingleInstance()
    {
        const string mutexName = @"Local\PeripheralBattery.Mutex";
        const string signalName = @"Local\PeripheralBattery.Show";

        _mutex = new Mutex(true, mutexName, out var created);
        if (!created)
        {
            // 已有实例在跑：给它发个信号，然后自己退出
            try { EventWaitHandle.OpenExisting(signalName).Set(); }
            catch { /* 对方还没建好信号，忽略 */ }
            Shutdown(0);
            return false;
        }

        // 第一个实例：建好信号并起一条后台线程守着。收到就切回 UI 线程去开面板
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
        var listener = new Thread(() =>
        {
            try
            {
                while (_showSignal.WaitOne())
                    Dispatcher.BeginInvoke(new Action(TogglePopup));
            }
            catch (ObjectDisposedException)
            {
                // 退出时句柄被释放，正常结束
            }
        })
        { IsBackground = true, Name = "single-instance-signal" };
        listener.Start();
        return true;
    }

    /// <summary>
    /// 点托盘图标 / 第二次启动时调用：面板开着就关、关着就开。
    /// 打开时顺带刷一次，保证看到的是当前值 —— 用户点开面板本来就意味着"现在就想知道"。
    /// </summary>
    private void TogglePopup()
    {
        if (_monitor == null) return;
        _popup ??= new PopupWindow(_monitor, ShowSettings);
        if (_popup.Toggle()) return;   // 刚被自动收起，这次点击当作"关掉"

        // 打开面板就立刻刷一次，保证看到的是当前值
        _ = _monitor.RefreshAsync(manual: true);
    }

    /// <summary>
    /// 每轮轮询结束后由 Monitor 的 Changed 事件触发：更新托盘图标 + 判断要不要弹低电量气泡。
    /// 两个职责放一起是因为它们都需要"这一轮的最新数据"，分成两次遍历没必要。
    /// </summary>
    private void UpdateTray()
    {
        if (_tray == null || _monitor == null) return;

        // 显示哪个电量：指定了主设备就显示它，否则"所有在线设备里最低"。
        // 是否标红由 TrayIsLow 单独判断 —— 它会排除正在充电的设备，
        // 不能简单拿"图标上那个数字 ≤ 阈值"来比（那样 5% 正在充电也会标红）。
        _tray.Update(
            _monitor.TrayPercent(),
            _monitor.BuildTooltip(),
            _monitor.TrayIsLow());

        // 低电量提醒：LowBatteryWatcher 负责"该不该弹"，这里只管弹
        if (!_config.NotifyLowBattery) return;
        var low = _lowBattery.Evaluate(_monitor.Online, _config.LowBatteryPercent, enabled: true);
        if (low.Count == 0) return;

        var device = low[0];
        Log.Write($"低电量提醒: {device.Name} {device.Percent}%");
        _tray.ShowBalloon(
            $"{device.Name} 电量低",
            $"剩余 {device.PercentText}，建议尽快充电。点击查看所有设备。",
            warning: true);
    }

    /// <summary>
    /// 打开设置窗口，并在用户点"保存"后把改动落实到正在运行的系统上。
    ///
    /// 顺序是有讲究的：先让 StartupManager 对齐注册表（自启）、再存配置、
    /// 然后让 Monitor 按新参数重排定时器、同步阈值颜色、同步主设备标记，
    /// 最后清掉低电量提醒的记忆 —— 用户多半是因为"阈值定得不合适才总弹"才进设置的，
    /// 不清记忆的话改完阈值也不会立刻重新判定。
    /// </summary>
    private void ShowSettings()
    {
        // 用标志位而不是把窗口置顶/禁用：连点两下托盘不会开出两个设置窗口
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            if (_popup?.IsVisible == true) _popup.Hide();
            var window = new SettingsWindow(_config, _monitor!);
            if (window.ShowDialog() == true)
            {
                StartupManager.Apply(_config.AutoStart);
                ConfigStore.Save(_config);
                _monitor?.RestartTimer();      // 新间隔立即生效
                _monitor?.RefreshTimerHint();
                _monitor?.ApplyLowThreshold(); // 阈值改了，设备行颜色立刻跟上
                _monitor?.SyncPrimaryFromConfig();  // 主设备可能在设置里被改过
                _lowBattery.Reset();           // 允许按新阈值重新提醒
            }
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    /// <summary>从睡眠恢复（合盖唤醒等）时补一次。休眠期间定时器不走，醒来的第一眼必然是旧数据。</summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ = _monitor?.RefreshAsync(true);
    }

    /// <summary>解锁屏幕时补一次（锁屏期间外设可能已经配对/断开）。</summary>
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock) _ = _monitor?.RefreshAsync(true);
    }

    /// <summary>
    /// 退出：先落盘配置，再销毁托盘图标，最后关进程。
    ///
    /// 顺序有讲究：BatteryMonitor 并不持有 TrayIcon（它只通过 Changed 事件通知 App），
    /// 真正的风险是**定时器回调**——如果先 Dispose 图标、再等下一轮轮询，
    /// UpdateTray 会拿到一个已经释放的 NotifyIcon 去更新，抛出来的异常在退出阶段很难看。
    /// 先把配置存好、立刻 Shutdown 缩短这个窗口，比反过来安全。
    /// </summary>
    private void ExitApp()
    {
        _monitor?.SaveNow();
        _tray?.Dispose();
        Shutdown(0);
    }

    /// <summary>
    /// 退出收尾。即使 ExitApp 已经做过一遍，这里也要再做 ——
    /// 用户可能从任务管理器/关机流程直接结束进程，那时只会走 OnExit。
    /// 配置的 SaveNow 是幂等的，重复调用无害。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _monitor?.SaveNow();
        _tray?.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _showSignal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
