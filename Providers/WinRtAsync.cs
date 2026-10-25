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
