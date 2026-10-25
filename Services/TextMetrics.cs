using System;
using System.Drawing;
using System.Drawing.Text;

namespace PeripheralBattery.Services;

/// <summary>
/// 测量文本在**系统提示框字体**下的真实像素宽度。
///
/// 为什么需要它：托盘悬停提示（NotifyIcon.Text）由系统 ToolTip 控件绘制，
/// 字体是比例字体 —— 实测 Segoe UI 12px 下空格约 3px、数字约 6px、字母更宽。
/// 而且不止"空格和数字不一样宽"，**数字之间也不一样宽**（见 <see cref="PadToWidth"/>），
/// 所以任何按字符数/列数补空格的方案都补不准。
///
/// 现在的用法：悬停提示是"电量前置"格式（<c> 41% | GPW 2</c>），
/// 用 <see cref="Width"/> 找出这批电量里最宽的那个，再用 <see cref="PadToWidth"/>
/// 把其余行补到同一像素宽度，竖线和名字就自然对齐成一列。
///
/// 字体取 SystemFonts.MessageBoxFont 的近似值：系统提示框用的是
/// "MS Shell Dlg" 映射字体，中文环境下实测与 Segoe UI 12px 的度量吻合
/// （截图反推：'OPPO Enco Free4 20%' 实测 116px，Segoe UI 12px 算出 117px）。
///
/// 注意：这里算的是"字符本身画出来多宽"，不含折行、不含提示框内边距 ——
/// 我们只用它做**行内相对对齐**，绝对宽度差 1px 不影响结论。
/// </summary>
internal static class TextMetrics
{
    // 提示框字号：系统默认 9pt，在 100% 缩放下约等于 12px
    private const float TooltipFontSize = 12f;
    private const string TooltipFontFamily = "Segoe UI";

    // Bitmap 只在这里当"测量画布"用，1×1 就够，不参与显示
    private static readonly Bitmap Canvas = new(1, 1);

    private static readonly Font MeasureFont = new(
        TooltipFontFamily, TooltipFontSize, FontStyle.Regular, GraphicsUnit.Pixel);

    private static readonly Graphics G = Graphics.FromImage(Canvas);

    static TextMetrics()
    {
        // 用 ClearType 的前提下的抗锯齿度量，跟系统渲染一致性最好
        G.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        G.PageUnit = GraphicsUnit.Pixel;
    }

    /// <summary>
    /// 一个字符串渲染出来有多宽（像素）。
    ///
    /// 用 MeasureString + 关闭"两端多余留白"，否则 GDI+ 会额外加上
    /// 一点边距，多个字符累加后误差能到几个像素。
    /// </summary>
    public static float Width(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var format = StringFormat.GenericTypographic;
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;   // 尾部空格也要算进去
        return G.MeasureString(text, MeasureFont, int.MaxValue, format).Width;
    }

    /// <summary>
    /// 要把 <paramref name="current"/> 补到 <paramref name="targetWidth"/> 像素宽，需要几个空格。
    ///
    /// 这里用 Ceiling 而不是 Round：空格宽约 3px 时缺口常常除不尽
    /// （比如差 58px 需要 19.33 个空格），四舍五入会少补 1px，
    /// 多行之间累计就是肉眼可见的错位（实测两行差 1px 时看着就别扭）。
    /// 宁可多补 1 个空格（误差被"左补"吸收在电量数字左边，看不出来），
    /// 也不要少补导致右边缘参差不齐。
    ///
    /// 上限 40 个空格是保险：防止某种极端字体度量下补出超长字符串，把 63 字符预算一次吃光。
    /// </summary>
    public static int SpacesToPad(string current, float targetWidth)
    {
        var gap = targetWidth - Width(current);
        if (gap <= 0) return 0;

        var spaceWidth = Width(" ");
        if (spaceWidth <= 0) return 0;

        return Math.Clamp((int)Math.Ceiling(gap / spaceWidth), 0, 40);
    }

    /// <summary>
    /// 把 <paramref name="text"/> 左侧补空格补到至少 <paramref name="targetWidth"/> 像素宽。
    ///
    /// 为什么必须按像素、不能按字符数补 —— 这条是实测出来的，记下来防止再改回去：
    ///   Segoe UI 12px 下数字**并不严格等宽**：
    ///     "  5" = 13.04px、" 30" = 16.22px、" 41" = 16.22px、"100" = 19.41px
    ///   也就是 '1' 比 '5' 窄、'0' 比 '3' 宽（ClearType hinting 的结果）。
    ///   所以 `PadLeft(3)` 补齐字符数之后，三行的像素宽度仍然差 3px 左右，
    ///   用户截图里看到的"没对齐"就是这么来的。
    ///
    /// 补空格而不是补别的字符，是因为空格最窄（3px），
    /// 同样的像素缺口占用的字符串长度最少 —— 在 63 字符的预算下这很关键。
    /// </summary>
    public static string PadToWidth(string text, float targetWidth)
    {
        var pad = SpacesToPad(text, targetWidth);
        return pad <= 0 ? text : new string(' ', pad) + text;
    }
}
