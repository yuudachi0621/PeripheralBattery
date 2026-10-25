using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace PeripheralBattery.UI;

/// <summary>
/// 托盘图标：在 16×16 里画一个电池，显示"该关注的那个电量"。
/// 具体显示谁的数由 <see cref="BatteryMonitor.TrayPercent"/> 决定 ——
/// 设了主设备就是主设备的电量，没设就是所有在线设备里的最低值（和 Twinkle Tray 一样，一个图标看全局）。
///
/// 只有在电量档位（按 5% 分档）或低电量状态变化时才重新生成图标；
/// 生成后由 <see cref="SwapIcon"/> 换掉 NotifyIcon 的图标并 Dispose 上一张，
/// 所以连续更新不会累积 GDI 句柄。
///
/// 低电量时图标变红并加感叹号角标：图标只有 16×16，光靠颜色在任务栏上很难一眼看出。
///
/// **本类不做任何业务判断**：显示哪个电量、算不算低电量，全由调用方算好传进来。
/// 早先这里自己拿阈值比大小，结果和 BatteryMonitor.TrayIsLow 那套判据（会排除正在充电的设备）
/// 打了架 —— "5% 但正在充电"的设备会把图标标红。现在只接收一个 isLow 布尔值。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly Forms.NotifyIcon _notify;
    private Icon? _icon;

    /// <summary>上一次的缓存键：(电量档位, 是否低电量)。任一变化才重画。</summary>
    private (int bucket, bool low) _lastKey = (int.MinValue, false);

    public event Action? Clicked;
    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("立即刷新", null, (_, _) => RefreshRequested?.Invoke());
        menu.Items.Add("设置…", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _notify = new Forms.NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = menu,
            Text = "外设电量",
        };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke();
        };

        // 点气泡正文 = 打开面板看细节
        _notify.BalloonTipClicked += (_, _) => Clicked?.Invoke();
    }

    /// <summary>
    /// 更新图标与悬停提示。
    /// </summary>
    /// <param name="percent">
    /// 要显示的电量（主设备或全局最低，由调用方决定）。null 表示没数可显示，
    /// 图标画成"带横杠的灰电池"。
    /// </param>
    /// <param name="tooltip">悬停提示文字，内部会再按 63 字符截断一次兜底。</param>
    /// <param name="low">
    /// 是否按"低电量"样式画（红电池 + 感叹号角标）。
    /// 由 <see cref="BatteryMonitor.TrayIsLow"/> 算 —— 它会排除正在充电的设备，
    /// 这个判断不能放在本类里，否则两套口径会打架。
    /// </param>
    public void Update(int? percent, string tooltip, bool low)
    {
        // NotifyIcon.Text 上限 63 个字符，超了会抛异常。
        // BuildTooltip 已经保证不超，这里是最后一道保险（不这么做的话一个异常就能把托盘图标弄丢）
        _notify.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;

        // 按 5% 分档缓存：每 1% 都重画毫无意义（16×16 上根本看不出差别），还费 GDI 句柄
        var bucket = percent is null ? -1 : percent.Value / 5;

        var key = (bucket, low);
        if (key == _lastKey && _icon != null) return;   // 没变化就别重画
        _lastKey = key;
        SwapIcon(RenderIcon(percent, low));
    }

    /// <summary>弹一条 Windows 通知气泡（Win10/11 会渲染成系统通知）。</summary>
    public void ShowBalloon(string title, string text, bool warning)
    {
        _notify.ShowBalloonTip(8000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    private void SwapIcon(Icon icon)
    {
        var old = _icon;
        _icon = icon;
        _notify.Icon = icon;
        old?.Dispose();
    }

    /// <summary>
    /// 画托盘图标：一个**占满画布**的电池 + 电量填充（low=true 时用警示红并带感叹号角标）。
    ///
    /// 为什么是三层不透明色块而不是"描边轮廓"（这是用户反馈"看不清"之后重画的）：
    /// 托盘实际只给 16×16 像素，1px 轮廓线在深色任务栏上会糊成一条灰边，
    /// 低电量时填充区又窄，整体看起来只剩一个空框。改成大面积色块以后，
    /// 无论任务栏是深是浅、图标被缩到多小，都能一眼看出"还有多少"。
    ///
    /// 三层次序（每一层都在不同背景下承担可见性）：
    ///   1. 外壳：不透明中灰 —— 纯白和纯黑背景上都比背景亮/暗得出来，界定电池形状；
    ///   2. 内槽：深灰 —— 和外壳拉开对比，"没电的部分"看得出来是空的而不是没画；
    ///   3. 填充：按电量分档的亮色（绿/琥珀/红），缩到 16px 仍保证至少 1.5px 宽。
    ///
    /// 配色刻意比面板里的电量条更亮一档：面板是米白底，托盘多半是深色任务栏，
    /// 同一套颜色在两边看起来的亮度不一样。
    /// </summary>
    internal static Icon RenderIcon(int? percent, bool low = false)
    {
        // 画布故意用 32×32（系统缩到 16×16 显示）：直接按 16 画的话，
        // 高分屏下系统会放大导致发虚，按 2 倍画再缩才是清晰的。
        const int size = 32;

        var accent = low
            ? TrayRed
            : percent switch
            {
                null => TrayGray,
                <= 15 => TrayRed,
                <= 40 => TrayAmber,
                _ => TrayGreen,
            };

        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // 电池体（横向占满，右侧留 5px 给正极）+ 正极触点
        var body = new RectangleF(1f, 7f, 26f, 18f);
        var tip = new RectangleF(27f, 13f, 4f, 6f);
        // 内槽：外壳里再收 3px，这一圈就是"电池壳的厚度"
        var slot = new RectangleF(body.X + 3f, body.Y + 3f, body.Width - 6f, body.Height - 6f);

        using (var shell = RoundedRect(body, 4.5f))
        using (var brush = new SolidBrush(TrayShell))
        {
            g.FillPath(brush, shell);
        }
        using (var brush = new SolidBrush(TrayShell))
        {
            g.FillRectangle(brush, tip);
        }

        using (var slotPath = RoundedRect(slot, 2.5f))
        using (var brush = new SolidBrush(TraySlot))
        {
            g.FillPath(brush, slotPath);
        }

        using (var brush = new SolidBrush(accent))
        {
            if (percent is int p && p > 0)
            {
                var fillWidth = slot.Width * Math.Clamp(p, 0, 100) / 100f;
                // 至少画 4px（16px 下 2px）：1% 这种极端值如果画成一条线，缩到 16px 会彻底消失
                fillWidth = Math.Max(fillWidth, 4f);
                using var fill = RoundedRect(
                    new RectangleF(slot.X, slot.Y, Math.Min(fillWidth, slot.Width), slot.Height), 2.5f);
                g.FillPath(brush, fill);
            }
            else if (percent is null)
            {
                // 未知电量：内槽中间画一小段横杠（"没有数据"）。
                // 比画空槽明确 —— 空槽和"快没电"几乎一样，会被误读。
                g.FillRectangle(brush, slot.X + 3f, slot.Y + slot.Height / 2 - 1.5f, slot.Width - 6f, 3f);
            }
        }

        if (low) DrawLowBadge(g);

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();   // Clone 之后才能安全销毁原句柄
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// 低电量角标：右下角的红圆 + 白感叹号。
    /// 光靠颜色变化在 16px 图标上不够醒目（尤其任务栏本身是红/橙色调时），
    /// 一个带圆圈的"!"形状在缩到最小时仍然能认出是警告。
    /// </summary>
    private static void DrawLowBadge(Graphics g)
    {
        // 13px（16px 下约 6.5px）：再大就会把本来就只剩一点点的电量条盖住
        var circle = new RectangleF(18f, 17f, 13f, 13f);
        using (var fill = new SolidBrush(TrayRed))
        using (var ring = new Pen(Color.White, 1.8f))
        {
            g.FillEllipse(fill, circle);
            g.DrawEllipse(ring, circle);
        }

        // 感叹号用几何形状画，不依赖字体：字体在极小尺寸下会因 hinting 糊成一团
        using var mark = new SolidBrush(Color.White);
        var cx = circle.X + circle.Width / 2;
        g.FillRectangle(mark, cx - 1.1f, circle.Y + 2.6f, 2.2f, 4.8f);
        g.FillRectangle(mark, cx - 1.1f, circle.Y + 8.2f, 2.2f, 2.2f);
    }

    // 托盘专用配色（不透明）：外壳/内槽是中性灰，在深浅任务栏上都立得住；
    // 电量色比面板那套亮一档，深色任务栏上才醒目。
    private static readonly Color TrayShell = Color.FromArgb(0x8C, 0x8C, 0x8C);
    private static readonly Color TraySlot = Color.FromArgb(0x3A, 0x3A, 0x3A);
    private static readonly Color TrayGreen = Color.FromArgb(0x7C, 0xB8, 0x6A);
    private static readonly Color TrayAmber = Color.FromArgb(0xE0, 0xA9, 0x4C);
    private static readonly Color TrayRed = Color.FromArgb(0xE0, 0x5A, 0x4E);
    private static readonly Color TrayGray = Color.FromArgb(0xB0, 0xAA, 0x9E);

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _icon?.Dispose();
    }
}
