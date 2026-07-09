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
        ResiliencePipelineRegistry<string> registry) : IDisposable, IAsyncDisposable
    {
        private readonly ResiliencePipeline _pipeline =
            registry.GetOrAddPipeline($"{PipelineKey.Pool}:{device.Id}", builder => builder.AddPoolStrategies(options));
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
            if (!await _semaphore.WaitAsync(options.OperationTimeout, ct).ConfigureAwait(false))
                throw new TimeoutException($"无法在 {options.OperationTimeout.TotalSeconds} 秒内获取设备 {device.Id} 的驱动连接");
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

        public void Return(IProtocolDriver driver)
        {
            if (driver.DriverStatus == DriverStatus.Faulted || !_idle.Writer.TryWrite(driver))
                _ = TryDisposeAsync(driver).AsTask();
            else
                PoolLog.LogDriverReturned(logger, device.Id, _idle.Reader.Count);
            _semaphore.Release();
        }

        public void Dispose() => _semaphore.Dispose();

        public async ValueTask DisposeAsync()
        {
            while (_idle.Reader.TryRead(out var driver))
                await TryDisposeAsync(driver).ConfigureAwait(false);
            _semaphore.Dispose();
        }

        private async ValueTask TryDisposeAsync(IProtocolDriver driver)
        {
            try { await driver.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { PoolLog.LogDriverDisposeFailed(logger, ex, device.Id); }
        }
    }
}
