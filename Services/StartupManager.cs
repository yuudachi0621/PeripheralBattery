using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace PeripheralBattery.Services;

/// <summary>
/// 开机自启：写 HKCU 的 Run 键（不需要管理员权限，也不装服务、不留计划任务）。
/// 命令带上 --tray 参数，登录后直接缩到托盘，不弹窗。
///
/// 用 HKCU 而不是 HKLM 的取舍：HKLM 对全体用户生效但要提权，
/// 一个读电量的工具没理由为了自启去要管理员权限。
///
/// ExePath 优先取 Environment.ProcessPath，而不是 MainModule：
/// 前者在单文件发布 / 裁剪过的情况下仍然可靠，后者在部分环境会返回 null。
/// </summary>
internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PeripheralBattery";

    private static string ExePath
        => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    /// <summary>
    /// 把注册表写成 enabled 指定的状态。幂等 —— 每次都覆盖写，不做"要不要改"的判断，
    /// 这样即使用户手动删过注册表项，程序一启动也能自动补回来。
    /// </summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enabled && ExePath.Length > 0)
                key.SetValue(ValueName, $"\"{ExePath}\" --tray");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 注册表被策略锁住时静默失败，不影响主功能
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
