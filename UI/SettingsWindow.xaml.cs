using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using PeripheralBattery.Models;
using PeripheralBattery.Native;
using PeripheralBattery.Services;

namespace PeripheralBattery.UI;

/// <summary>设置窗口：轮询间隔 + 低电量提醒 + 托盘图标设备 + 设备列表 + 自启动 + 显示选项。</summary>
public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly BatteryMonitor _monitor;
    private static readonly int[] Presets = { 10, 30, 60, 120, 300 };
    private static readonly int[] Thresholds = { 10, 15, 20, 30 };

    public SettingsWindow(AppConfig config, BatteryMonitor monitor)
    {
        _config = config;
        _monitor = monitor;
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            WindowFx.UseDarkTitleBar(handle, Theme.IsDark);
        };

        foreach (var seconds in Presets)
        {
            var text = seconds < 60 ? $"{seconds} 秒" : $"{seconds / 60} 分钟";
            if (seconds == 60) text += "（推荐）";
            IntervalBox.Items.Add(new ComboBoxItem { Content = text, Tag = seconds });
        }

        // 当前值可能不在预设里（手改过配置），补一个自定义项
        var current = IntervalBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (int)i.Tag == config.PollSeconds);
        if (current == null)
        {
            current = new ComboBoxItem { Content = $"{config.PollSeconds} 秒（自定义）", Tag = config.PollSeconds };
            IntervalBox.Items.Add(current);
        }
        IntervalBox.SelectedItem = current;

        foreach (var percent in Thresholds)
            ThresholdBox.Items.Add(new ComboBoxItem { Content = $"{percent}%", Tag = percent });

        var currentThreshold = ThresholdBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (int)i.Tag == config.LowBatteryPercent);
        if (currentThreshold == null)
        {
            currentThreshold = new ComboBoxItem { Content = $"{config.LowBatteryPercent}%", Tag = config.LowBatteryPercent };
            ThresholdBox.Items.Add(currentThreshold);
        }
        ThresholdBox.SelectedItem = currentThreshold;

        NotifyBox.IsChecked = config.NotifyLowBattery;
        AutoStartBox.IsChecked = config.AutoStart;
        ShowOfflineBox.IsChecked = config.ShowOffline;
        ThrottleBox.IsChecked = config.ThrottleWhenIdle;

        FillPrimaryBox();

        // 关掉提醒开关时，阈值选择也该变灰，避免误解为"关了还有效"
        NotifyBox.Checked += (_, _) => ThresholdBox.IsEnabled = true;
        NotifyBox.Unchecked += (_, _) => ThresholdBox.IsEnabled = false;
        ThresholdBox.IsEnabled = config.NotifyLowBattery;

        HintText.Text = "配置文件：" + ConfigStore.FilePath;
    }

    /// <summary>离屏渲染（--ui-shot）不会触发 SizeChanged，给渲染工具一个入口。</summary>
    internal void PrepareForRender(double windowWidth)
    {
        // 说明文字的宽度已经在 XAML 里用 MaxWidth 静态限制了，这里不需要额外处理。
        // 保留这个方法是为了让渲染工具有个明确的钩子，将来有依赖尺寸的排版再塞进来。
        _ = windowWidth;
    }

    /// <summary>
    /// 重建"托盘图标显示哪个设备"的下拉项。
    /// 扫描添加完设备后要重新调一次，否则新加的设备不会出现在选项里。
    /// </summary>
    private void FillPrimaryBox()
    {
        var previous = (PrimaryBox.SelectedItem as ScanOption)?.Id
                       ?? _config.PrimaryDeviceId;

        PrimaryBox.Items.Clear();
        PrimaryBox.Items.Add(new ScanOption("", "自动（所有设备里的最低电量）"));

        foreach (var device in _monitor.AllDevices
                     .OrderByDescending(d => d.Online)
                     .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var suffix = device.Online ? "" : "（离线）";
            PrimaryBox.Items.Add(new ScanOption(device.Id, device.Name + suffix));
        }

        var match = PrimaryBox.Items.OfType<ScanOption>().FirstOrDefault(o => o.Id == previous)
                    ?? PrimaryBox.Items.OfType<ScanOption>().First();
        PrimaryBox.SelectedItem = match;
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        var window = new ScanWindow(_monitor) { Owner = this };
        if (window.ShowDialog() != true) return;

        // 新设备进了面板 → 下拉框要能选到它
        FillPrimaryBox();

        if (window.AddedCount > 0)
            HintText.Text = $"已添加 {window.AddedCount} 台设备。";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (IntervalBox.SelectedItem is ComboBoxItem interval && interval.Tag is int seconds)
            _config.PollSeconds = seconds;

        if (ThresholdBox.SelectedItem is ComboBoxItem threshold && threshold.Tag is int percent)
            _config.LowBatteryPercent = percent;

        _config.NotifyLowBattery = NotifyBox.IsChecked == true;
        _config.AutoStart = AutoStartBox.IsChecked == true;
        _config.ShowOffline = ShowOfflineBox.IsChecked == true;
        _config.ThrottleWhenIdle = ThrottleBox.IsChecked == true;

        // 主设备在这里只改配置；界面上的 IsPrimary 标记和托盘图标由 App 在保存后统一同步
        if (PrimaryBox.SelectedItem is ScanOption option) _config.PrimaryDeviceId = option.Id;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
