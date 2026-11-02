using System;

namespace PlcLibrary.DriverPool.Engine
{
    /// <summary>
    /// 池已进入空闲回收：Acquire 失败后由 <see cref="DeviceDriverPool"/> 重新取池重试，
    /// 避免与清理线程竞争已释放的信号量或驱动队列。
    /// </summary>
    internal sealed class PoolRecycledException(string deviceId)
        : InvalidOperationException($"Driver pool for device '{deviceId}' is being recycled");
}
