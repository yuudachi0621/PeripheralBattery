using System;
using System.Runtime.InteropServices;

namespace PeripheralBattery.Native;

/// <summary>
/// 系统空闲检测：用来在用户长时间不操作时自动降低轮询频率。
/// 走 GetLastInputInfo 拿"最后一次键鼠输入的系统 tick"，
/// 它包含所有输入（不只是本程序窗口），所以在托盘后台也能正确判断。
/// </summary>
internal static class Power
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>
    /// 距离最后一次键鼠输入过了多少秒。
    ///
    /// dwTime 是 32 位 tick，约 49.7 天回绕一次；用 unchecked 让减法按无符号回绕自然处理，
    /// 这样即使在回绕点附近算出来的差值也是对的。系统运行超过 49.7 天后，
    /// 不用 unchecked 会得到负数，进而被当成"不空闲"，降频就悄悄失效了。
    /// </summary>
    public static int IdleSeconds()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        var idle = unchecked((uint)Environment.TickCount - info.dwTime);
        return (int)Math.Min(idle / 1000, int.MaxValue);
    }
}

/// <summary>
/// 窗口外观相关的小工具：靠 DWM 的属性（DwmSetWindowAttribute）调整系统绘制的部分。
/// 好处是不用自己画标题栏；这些属性在不同 Windows 版本上的支持情况不一致，
/// 所以调用处忽略返回值和异常，系统不认时就退化成默认样式。
/// </summary>
internal static class WindowFx
{
    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE（Win11 才认识）。</summary>
    private const int DwmwaWindowCornerPreference = 33;
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 1809+ 起支持，旧版本忽略）。</summary>
    private const int DwmaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Win11 上给无边框窗口加原生圆角；Win10 上调用无效，会被忽略。</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        var preference = 2;   // DWMWCP_ROUND
        try { DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int)); }
        catch { /* Win10 没有这个属性 */ }
    }

    /// <summary>深色主题下把标题栏也变深。</summary>
    public static void UseDarkTitleBar(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DwmaUseImmersiveDarkMode, ref value, sizeof(int)); }
        catch { /* 忽略 */ }
    }
}
