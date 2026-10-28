using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PeripheralBattery.UI;

/// <summary>
/// 电量百分比 → Grid 星号宽度，用来画 0 代码的进度条。
///
/// 用法（见 PopupWindow.xaml 的设备行）：把一个 Grid 拆成两列，左列绑电量、右列绑"剩余"，
/// 两个列宽都用 GridUnitType.Star。WPF 按权重分配可用宽度，
/// 于是左列自然占 70%、右列占 30% —— 不需要 ProgressBar，也不需要算像素。
///
/// 好处是宽度完全跟随外层容器（面板宽度变了、文字变了都自动重算），
/// 我们只管往左列填一个跟电量同色的 Border。
///
/// 传进来的值不是 int（比如还没读到电量的 null）时按 0 处理，条就是空的。
/// </summary>
public sealed class LevelStarConverter : IValueConverter
{
    /// <summary>true = 返回"剩余部分"的宽度（100 - 电量）。同一个转换器类靠这个标记区分左右两列。</summary>
    public bool Rest { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Clamp 兜住异常值：设备偶尔会上报 255 之类的脏数据，不夹一下会把星号权重撑爆
        var percent = value is int p ? Math.Clamp(p, 0, 100) : 0;
        if (Rest) return new GridLength(100 - percent, GridUnitType.Star);
        return new GridLength(percent, GridUnitType.Star);
    }

    /// <summary>该转换器只用于显示，不支持反向转换。</summary>
    /// <exception cref="NotSupportedException">始终抛出。</exception>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// bool → Visibility。默认 true 显示、false 折叠（Collapsed 而不是 Hidden：
/// 折叠的元素不占布局空间，这正是"文字和输入框二选一""有主设备才显示那行小字"需要的）。
///
/// Invert=true 时语义反过来，用来表达"不处于某状态时才显示"——
/// 比如"不在编辑中才显示名字"，这样一对转换器就能覆盖界面里所有显隐需求。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>true = 把布尔值取反再转（false → 显示）。</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>该转换器只用于显示，不支持反向转换。</summary>
    /// <exception cref="NotSupportedException">始终抛出。</exception>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
