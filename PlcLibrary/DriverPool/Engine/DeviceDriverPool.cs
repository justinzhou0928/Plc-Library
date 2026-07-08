using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General.Configuration;
using Polly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverPool.Engine
{
    internal sealed class DeviceDriverPool(
        ResiliencePipeline pipeline,
        IOptions<PoolOptions> options,
        IEnumerable<IDriverFactory> factories,
        ILoggerFactory loggerFactory) : IAsyncDisposable
    {
        private readonly IReadOnlyDictionary<string, IDriverFactory> _factoriesByProtocol =
            factories.ToDictionary(f => f.ProtocolDriver);
        private readonly ConcurrentDictionary<string, Lazy<DeviceSharedPool>> _pools = new();

        public ValueTask<IProtocolDriver> AcquireAsync(DeviceConfiguration device, CancellationToken ct = default)
            => GetOrCreatePool(device).AcquireAsync(ct);

        public void Return(IProtocolDriver driver, DeviceConfiguration device)
        {
            if (TryGetPool(device, out var pool)) pool.Return(driver);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var (_, lazy) in _pools)
                await lazy.Value.DisposeAsync().ConfigureAwait(false);
            _pools.Clear();
        }

        private DeviceSharedPool GetOrCreatePool(DeviceConfiguration device)
        {
            var factory = ResolveFactory(device.Protocol);
            var key = factory.GetConnectionKey(device.ConnectionString);
            return _pools.GetOrAdd(key,
                _ => new Lazy<DeviceSharedPool>(
                    () => new DeviceSharedPool(
                        loggerFactory.CreateLogger<DeviceSharedPool>(),device, factory, options.Value,pipeline),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private bool TryGetPool(DeviceConfiguration device, [NotNullWhen(true)] out DeviceSharedPool? pool)
        {
            if (_factoriesByProtocol.TryGetValue(device.Protocol, out var factory)
                && _pools.TryGetValue(factory.GetConnectionKey(device.ConnectionString), out var lazy))
            {
                pool = lazy.Value;
                return true;
            }
            pool = null;
            return false;
        }

        private IDriverFactory ResolveFactory(string protocol)
        {
            if (_factoriesByProtocol.TryGetValue(protocol, out var factory)) return factory;
            throw new InvalidOperationException($"No driver factory registered for protocol '{protocol}'.");
        }
    }
}
