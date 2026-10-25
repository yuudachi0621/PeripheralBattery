using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using PeripheralBattery.Models;
using PeripheralBattery.Native;
using PeripheralBattery.Services;

namespace PeripheralBattery.UI;

/// <summary>
/// 「扫描其他设备」窗口。
///
/// 定位：轮询本身只读"已收纳"的设备，所以需要这个入口主动把附近的设备找出来。
/// 它调用的是 <see cref="BatteryMonitor.ScanAsync"/>（走真实通道，不是查配置），
/// 用户勾选后由 <see cref="BatteryMonitor.AddScanned"/> 写进面板。
/// </summary>
public partial class ScanWindow : Window
{
    private readonly BatteryMonitor _monitor;
    private readonly ObservableCollection<ScanItem> _items = new();
    private bool _scanning;

    public ScanWindow(BatteryMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();

        ResultList.ItemsSource = _items;
        SourceInitialized += (_, _) =>
            WindowFx.UseDarkTitleBar(new WindowInteropHelper(this).Handle, Theme.IsDark);

        // 集合增删（扫描完成/重扫）后刷新按钮状态；
        // 注意勾选变化不经过这里 —— 那条由 OnItemChanged 监听单个条目的 Selected 处理。
        _items.CollectionChanged += (_, _) => RefreshAddButton();

        // 打开即自动扫一次，省掉一次多余的点击
        Loaded += async (_, _) => await ScanAsync();
    }

    /// <summary>用户点了添加时的结果，给设置页看。</summary>
    public int AddedCount { get; private set; }

    private async System.Threading.Tasks.Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        ScanButton.IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;

        try
        {
            var found = await _monitor.ScanAsync();

            _items.Clear();
            foreach (var item in found)
            {
                item.PropertyChanged += OnItemChanged;
                _items.Add(item);
            }

            EmptyText.Text = found.Count == 0
                ? "没有找到外设。请确认键盘鼠标已开机、蓝牙耳机/手柄已连接后重试。\n" +
                  "如果设备本来就不提供电量接口，它也可能不会出现在这里。"
                : "";
            EmptyText.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Error("扫描设备", ex);
            EmptyText.Text = "扫描失败：" + ex.Message;
            EmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            _scanning = false;
            ScanButton.IsEnabled = true;
            BusyBar.Visibility = Visibility.Collapsed;
            RefreshAddButton();
        }
    }

    private void RefreshAddButton()
    {
        var count = _items.Count(i => i.Selected && !i.AlreadyAdded);
        AddButton.IsEnabled = count > 0;
        AddButton.Content = count > 0 ? $"添加所选（{count}）" : "添加所选";
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanItem.Selected)) RefreshAddButton();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    /// <summary>供 --ui-shot 使用：不弹窗也能触发一次真实扫描。</summary>
    internal System.Threading.Tasks.Task RunScanForTestAsync() => ScanAsync();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _items.Where(i => i.Selected && !i.AlreadyAdded).ToList();
        if (chosen.Count == 0) return;

        AddedCount = _monitor.AddScanned(chosen);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
