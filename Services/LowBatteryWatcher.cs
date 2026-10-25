using System;
using System.Collections.Generic;
using System.Linq;
using PeripheralBattery.Models;

namespace PeripheralBattery.Services;

/// <summary>
/// 低电量提醒的决策逻辑（不负责真正弹气泡，只负责"该不该弹"）。
///
/// 难点是别烦人：轮询每分钟一次，如果无条件弹，一晚上能弹几十次。
/// 规则：
///   1. 只在"这次放电"里提醒一次 —— 记住该设备已经提醒过；
///   2. 清记忆的条件有三种：**正在充电/供电**（拔线后再低能重新提醒）、
///      电量回升到阈值 **+5 以上**（5% 的迟滞，防止在阈值附近反复横跳）、
///      距上次提醒超过 6 小时冷却期；
///      "重新连接"本身**不会**清记忆 —— 否则摘下来再连上就会再弹一次；
///   3. 冷却期 6 小时，兜住"电量在阈值上下反复"的设备。
/// 这样一晚上只会被叫醒一次，符合"提醒"而不是"刷屏"的定位。
/// </summary>
public sealed class LowBatteryWatcher
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(6);

    /// <summary>已经提醒过的设备：Id → 提醒时间。</summary>
    private readonly Dictionary<string, DateTime> _notified = new();

    /// <summary>返回这一轮需要弹气泡的设备（通常 0~1 台）。</summary>
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

            // 电量回到阈值以上：清掉记忆，允许它下次重新提醒
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

    /// <summary>用户手动"已知悉"时清空记忆（设置里改阈值也会调用）。</summary>
    public void Reset() => _notified.Clear();
}
