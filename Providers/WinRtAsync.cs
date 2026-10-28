using System;
using System.Threading.Tasks;

namespace PeripheralBattery.Providers;

/// <summary>
/// WinRT 异步调用的超时包装。
/// 蓝牙 API 在设备关机/不可达时可能永远不返回，必须自己兜一个超时，否则整轮轮询会卡住。
///
/// 注意这里**没有真正取消**那个 WinRT 操作：IAsyncOperation 的取消是协作式的，
/// 蓝牙栈在多数情况下并不响应 Cancel，硬等它反而会再阻塞一次。所以做法是"放弃等它"——
/// 超时后直接返回 null，让底层操作自己在后台跑完/失败，结果丢掉。
/// 代价是极端情况下可能有几个操作悬着，但它们不会拖住轮询线程。
///
/// ConfigureAwait(false) 是必须的：这些调用来自轮询线程，续体不需要回 UI 线程，
/// 不这么做会在 UI 线程忙的时候被排队，白白拉长一轮耗时。
/// </summary>
internal static class WinRtAsync
{
    /// <summary>等待 WinRT 异步操作完成，超时或底层操作异常时返回 null。</summary>
    /// <param name="operation">要等待的 WinRT 异步操作。</param>
    /// <param name="milliseconds">最长等待时间，单位为毫秒。</param>
    /// <returns>操作成功时的结果；超时或失败时为 null。</returns>
    /// <remarks>超时不会取消底层操作，只是不再等待它。</remarks>
    public static async Task<T?> WithTimeout<T>(Windows.Foundation.IAsyncOperation<T> operation, int milliseconds)
        where T : class
    {
        var task = operation.AsTask();
        var finished = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);
        if (finished != task) return null;   // 超时：放弃这次结果，别管后台那个操作
        try { return await task.ConfigureAwait(false); }
        catch { return null; }
    }
}
