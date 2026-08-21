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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverPool.Engine
{
    internal sealed class DeviceSharedPool(
        ILogger<DeviceSharedPool> logger,
        DeviceConfiguration device,
        IDriverFactory factory,
        PoolOptions options,
        ManagedResiliencePipelineRegistry registry) : IAsyncDisposable
    {
        private readonly ResiliencePipeline _pipeline =
            registry.GetOrAddPipeline(ResiliencePipelineKeys.Pool(device),
                builder => builder.AddPoolStrategies(options, logger, device.Id));
        private readonly SemaphoreSlim _semaphore = new(options.MaxConnectionsPerDevice, options.MaxConnectionsPerDevice);
        private readonly ConcurrentQueue<IProtocolDriver> _idle = new();
        private int _disposed;
        private int _inUse;
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;

        public DeviceConfiguration Device => device;

        /// <summary>当前是否空闲超过阈值（无在途借用且距最后活动超过 ttl）。供池清理器判断回收。</summary>
        public bool IsIdleBeyond(TimeSpan ttl)
            => Volatile.Read(ref _inUse) == 0
               && DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastActivityTicks), DateTimeKind.Utc) > ttl;

        private void TouchActivity() => Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        public async ValueTask<IProtocolDriver> AcquireAsync(CancellationToken ct)
        {
            IProtocolDriver? driver = null;
            var acquireTimeout = device.ConnectionTimeout > TimeSpan.Zero ? device.ConnectionTimeout : options.OperationTimeout;
            var sw = Stopwatch.StartNew();
            if (!await _semaphore.WaitAsync(acquireTimeout, ct).ConfigureAwait(false))
                throw new TimeoutException($"Unable to acquire driver for device '{device.Id}' within {acquireTimeout.TotalSeconds}s");

            try
            {
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
                    await _pipeline.ExecuteAsync(async token =>
                    {
                        if (driver.DriverStatus == DriverStatus.Faulted)
                            await driver.TryReconnectAsync(token).ConfigureAwait(false);
                        else
                            await driver.ConnectAsync(token).ConfigureAwait(false);
                    }, ct).ConfigureAwait(false);
                }

                sw.Stop();
                Interlocked.Increment(ref _inUse);
                TouchActivity();
                PlcMetrics.AcquireDuration.Record(sw.Elapsed.TotalSeconds,
                    new TagList { { "device.id", device.Id }, { "device.protocol", device.Protocol } });
                return driver;
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

        public bool Return(IProtocolDriver driver)
        {
            // 池已销毁：晚到的归还直接释放驱动，不再触碰信号量（可能已 Dispose）
            if (Volatile.Read(ref _disposed) != 0)
            {
                _ = TryDisposeAsync(driver);
                return false;
            }

            Interlocked.Decrement(ref _inUse);
            TouchActivity();

            if (driver.DriverStatus == DriverStatus.Faulted)
            {
                _ = TryDisposeAsync(driver);
                TryRelease();
                return false;
            }

            _idle.Enqueue(driver);
            PoolLog.LogDriverReturned(logger, device.Id, _idle.Count);
            TryRelease();
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            while (_idle.TryDequeue(out var driver))
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
            try { await driver.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { PoolLog.LogDriverDisposeFailed(logger, ex, device.Id); }
        }
    }
}
