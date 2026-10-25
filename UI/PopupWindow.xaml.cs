using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PeripheralBattery.Models;
using PeripheralBattery.Native;
using PeripheralBattery.Services;

namespace PeripheralBattery.UI;

/// <summary>
/// 点击托盘图标弹出的面板。行为参照 Twinkle Tray：
/// 失去焦点自动收起、Esc 关闭、贴在工作区右下角、内容变化时保持底部对齐。
/// </summary>
public partial class PopupWindow : Window
{
    private readonly BatteryMonitor _monitor;
    private readonly Action _openSettings;
    private long _shownAt;
    private long _hiddenAt;
    private bool _refreshing;

    public PopupWindow(BatteryMonitor monitor, Action openSettings)
    {
        _monitor = monitor;
        _openSettings = openSettings;

        InitializeComponent();
        DataContext = monitor;

        SourceInitialized += (_, _) => WindowFx.RoundCorners(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => Position();
        SizeChanged += (_, _) => { if (IsVisible) Position(); };
        Deactivated += OnDeactivated;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideSafely(); };
    }

    /// <summary>切换显示：已经打开就收起。返回 true 表示这次调用被"忽略"了（什么也没做）。</summary>
    public bool Toggle()
    {
        if (IsVisible)
        {
            HideSafely();
            return false;
        }

        // 点托盘图标时，面板会先因为"失去焦点"自己收起，紧接着才收到这次点击。
        // 这段时间内的 Toggle 一律当作"用户是要关掉"，否则会出现关不掉的现象。
        if (Environment.TickCount64 - _hiddenAt < 300) return true;

        _shownAt = Environment.TickCount64;
        Show();
        UpdateLayout();
        Position();
        Activate();
        return false;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // 弹出瞬间系统可能还没把焦点交过来，此时收到的 Deactivated 不能当作用户点了别处
        if (Environment.TickCount64 - _shownAt < 250) return;
        HideSafely();
    }

    /// <summary>贴住工作区右下角（任务栏上方）。</summary>
    private void Position()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 8;
        Top = Math.Max(area.Top + 8, area.Bottom - ActualHeight - 8);
    }

    private void HideSafely()
    {
        // 正在改名时先落库，避免把输入丢掉
        foreach (var state in _monitor.Online.Concat(_monitor.Offline))
            if (state.IsEditing) _monitor.Rename(state, state.EditName);

        _hiddenAt = Environment.TickCount64;
        Hide();
    }

    // ---------------- 立即刷新 + 反馈 ----------------

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshWithFeedbackAsync();

    private async void Menu_Refresh_Click(object sender, RoutedEventArgs e) => await RefreshWithFeedbackAsync();

    /// <summary>
    /// 刷新并给出可见反馈：图标转一圈、颜色转成强调色，完成后停住。
    ///
    /// 为什么要这么绕：轮询本身可能只要几百毫秒，一眨眼就结束了，
    /// 用户会觉得"点了没反应"。所以设一个最短动画时长（600ms），
    /// 保证一定会看到动作；期间再点会被 _refreshing 挡掉，避免叠加。
    /// </summary>
    private async System.Threading.Tasks.Task RefreshWithFeedbackAsync()
    {
        if (_refreshing) return;
        _refreshing = true;

        var accent = (Brush)FindResource("AccentBrush");
        var secondary = (Brush)FindResource("TextSecondaryBrush");
        RefreshGlyph.Stroke = accent;

        try
        {
            // 连续旋转：一圈 0.6s，跑完再停
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(600))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            var story = new Storyboard { FillBehavior = FillBehavior.Stop };
            Storyboard.SetTarget(spin, RefreshRotate);
            Storyboard.SetTargetProperty(spin, new PropertyPath(RotateTransform.AngleProperty));
            story.Children.Add(spin);
            story.Begin();

            var work = _monitor.RefreshAsync(manual: true);
            var minimum = System.Threading.Tasks.Task.Delay(600);
            await System.Threading.Tasks.Task.WhenAll(work, minimum);
        }
        finally
        {
            RefreshGlyph.Stroke = secondary;
            _refreshing = false;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _openSettings();

    // ---------------- 主设备 ----------------

    private void Menu_SetPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (FindState(sender) is not DeviceState state) return;
        _monitor.SetPrimary(state);
    }

    private void Menu_ClearPrimary_Click(object sender, RoutedEventArgs e) => _monitor.SetPrimary(null);

    // ---------------- 从面板移除 ----------------

    private async void Menu_Remove_Click(object sender, RoutedEventArgs e)
    {
        if (FindState(sender) is not DeviceState state) return;

        var wasPrimary = state.Id == _monitor.Primary?.Id;
        _monitor.Remove(state);

        // 移除的是主设备 → 托盘图标要立刻放弃它，否则还会显示一个不存在的设备
        if (wasPrimary) _monitor.SetPrimary(null);

        // 底部给一句提示，告诉用户"删了还能找回来"，不然会以为彻底丢了
        await FlashFooterAsync("已从面板移除，可在设置里重新扫描添加");
    }

    /// <summary>底部状态栏临时显示一句提示（盖住正常的底部文案），2.5 秒后恢复。</summary>
    private async System.Threading.Tasks.Task FlashFooterAsync(string message)
    {
        FlashBar.Text = message;
        FlashBar.Visibility = Visibility.Visible;
        FooterText.Visibility = Visibility.Collapsed;
        await System.Threading.Tasks.Task.Delay(2500);
        FlashBar.Visibility = Visibility.Collapsed;
        FooterText.Visibility = Visibility.Visible;
    }

    // ---------------- 双击 / 右键 重命名 ----------------

    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        BeginEdit((sender as FrameworkElement)?.DataContext as DeviceState);
    }

    private void Menu_Rename_Click(object sender, RoutedEventArgs e) => BeginEdit(FindState(sender));

    private void Menu_ResetName_Click(object sender, RoutedEventArgs e)
    {
        if (FindState(sender) is not DeviceState state) return;
        _monitor.Rename(state, null);   // 空名 = 回到设备自报的名字
    }

    private void BeginEdit(DeviceState? state)
    {
        if (state == null) return;
        state.EditName = state.Name;
        state.IsEditing = true;
    }

    /// <summary>菜单命令的发送者是 MenuItem，得顺着它的数据上下文找到对应的设备。</summary>
    private static DeviceState? FindState(object sender) => sender switch
    {
        MenuItem item => item.DataContext as DeviceState,
        FrameworkElement element => element.DataContext as DeviceState,
        _ => null,
    };

    private void Rename_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible) return;
        // 等布局完成再抢焦点，否则 Focus() 会落空
        box.Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            box.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void Rename_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not DeviceState state) return;

        if (e.Key == Key.Enter)
        {
            _monitor.Rename(state, state.EditName);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            state.IsEditing = false;   // 取消：不改名
            e.Handled = true;
        }
    }

    private void Rename_LostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceState state && state.IsEditing)
            _monitor.Rename(state, state.EditName);
    }
}
