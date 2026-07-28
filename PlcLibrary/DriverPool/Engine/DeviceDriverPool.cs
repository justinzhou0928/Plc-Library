using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General;
using PlcLibrary.General.Configuration;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using Polly.Registry;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverPool.Engine
{
    internal sealed class DeviceDriverPool : IDeviceAccessor, IAsyncDisposable
    {
        private readonly IReadOnlyDictionary<string, IDriverFactory> _factoriesByProtocol;
        private readonly ConcurrentDictionary<string, Lazy<DeviceSharedPool>> _pools = new();
        private readonly PoolOptions _options;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ResiliencePipelineRegistry<string> _pipelineRegistry;
        private readonly ResiliencePipeline _ioResilience;

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
            _ioResilience = new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = _options.MaxRetryAttempts,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = _options.RetryDelay,
                    ShouldHandle = new PredicateBuilder()
                        .Handle<Exception>(ex => ex is not OperationCanceledException and not TimeoutRejectedException),
                })
                .AddTimeout(_options.OperationTimeout)
                .Build();
        }

        public async Task<DriverResult[]> ReadAsync(
            DeviceConfiguration device, TagPointConfiguration[] points, CancellationToken ct = default)
        {
            var driver = await AcquireAsync(device, ct).ConfigureAwait(false);
            try
            {
                var sw = Stopwatch.StartNew();
                var results = await _ioResilience.ExecuteAsync(
                    async token => await driver.ReadAsync(points, token).ConfigureAwait(false), ct).ConfigureAwait(false);
                sw.Stop();
                PlcMetrics.ReadsTotal.Add(results.Length);
                PlcMetrics.ReadDuration.Record(sw.Elapsed.TotalSeconds);
                return results;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                PlcMetrics.ReadErrors.Add(1);
                throw;
            }
            finally { Return(driver, device); }
        }

        public async Task<DriverResult[]> WriteAsync(
            DeviceConfiguration device, IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            var driver = await AcquireAsync(device, ct).ConfigureAwait(false);
            try
            {
                var results = await _ioResilience.ExecuteAsync(
                    async token => await driver.WriteAsync(values, token).ConfigureAwait(false), ct).ConfigureAwait(false);
                PlcMetrics.WriteOperations.Add(1);
                return results;
            }
            finally { Return(driver, device); }
        }

        public ValueTask<IProtocolDriver> AcquireAsync(DeviceConfiguration device, CancellationToken ct = default)
            => GetOrCreatePool(device).AcquireAsync(ct);

        public void Return(IProtocolDriver driver, DeviceConfiguration device)
        {
            if (TryGetPool(device, out var pool))
                pool.Return(driver);
            else
                _ = DisposeDriverAsync(driver);
        }

        private static async Task DisposeDriverAsync(IProtocolDriver driver)
        {
            try { await driver.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var (_, lazy) in _pools)
            {
                if (lazy.IsValueCreated)
                    await lazy.Value.DisposeAsync().ConfigureAwait(false);
            }
            _pools.Clear();
        }

        private static string PoolKey(DeviceConfiguration device)
            => $"{device.Protocol}|{device.ConnectionString}";

        private DeviceSharedPool GetOrCreatePool(DeviceConfiguration device)
        {
            var factory = ResolveFactory(device.Protocol);
            return _pools.GetOrAdd(PoolKey(device),
                _ => new Lazy<DeviceSharedPool>(
                    () => new DeviceSharedPool(
                        _loggerFactory.CreateLogger<DeviceSharedPool>(), device, factory, _options, _pipelineRegistry),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private bool TryGetPool(DeviceConfiguration device, [NotNullWhen(true)] out DeviceSharedPool? pool)
        {
            if (_factoriesByProtocol.TryGetValue(device.Protocol, out _)
                && _pools.TryGetValue(PoolKey(device), out var lazy))
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
