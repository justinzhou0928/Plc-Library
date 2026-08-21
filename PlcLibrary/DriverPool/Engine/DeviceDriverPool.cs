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
        private readonly ManagedResiliencePipelineRegistry _pipelineRegistry;
        private readonly ResiliencePipeline _ioResilience;
        private readonly System.Threading.Timer? _cleanupTimer;
        private int _disposed;

        private static readonly TimeSpan CleanupPeriod = TimeSpan.FromSeconds(60);

        public DeviceDriverPool(
            IOptions<PoolOptions> options,
            IEnumerable<IDriverFactory> factories,
            ILoggerFactory loggerFactory,
            ManagedResiliencePipelineRegistry pipelineRegistry)
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

            // 空置池自动回收：长期热更新增删设备不泄漏连接与弹性管线
            if (_options.PoolIdleTimeout > TimeSpan.Zero)
                _cleanupTimer = new System.Threading.Timer(
                    _ => _ = CleanupIdlePoolsAsync(), null, CleanupPeriod, CleanupPeriod);
        }

        public async Task<DriverResult[]> ReadAsync(
            DeviceConfiguration device, TagPointConfiguration[] points, CancellationToken ct = default)
        {
            using var activity = PlcActivity.Source.StartActivity("PlcLibrary.ReadAsync");
            activity?.SetTag("device.id", device.Id);
            activity?.SetTag("device.protocol", device.Protocol);
            activity?.SetTag("point.count", points.Length);

            var driver = await AcquireAsync(device, ct).ConfigureAwait(false);
            try
            {
                var sw = Stopwatch.StartNew();
                var results = await _ioResilience.ExecuteAsync(
                    async token => await driver.ReadAsync(points, token).ConfigureAwait(false), ct).ConfigureAwait(false);
                sw.Stop();
                PlcMetrics.ReadsTotal.Add(results.Length, new TagList
                {
                    { "device.id", device.Id },
                    { "device.protocol", device.Protocol }
                });
                PlcMetrics.ReadDuration.Record(sw.Elapsed.TotalSeconds, new TagList
                {
                    { "device.id", device.Id },
                    { "device.protocol", device.Protocol }
                });
                return results;
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutRejectedException)
            {
                // IO 超时：底层连接状态未知（可能有半读响应残留），强制断开，
                // 下次获取时走重连路径，避免复用状态未知的连接导致串流/脏数据
                await TryDisconnectAsync(driver).ConfigureAwait(false);
                PlcMetrics.ReadErrors.Add(1, new TagList { { "device.id", device.Id }, { "device.protocol", device.Protocol } });
                throw;
            }
            catch
            {
                PlcMetrics.ReadErrors.Add(1, new TagList { { "device.id", device.Id }, { "device.protocol", device.Protocol } });
                throw;
            }
            finally { Return(driver, device); }
        }

        public async Task<DriverResult[]> WriteAsync(
            DeviceConfiguration device, IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            using var activity = PlcActivity.Source.StartActivity("PlcLibrary.WriteAsync");
            activity?.SetTag("device.id", device.Id);
            activity?.SetTag("device.protocol", device.Protocol);
            activity?.SetTag("point.count", values.Count);

            var driver = await AcquireAsync(device, ct).ConfigureAwait(false);
            try
            {
                var results = await _ioResilience.ExecuteAsync(
                    async token => await driver.WriteAsync(values, token).ConfigureAwait(false), ct).ConfigureAwait(false);
                PlcMetrics.WriteOperations.Add(1, new TagList
                {
                    { "device.id", device.Id },
                    { "device.protocol", device.Protocol }
                });
                return results;
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutRejectedException)
            {
                await TryDisconnectAsync(driver).ConfigureAwait(false);
                throw;
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

        private static async Task TryDisconnectAsync(IProtocolDriver driver)
        {
            try { await driver.DisconnectAsync().ConfigureAwait(false); }
            catch { }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cleanupTimer?.Dispose();
            foreach (var (_, lazy) in _pools)
            {
                if (lazy.IsValueCreated)
                    await lazy.Value.DisposeAsync().ConfigureAwait(false);
            }
            _pools.Clear();
            _pipelineRegistry.Clear();
        }

        /// <summary>回收空置超过 PoolIdleTimeout 的连接池及其弹性管线（由定时器周期调用，也可测试直接调用）。</summary>
        internal async Task CleanupIdlePoolsAsync()
        {
            if (Volatile.Read(ref _disposed) != 0 || _options.PoolIdleTimeout <= TimeSpan.Zero) return;

            foreach (var (key, lazy) in _pools)
            {
                if (!lazy.IsValueCreated) continue;
                var pool = lazy.Value;
                if (!pool.IsIdleBeyond(_options.PoolIdleTimeout)) continue;

                if (_pools.TryRemove(key, out _))
                {
                    await pool.DisposeAsync().ConfigureAwait(false);
                    _pipelineRegistry.TryRemove(ResiliencePipelineKeys.Pool(pool.Device));
                    PoolLog.LogPoolRecycled(_loggerFactory.CreateLogger<DeviceDriverPool>(), pool.Device.Id);
                }
            }
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
