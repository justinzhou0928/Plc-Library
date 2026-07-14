using Microsoft.Extensions.Logging;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General;
using PlcLibrary.General.Configuration;
using Polly;
using Polly.Registry;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverPool.Engine
{
    internal sealed class DeviceSharedPool(
        ILogger<DeviceSharedPool> logger,
        DeviceConfiguration device,
        IDriverFactory factory,
        PoolOptions options,
        ResiliencePipelineRegistry<string> registry) : IAsyncDisposable
    {
        private readonly ResiliencePipeline _pipeline =
            registry.GetOrAddPipeline(ResiliencePipelineKeys.Pool(device.Id),
                builder => builder.AddPoolStrategies(options, logger, device.Id));
        private readonly SemaphoreSlim _semaphore = new(options.MaxConnectionsPerDevice, options.MaxConnectionsPerDevice);
        private readonly ConcurrentQueue<IProtocolDriver> _idle = new();

        public async ValueTask<IProtocolDriver> AcquireAsync(CancellationToken ct)
        {
            IProtocolDriver? driver = null;
            var acquireTimeout = device.ConnectionTimeout > TimeSpan.Zero ? device.ConnectionTimeout : options.OperationTimeout;
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
                return driver;
            }
            catch (Exception ex)
            {
                if (driver is not null)
                    await TryDisposeAsync(driver).ConfigureAwait(false);
                _semaphore.Release();
                PoolLog.LogAcquireFailed(logger, ex, device.Id);
                throw;
            }
        }

        public bool Return(IProtocolDriver driver)
        {
            if (driver.DriverStatus == DriverStatus.Faulted)
            {
                _ = TryDisposeAsync(driver);
                _semaphore.Release();
                return false;
            }

            _idle.Enqueue(driver);
            PoolLog.LogDriverReturned(logger, device.Id, _idle.Count);
            _semaphore.Release();
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            while (_idle.TryDequeue(out var driver))
                await TryDisposeAsync(driver).ConfigureAwait(false);
            _semaphore.Dispose();
        }

        public async ValueTask TryDisposeAsync(IProtocolDriver driver)
        {
            try { await driver.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { PoolLog.LogDriverDisposeFailed(logger, ex, device.Id); }
        }
    }
}
