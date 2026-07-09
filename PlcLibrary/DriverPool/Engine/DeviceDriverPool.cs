using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General.Configuration;
using Polly.Registry;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverPool.Engine
{
    internal sealed class DeviceDriverPool : IDeviceAccessor, IDisposable, IAsyncDisposable
    {
        private readonly IReadOnlyDictionary<string, IDriverFactory> _factoriesByProtocol;
        private readonly ConcurrentDictionary<string, Lazy<DeviceSharedPool>> _pools = new();
        private readonly PoolOptions _options;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ResiliencePipelineRegistry<string> _pipelineRegistry;

        public DeviceDriverPool(
            IOptions<PoolOptions> options,
            IEnumerable<IDriverFactory> factories,
            ILoggerFactory loggerFactory,
            ResiliencePipelineRegistry<string> pipelineRegistry)
        {
            _factoriesByProtocol = factories.ToDictionary(f => f.ProtocolDriverName);
            _options = options.Value;
            _loggerFactory = loggerFactory;
            _pipelineRegistry = pipelineRegistry;
        }

        public async Task<DriverResult[]> ReadAsync(
            DeviceConfiguration device, TagPointConfiguration[] points, CancellationToken ct = default)
        {
            var driver = await AcquireAsync(device, ct).ConfigureAwait(false);
            try { return await driver.ReadAsync(points, ct).ConfigureAwait(false); }
            finally { Return(driver, device); }
        }

        public async Task<DriverResult[]> WriteAsync(
            DeviceConfiguration device, IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            var driver = await AcquireAsync(device, ct).ConfigureAwait(false);
            try { return await driver.WriteAsync(values, ct).ConfigureAwait(false); }
            finally { Return(driver, device); }
        }

        public ValueTask<IProtocolDriver> AcquireAsync(DeviceConfiguration device, CancellationToken ct = default)
            => GetOrCreatePool(device).AcquireAsync(ct);

        public void Return(IProtocolDriver driver, DeviceConfiguration device)
        {
            if (TryGetPool(device, out var pool))
                pool.Return(driver);
            else
                _ = TryDisposeAsync(driver);
        }

        public void Dispose()
        {
            foreach (var (_, lazy) in _pools)
            {
                if (lazy.IsValueCreated)
                    lazy.Value.Dispose();
            }
            _pools.Clear();
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
                        _loggerFactory.CreateLogger<DeviceSharedPool>(), device, factory, _options, _pipelineRegistry),
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

        private static async ValueTask TryDisposeAsync(IProtocolDriver driver)
        {
            try { await driver.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
    }
}
