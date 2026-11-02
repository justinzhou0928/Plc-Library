using Microsoft.Extensions.Logging;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General;
using PlcLibrary.General.Configuration;
using Polly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverPool.Engine
{
    internal sealed class DeviceSharedPool(
        ILogger<DeviceSharedPool> logger,
        DeviceConfiguration device,
        IDriverFactory factory,
        PoolOptions options) : IAsyncDisposable
    {
        private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ConnectHardTimeoutGrace = TimeSpan.FromSeconds(2);

        private readonly ResiliencePipeline _pipeline =
            new ResiliencePipelineBuilder().AddPoolStrategies(options, logger, device.Id).Build();
        private readonly SemaphoreSlim _semaphore = new(options.MaxConnectionsPerDevice, options.MaxConnectionsPerDevice);
        private readonly ConcurrentQueue<IProtocolDriver> _idle = new();
        private readonly object _gate = new();
        private int _disposed;
        private int _inUse;
        private int _recycling;
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;

        public DeviceConfiguration Device => device;

        /// <summary>当前是否空闲超过阈值（无在途借用且距最后活动超过 ttl）。供池清理器判断回收。</summary>
        public bool IsIdleBeyond(TimeSpan ttl)
            => Volatile.Read(ref _inUse) == 0
               && Volatile.Read(ref _recycling) == 0
               && DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastActivityTicks), DateTimeKind.Utc) > ttl;

        /// <summary>
        /// 原子地进入「回收中」：只有无在途借用时成功。成功后新的借用以 <see cref="PoolRecycledException"/> 失败，
        /// 由上层重新取池；避免清理线程销毁池时与并发 Acquire 竞争信号量/驱动队列。
        /// </summary>
        internal bool TryBeginRecycle()
        {
            if (Interlocked.CompareExchange(ref _recycling, 1, 0) != 0) return false;
            if (Volatile.Read(ref _inUse) == 0) return true;
            Volatile.Write(ref _recycling, 0);
            return false;
        }

        internal void CancelRecycle() => Volatile.Write(ref _recycling, 0);

        private void TouchActivity() => Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        public async ValueTask<IProtocolDriver> AcquireAsync(CancellationToken ct)
        {
            IProtocolDriver? driver = null;
            var acquireTimeout = device.ConnectionTimeout > TimeSpan.Zero ? device.ConnectionTimeout : options.OperationTimeout;
            var hardTimeout = acquireTimeout + ConnectHardTimeoutGrace;
            var sw = Stopwatch.StartNew();
            if (!await _semaphore.WaitAsync(acquireTimeout, ct).ConfigureAwait(false))
                throw new TimeoutException($"Unable to acquire driver for device '{device.Id}' within {acquireTimeout.TotalSeconds}s");

            try
            {
                if (Volatile.Read(ref _recycling) != 0)
                    throw new PoolRecycledException(device.Id);

                if (_idle.TryDequeue(out driver))
                    PoolLog.LogReusingDriver(logger, device.Id, _idle.Count);
                else
                {
                    driver = await factory.CreateAsync(device, ct).ConfigureAwait(false);
                    PoolLog.LogCreatingDriver(logger, device.Id, device.Protocol);
                }

                if (driver.DriverStatus is DriverStatus.Disconnected or DriverStatus.Faulted)
                {
                    PoolLog.LogConnectingDriver(logger, device.Id, (byte)driver.DriverStatus);
                    // 连接/重连可能忽略取消令牌（底层同步 socket 调用）；WaitAsync 提供硬上限，
                    // 到时放弃该驱动，避免整个采集循环永远卡在 Connect。
                    await _pipeline.ExecuteAsync(async token =>
                    {
                        if (driver.DriverStatus == DriverStatus.Faulted)
                        {
                            await driver.TryReconnectAsync(token).ConfigureAwait(false);
                        }
                        else
                        {
                            await driver.ConnectAsync(token).ConfigureAwait(false);
                        }
                    }, ct).AsTask().WaitAsync(hardTimeout, ct).ConfigureAwait(false);
                }

                // 连接期间池可能已被回收：此时不能把驱动交出去（池即将 Dispose 信号量）
                if (Volatile.Read(ref _recycling) != 0)
                    throw new PoolRecycledException(device.Id);

                sw.Stop();
                Interlocked.Increment(ref _inUse);
                TouchActivity();
                PlcMetrics.AcquireDuration.Record(sw.Elapsed.TotalSeconds,
                    new TagList { { "device.id", device.Id }, { "device.protocol", device.Protocol } });
                return driver;
            }
            catch (PoolRecycledException)
            {
                if (driver is not null)
                    await TryDisposeAsync(driver).ConfigureAwait(false);
                TryRelease();
                throw;
            }
            catch (Exception ex)
            {
                if (driver is not null)
                    await TryDisposeAsync(driver).ConfigureAwait(false);
                TryRelease();
                PoolLog.LogAcquireFailed(logger, ex, device.Id);
                throw;
            }
        }

        /// <summary>归还驱动。<paramref name="discard"/> 为 true 时直接销毁（读取超时/状态未知）。</summary>
        public bool Return(IProtocolDriver driver, bool discard = false)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                DisposeInBackground(driver);
                return false;
            }

            if (discard || driver.DriverStatus == DriverStatus.Faulted)
            {
                Interlocked.Decrement(ref _inUse);
                TouchActivity();
                DisposeInBackground(driver);
                TryRelease();
                return false;
            }

            // 先入队、后减少在途计数：清理线程看到 _inUse == 0 时，驱动一定已经回到 idle 队列，
            // 不会出现“清理已 Remove 池、归还又入队”导致的驱动泄漏。
            bool disposed;
            lock (_gate)
            {
                disposed = Volatile.Read(ref _disposed) != 0;
                if (!disposed)
                    _idle.Enqueue(driver);
            }

            Interlocked.Decrement(ref _inUse);
            TouchActivity();

            if (disposed)
            {
                DisposeInBackground(driver);
                return false;
            }

            PoolLog.LogDriverReturned(logger, device.Id, _idle.Count);
            TryRelease();
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            var drained = new List<IProtocolDriver>();
            lock (_gate)
            {
                while (_idle.TryDequeue(out var driver))
                    drained.Add(driver);
            }

            foreach (var driver in drained)
                await TryDisposeAsync(driver).ConfigureAwait(false);
            _semaphore.Dispose();
        }

        private void TryRelease()
        {
            try { _semaphore.Release(); }
            catch (ObjectDisposedException) { }
        }

        public async ValueTask TryDisposeAsync(IProtocolDriver driver)
        {
            try
            {
                await driver.DisposeAsync().AsTask()
                    .WaitAsync(DisposeTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                PoolLog.LogDriverDisposeFailed(logger, new TimeoutException(
                    $"Driver dispose for device '{device.Id}' timed out after {DisposeTimeout.TotalSeconds}s.", ex), device.Id);
            }
            catch (Exception ex)
            {
                PoolLog.LogDriverDisposeFailed(logger, ex, device.Id);
            }
        }

        private void DisposeInBackground(IProtocolDriver driver) => _ = DisposeDriverAsync(driver);

        private async Task DisposeDriverAsync(IProtocolDriver driver)
            => await TryDisposeAsync(driver).ConfigureAwait(false);
    }
}
