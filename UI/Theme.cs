using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Media;
using PeripheralBattery.Models;
using Color = System.Drawing.Color;

namespace PeripheralBattery.UI;

/// <summary>
/// 主题：Claude 风格配色；启动时读取 Windows 的浅色/深色设置。
///
/// 设计取自 Claude 官网的视觉语言：
///   · 底色是带一点暖意的米白/纸色，而不是纯白或冷灰；
///   · 强调色是低饱和的陶土橙（#D97757 一带），用在交互元素上；
///   · 文字是暖黑，次要文字用中性暖灰，层级靠明度而不是靠加粗；
///   · 圆角偏大、不用阴影、分割线极淡 —— 整体安静，不抢注意力。
///
/// 只做一套颜色键，XAML 里统一用 DynamicResource 引用。当前没有监听系统主题变化；
/// 若运行期间再次调用 <see cref="Apply"/>，DynamicResource 会采用新颜色。
/// 电量条的三档颜色（LevelBrush）不在这里，见 <see cref="ApplyLevelColors"/> 的说明。
/// </summary>
internal static class Theme
{
    public static bool IsDark { get; private set; }

    public static void Apply()
    {
        IsDark = DetectDark();
        var r = System.Windows.Application.Current.Resources;

        void Set(string key, Color c)
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
            brush.Freeze();
            r[key] = brush;
        }

        if (IsDark)
        {
            // 深色版：把米白翻成暖炭，橙色调亮一档保证对比度
            Set("BgBrush", Color.FromArgb(0x23, 0x21, 0x1E));          // 暖炭底
            Set("RowHoverBrush", Color.FromArgb(0x2F, 0x2C, 0x28));
            Set("CardBrush", Color.FromArgb(0x2A, 0x27, 0x23));
            Set("TextPrimaryBrush", Color.FromArgb(0xF0, 0xEC, 0xE5));  // 暖白
            Set("TextSecondaryBrush", Color.FromArgb(0x9C, 0x95, 0x89));
            Set("DividerBrush", Color.FromArgb(0x38, 0x34, 0x2F));
            Set("TrackBrush", Color.FromArgb(0x3C, 0x38, 0x32));
            Set("AccentBrush", Color.FromArgb(0xE0, 0x8A, 0x68));       // 提亮的陶土橙
            Set("AccentSoftBrush", Color.FromArgb(0x4A, 0x36, 0x2D));   // 强调色的淡底
            Set("DangerBrush", Color.FromArgb(0xE0, 0x6C, 0x62));
            Set("SuccessBrush", Color.FromArgb(0x8A, 0xB8, 0x76));       // 暖绿（"已完成"类状态）
        }
        else
        {
            // 浅色版：Claude 的纸色 + 陶土橙
            Set("BgBrush", Color.FromArgb(0xFA, 0xF9, 0xF5));          // 暖米白（纸）
            Set("RowHoverBrush", Color.FromArgb(0xF0, 0xEE, 0xE7));    // 悬停：略深一档
            Set("CardBrush", Color.FromArgb(0xFF, 0xFF, 0xFF));
            Set("TextPrimaryBrush", Color.FromArgb(0x1F, 0x1E, 0x1B)); // 暖黑
            Set("TextSecondaryBrush", Color.FromArgb(0x73, 0x6F, 0x66));
            Set("DividerBrush", Color.FromArgb(0xE8, 0xE4, 0xDA));
            Set("TrackBrush", Color.FromArgb(0xE6, 0xE2, 0xD8));
            Set("AccentBrush", Color.FromArgb(0xD9, 0x77, 0x57));      // 陶土橙
            Set("AccentSoftBrush", Color.FromArgb(0xFA, 0xEE, 0xE8));  // 强调色的淡底
            Set("DangerBrush", Color.FromArgb(0xC0, 0x4B, 0x3F));
            Set("SuccessBrush", Color.FromArgb(0x6D, 0x9E, 0x5C));       // 暖绿（"已完成"类状态）
        }

        ApplyLevelColors(IsDark);
    }

    /// <summary>
    /// 电量条颜色的语义：高电量不该是"成功绿"，因为整体是暖色系。
    /// 改成——充足=沉稳的暖绿，中等=琥珀，低=警示红，未知=灰色。
    /// </summary>
    private static void ApplyLevelColors(bool dark)
    {
        Color high = dark ? Color.FromArgb(0x7F, 0xB1, 0x6E) : Color.FromArgb(0x6D, 0x9E, 0x5C);
        Color mid = dark ? Color.FromArgb(0xD9, 0xA0, 0x4C) : Color.FromArgb(0xC8, 0x8B, 0x3A);
        Color low = dark ? Color.FromArgb(0xE0, 0x6C, 0x62) : Color.FromArgb(0xC0, 0x4B, 0x3F);
        Color muted = dark ? Color.FromArgb(0x8A, 0x84, 0x79) : Color.FromArgb(0x9A, 0x94, 0x88);
        Palettes.SetLevels(high, mid, low, muted);
    }

    private static bool DetectDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
