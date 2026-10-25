namespace PeripheralBattery.Models;

/// <summary>
/// 设备"该怎么显示"的共享规则 —— 主面板的 <see cref="DeviceState"/> 和
/// 扫描窗口的 <see cref="ScanItem"/> 都调这里。
///
/// 为什么单独抽出来：这两处早先是各写一份判断（有读数/在充电/读不到该怎么画），
/// 结果每次调整显示口径都要改两遍，已经漏过两次 ——
/// 一次是"充电中"的文案只改了主面板，扫描窗口仍显示旧的"充电中 100%"；
/// 一次是"读不到电量"从破折号改成图标，也只改了一边。
///
/// 现在判断只留一份实现。两边的字段名不同（Percent / Level），
/// 所以传参而不是传对象 —— 保持这个类零依赖、可单测。
/// </summary>
internal static class DeviceDisplay
{
    /// <summary>
    /// 是否算"在充电"：电池正在被充（Charging）或设备直接吃 USB 线的电（ExternalPower）。
    /// 后者更宽泛 —— 电池充满后 Charging 会回落 false，但线还插着，状态不该消失。
    /// </summary>
    public static bool IsCharging(bool charging, bool externalPower) => charging || externalPower;

    /// <summary>
    /// 电量栏文字：
    ///   · 有读数 → "79%"（不管是否在充电，数字都是真的）
    ///   · 在充电但读不到电量 → "充电中"（有线键盘没有电量接口，至少要说清在供电）
    ///   · 都没有 → 空字符串，由 <see cref="ShowUnknown"/> 的图标接管
    /// </summary>
    public static string LevelText(int? level, bool charging, bool externalPower)
    {
        if (level is int p) return p + "%";
        return IsCharging(charging, externalPower) ? "充电中" : "";
    }

    /// <summary>是否显示"充电中"闪电。</summary>
    public static bool ShowCharge(bool charging, bool externalPower) => IsCharging(charging, externalPower);

    /// <summary>
    /// 是否显示"电量未知"图标（读不到电量、也不在充电）。
    /// 和 <see cref="LevelText"/> 返回空字符串是一对：文字让位给图标。
    /// </summary>
    public static bool ShowUnknown(int? level, bool charging, bool externalPower)
        => level == null && !IsCharging(charging, externalPower);

    /// <summary>
    /// 电量区的悬停说明。读不到电量时优先用设备自己给的 Note（如"未提供电量接口"），
    /// 那是唯一能解释"为什么没有数字"的信息。
    /// </summary>
    public static string LevelHint(int? level, bool charging, bool externalPower, string? note)
    {
        if (externalPower) return charging ? "正在充电 · 由 USB 线直接供电" : "由 USB 线直接供电";
        if (charging) return "正在充电";
        if (level == null) return note ?? "这台设备不提供电量读数";
        return "";
    }
}
