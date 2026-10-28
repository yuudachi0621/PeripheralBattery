using System;
using System.Collections.Generic;
using System.Linq;
using PeripheralBattery.Models;

namespace PeripheralBattery.Services;

/// <summary>
/// 低电量提醒的决策逻辑（不负责真正弹气泡，只负责"该不该弹"）。
///
/// 难点是别烦人：轮询每分钟一次，如果无条件弹，一晚上能弹几十次。
/// 实际规则：
///   1. 正在充电或 USB 供电的设备不提醒，并清掉它已有的提醒记录；
///   2. 未充电设备电量小于等于阈值时才会成为候选；
///   3. 同一设备提醒后进入 6 小时冷却，冷却结束前不会重复提醒；
///   4. 电量超过阈值 +5 才清除记录；阈值到阈值 +5（含）之间保留迟滞；
///   5. "重新连接"本身不会清记忆；
///   6. 每轮最多返回一台（最低电量者），但所有触发设备的提醒时间都会先被记录。
/// 本类不是线程安全的，应在构造和使用它的同一线程（当前为 UI 线程）调用。
/// </summary>
public sealed class LowBatteryWatcher
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(6);

    /// <summary>已经提醒过的设备：Id → 提醒时间。</summary>
    private readonly Dictionary<string, DateTime> _notified = new();

    /// <summary>评估这一轮需要弹气泡的设备。</summary>
    /// <param name="online">当前在线设备集合；无电量的设备会被忽略。</param>
    /// <param name="threshold">低电量百分比阈值，判定条件为 <c>percent &lt;= threshold</c>。</param>
    /// <param name="enabled">总开关；false 时直接返回空列表，且不会清理已有提醒记录。</param>
    /// <returns>最多一台设备；多台同时低电量时返回电量最低者。</returns>
    public List<DeviceState> Evaluate(IEnumerable<DeviceState> online, int threshold, bool enabled)
    {
        var result = new List<DeviceState>();
        if (!enabled) return result;

        foreach (var device in online)
        {
            // 正在充电 / USB 供电的设备一律不提醒：插着线还弹"电量低"是最糟糕的体验。
            // 这条要放在"有没有电量"之前 —— 有线模式的键盘就是"在充电但没有电量读数"，
            // 那种设备也该顺带清掉记忆，等它拔线后再低时能重新提醒。
            if (device.IsChargingLike)
            {
                _notified.Remove(device.Id);
                continue;
            }

            if (!device.Percent.HasValue) continue;

            // 电量高于阈值 +5 才清掉记忆；阈值到 +5 之间保留迟滞，防止边缘反复提醒
            if (device.Percent > threshold)
            {
                if (device.Percent > threshold + 5) _notified.Remove(device.Id);
                continue;
            }

            if (_notified.TryGetValue(device.Id, out var at) && DateTime.Now - at < Cooldown) continue;

            _notified[device.Id] = DateTime.Now;
            result.Add(device);
        }

        // 一轮里真的有多台同时低电量时，只报最差的那一台，别一次弹四个气泡
        return result.Count <= 1 ? result : new List<DeviceState> { result.OrderBy(d => d.Percent).First() };
    }

    /// <summary>清空所有提醒记录，使后续评估可以重新触发提醒；设置保存后会调用。</summary>
    public void Reset() => _notified.Clear();
}
