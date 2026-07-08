using Microsoft.Extensions.Logging;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General;
using PlcLibrary.General.Configuration;
using Polly;
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PlcLibrary.DriverPool.Engine
{
    internal sealed class DeviceSharedPool(
        ILogger<DeviceSharedPool> logger,
        DeviceConfiguration device,
        IDriverFactory factory,
        PoolOptions options,
        ResiliencePipeline pipeline) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore = new(options.MaxConnectionsPerDevice, options.MaxConnectionsPerDevice);
        private readonly Channel<IProtocolDriver> _idle = Channel.CreateBounded<IProtocolDriver>(
            new BoundedChannelOptions(options.MaxConnectionsPerDevice)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = false,
                SingleWriter = false,
            });

        public async ValueTask<IProtocolDriver> AcquireAsync(CancellationToken ct)
        {
            IProtocolDriver? driver = null;
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_idle.Reader.TryRead(out driver))
                    PoolLog.LogReusingDriver(logger, device.Id, _idle.Reader.Count);
                else
                {
                    driver = await factory.CreateAsync(device, ct).ConfigureAwait(false);
                    PoolLog.LogCreatingDriver(logger, device.Id, device.Protocol);
                }

                if (driver.DriverStatus is DriverStatus.Disconnected or DriverStatus.Faulted)
                {
                    PoolLog.LogConnectingDriver(logger, device.Id, (byte)driver.DriverStatus);
                    await pipeline.ExecuteAsync(async token =>
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

        public void Return(IProtocolDriver driver)
        {
            if (driver.DriverStatus == DriverStatus.Faulted || !_idle.Writer.TryWrite(driver))
                _ = TryDisposeAsync(driver).AsTask();
            else
                PoolLog.LogDriverReturned(logger, device.Id, _idle.Reader.Count);
            _semaphore.Release();
        }

        public async ValueTask DisposeAsync()
        {
            while (_idle.Reader.TryRead(out var driver))
                await TryDisposeAsync(driver).ConfigureAwait(false);
            _semaphore.Dispose();
        }

        private async ValueTask TryDisposeAsync(IProtocolDriver driver)
        {
            try { await driver.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose driver. Device={DeviceId}", device.Id);
            }
        }
    }
}
