using System;
using System.IO;
using System.Text;
using PeripheralBattery.Models;

namespace PeripheralBattery.Services;

/// <summary>
/// 极简日志：托盘程序没有控制台，出问题只能靠它。
/// 写在 %APPDATA%\PeripheralBattery\app.log，只保留最近 200KB。
///
/// 采样策略：正常轮询的结果**只在变化时**才写（见 BatteryMonitor.Merge），
/// 否则每分钟一条，一晚上就把 200KB 刷满、把真正有用的错误冲掉。
/// 锁是必要的 —— 轮询线程、UI 线程和诊断流程都会写这里。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(ConfigStore.Dir, "app.log");

    /// <summary>向用户日志追加一条带本地时间戳的记录。</summary>
    /// <param name="message">要写入的内容；调用方负责避免记录敏感信息。</param>
    /// <remarks>日志超过约 200 KB 时会先删除旧文件；任何 I/O 失败都会被吞掉。</remarks>
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ConfigStore.Dir);

                // 超过 200KB 直接删掉重开，而不是做滚动备份：
                // 这个日志只服务于"最近出了什么问题"，不需要历史归档
                var file = new FileInfo(Path);
                if (file.Exists && file.Length > 200 * 1024) file.Delete();

                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响主流程（磁盘满、目录被删都可能走到这里）
        }
    }

    /// <summary>记一条异常，带上发生位置，方便在日志里检索 "[错误]"。</summary>
    /// <param name="where">异常发生的位置或功能名。</param>
    /// <param name="ex">要记录的异常。</param>
    public static void Error(string where, Exception ex) => Write($"[错误] {where}: {ex}");
}
