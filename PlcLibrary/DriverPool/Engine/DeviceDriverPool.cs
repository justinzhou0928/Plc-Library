using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General;
using PlcLibrary.General.Configuration;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace PlcLibrary.DriverPool.Engine
{
    internal sealed class DeviceDriverPool : IDeviceAccessor, IDeviceBrowser, IAsyncDisposable
    {
        private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(2);

        /// <summary>Polly 超时之后的硬兜底宽限；测试可缩短。</summary>
        internal TimeSpan HardTimeoutGrace { get; set; } = TimeSpan.FromSeconds(2);

        private readonly IReadOnlyDictionary<string, IDriverFactory> _factoriesByProtocol;
        private readonly ConcurrentDictionary<string, Lazy<DeviceSharedPool>> _pools = new();
        private readonly ConcurrentDictionary<IoPipelineKey, ResiliencePipeline> _ioPipelines = new();
        private readonly PoolOptions _options;
        private readonly ILoggerFactory _loggerFactory;
        private readonly CancellationTokenSource? _cleanupCts;
        private readonly Task? _cleanupTask;
        private int _disposed;

        private const int CleanupPeriodSeconds = 60;
        private static readonly TimeSpan CleanupPeriod = TimeSpan.FromSeconds(CleanupPeriodSeconds);

        public DeviceDriverPool(
            IOptions<PoolOptions> options,
            IEnumerable<IDriverFactory> factories,
            ILoggerFactory loggerFactory)
        {
            // 幂等注册：同一协议重复 AddDriver 时保留最后一个工厂
            _factoriesByProtocol = factories
                .GroupBy(f => f.ProtocolDriverName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            _options = options.Value;
            _loggerFactory = loggerFactory;

            // 空置池自动回收：长期热更新增删设备不泄漏连接与弹性管线
            if (_options.PoolIdleTimeout > TimeSpan.Zero)
            {
                _cleanupCts = new CancellationTokenSource();
                _cleanupTask = CleanupLoopAsync(_cleanupCts.Token);
            }
        }

        public async Task<DriverResult[]> ReadAsync(
            DeviceConfiguration device, TagPointConfiguration[] points, CancellationToken ct = default)
        {
            using var activity = PlcActivity.Source.StartActivity("PlcLibrary.ReadAsync", ActivityKind.Client);
            activity?.SetTag("device.id", device.Id);
            activity?.SetTag("device.protocol", device.Protocol);
            activity?.SetTag("point.count", points.Length);

            var driver = await AcquireAsync(device, ct).ConfigureAwait(false);
            var discard = false;
            try
            {
                var sw = Stopwatch.StartNew();
                var results = await GetIoPipeline(device).ExecuteAsync(
                        async token => await driver.ReadAsync(points, token).ConfigureAwait(false), ct)
                    .AsTask()
                    .WaitAsync(HardTimeout, ct)
                    .ConfigureAwait(false);
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
            catch (OperationCanceledException)
            {
                discard = ct.IsCancellationRequested;
                throw;
            }
            catch (TimeoutRejectedException)
            {
                // IO 超时：底层连接状态未知（可能有半读响应残留），强制断开并丢弃，
                // 下次获取时走重连/新建路径，避免复用状态未知的连接导致串流/脏数据
                discard = true;
                await TryDisconnectAsync(driver).ConfigureAwait(false);
                PlcMetrics.ReadErrors.Add(1, new TagList { { "device.id", device.Id }, { "device.protocol", device.Protocol } });
                throw;
            }
            catch (TimeoutException)
            {
                // Polly 超时也杀不掉的底层调用：WaitAsync 硬上限兜底，直接丢弃驱动
                discard = true;
                PlcMetrics.ReadErrors.Add(1, new TagList { { "device.id", device.Id }, { "device.protocol", device.Protocol } });
                throw;
            }
            catch
            {
                PlcMetrics.ReadErrors.Add(1, new TagList { { "device.id", device.Id }, { "device.protocol", device.Protocol } });
                throw;
            }
            finally { Return(driver, device, discard); }
        }

        public async Task<DriverResult[]> WriteAsync(
            DeviceConfiguration device, IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            using var activity = PlcActivity.Source.StartActivity("PlcLibrary.WriteAsync", ActivityKind.Client);
            activity?.SetTag("device.id", device.Id);
            activity?.SetTag("device.protocol", device.Protocol);
            activity?.SetTag("point.count", values.Count);

            var driver = await AcquireAsync(device, ct).ConfigureAwait(false);
            var discard = false;
            try
            {
                var results = await GetIoPipeline(device).ExecuteAsync(
                        async token => await driver.WriteAsync(values, token).ConfigureAwait(false), ct)
                    .AsTask()
                    .WaitAsync(HardTimeout, ct)
                    .ConfigureAwait(false);
                PlcMetrics.WriteOperations.Add(1, new TagList
                {
                    { "device.id", device.Id },
                    { "device.protocol", device.Protocol }
                });
                return results;
            }
            catch (OperationCanceledException)
            {
                discard = ct.IsCancellationRequested;
                throw;
            }
            catch (TimeoutRejectedException)
            {
                discard = true;
                await TryDisconnectAsync(driver).ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException)
            {
                discard = true;
                throw;
            }
            finally { Return(driver, device, discard); }
        }

        public async Task<IReadOnlyList<TagDescriptor>> BrowseAsync(
            DeviceConfiguration device, string? address = null, CancellationToken ct = default)
        {
            using var activity = PlcActivity.Source.StartActivity("PlcLibrary.BrowseAsync", ActivityKind.Client);
            activity?.SetTag("device.id", device.Id);
            activity?.SetTag("device.protocol", device.Protocol);

            var driver = await AcquireAsync(device, ct).ConfigureAwait(false);
            var discard = false;
            try
            {
                if (driver is not ITagBrowser browser)
                    throw new NotSupportedException(
                        $"Protocol '{device.Protocol}' does not support tag browsing.");

                return await GetIoPipeline(device).ExecuteAsync(
                        async token => await browser.BrowseAsync(address, token).ConfigureAwait(false), ct)
                    .AsTask()
                    .WaitAsync(HardTimeout, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                discard = ct.IsCancellationRequested;
                throw;
            }
            catch (TimeoutRejectedException)
            {
                discard = true;
                await TryDisconnectAsync(driver).ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException)
            {
                discard = true;
                throw;
            }
            finally
            {
                Return(driver, device, discard);
            }
        }

        private TimeSpan HardTimeout => _options.OperationTimeout + HardTimeoutGrace;

        private ResiliencePipeline GetIoPipeline(DeviceConfiguration device)
            => _ioPipelines.GetOrAdd(new IoPipelineKey(PoolKey(device), device.Id), _ => BuildIoPipeline(device));

        private ResiliencePipeline BuildIoPipeline(DeviceConfiguration device)
        {
            var logger = _loggerFactory.CreateLogger("DeviceIO");
            return new ResiliencePipelineBuilder()
                // 断路器在最外层：统计“重试后仍失败”的最终结果；坏设备快速失败，
                // 避免每个采集周期都重试到超时。超时也计入失败，便于隔离慢设备。
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = _options.CircuitBreakerFailureRatio,
                    MinimumThroughput = _options.CircuitBreakerMinimumThroughput,
                    BreakDuration = _options.CircuitBreakerDuration,
                    ShouldHandle = new PredicateBuilder()
                        .Handle<Exception>(ex => ex is not OperationCanceledException),
                    OnOpened = _ =>
                    {
                        PoolLog.LogCircuitBreakerOpened(logger, device.Id, _options.CircuitBreakerDuration);
                        return default;
                    },
                    OnHalfOpened = _ =>
                    {
                        PoolLog.LogCircuitBreakerHalfOpened(logger, device.Id);
                        return default;
                    },
                    OnClosed = _ =>
                    {
                        PoolLog.LogCircuitBreakerClosed(logger, device.Id);
                        return default;
                    },
                })
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

        public async ValueTask<IProtocolDriver> AcquireAsync(DeviceConfiguration device, CancellationToken ct = default)
        {
            for (var attempt = 0; ; attempt++)
            {
                var pool = GetOrCreatePool(device);
                try
                {
                    return await pool.AcquireAsync(ct).ConfigureAwait(false);
                }
                catch (PoolRecycledException) when (attempt == 0)
                {
                    // 池刚被空闲回收：重新 GetOrCreatePool（会创建新池）后重试一次
                }
            }
        }

        public void Return(IProtocolDriver driver, DeviceConfiguration device, bool discard = false)
        {
            if (TryGetPool(device, out var pool))
                pool.Return(driver, discard);
            else
                _ = DisposeDriverAsync(driver);
        }

        private static async Task DisposeDriverAsync(IProtocolDriver driver)
        {
            try
            { await driver.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }

        private static async Task TryDisconnectAsync(IProtocolDriver driver)
        {
            try
            {
                await driver.DisconnectAsync()
                    .WaitAsync(DisconnectTimeout)
                    .ConfigureAwait(false);
            }
            catch { }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (_cleanupCts is not null)
                await _cleanupCts.CancelAsync().ConfigureAwait(false);
            if (_cleanupTask is not null)
            {
                try
                { await _cleanupTask.ConfigureAwait(false); }
                catch { }
            }
            _cleanupCts?.Dispose();

            foreach (var (_, lazy) in _pools)
            {
                if (lazy.IsValueCreated)
                    await lazy.Value.DisposeAsync().ConfigureAwait(false);
            }
            _pools.Clear();
            _ioPipelines.Clear();
        }

        private async Task CleanupLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(CleanupPeriod);
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    try
                    { await CleanupIdlePoolsAsync().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        PoolLog.LogPoolCleanupFailed(_loggerFactory.CreateLogger<DeviceDriverPool>(), ex);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>回收空置超过 PoolIdleTimeout 的连接池及其弹性管线（由定时器周期调用，也可测试直接调用）。</summary>
        internal async Task CleanupIdlePoolsAsync()
        {
            if (Volatile.Read(ref _disposed) != 0 || _options.PoolIdleTimeout <= TimeSpan.Zero)
                return;

            foreach (var (key, lazy) in _pools)
            {
                if (!lazy.IsValueCreated)
                    continue;
                var pool = lazy.Value;
                if (!pool.IsIdleBeyond(_options.PoolIdleTimeout))
                    continue;

                // 先原子进入「回收中」：有并发借用时放弃本轮回，避免与 Acquire 竞争
                if (!pool.TryBeginRecycle())
                    continue;

                if (_pools.TryRemove(key, out _))
                {
                    // IO 断路器按设备独立，池回收时清掉挂在该连接池下的所有设备管线
                    foreach (var ioKey in _ioPipelines.Keys)
                    {
                        if (string.Equals(ioKey.PoolKey, key, StringComparison.Ordinal))
                            _ioPipelines.TryRemove(ioKey, out _);
                    }

                    await pool.DisposeAsync().ConfigureAwait(false);
                    PoolLog.LogPoolRecycled(_loggerFactory.CreateLogger<DeviceDriverPool>(), pool.Device.Id);
                }
                else
                {
                    pool.CancelRecycle();
                }
            }
        }

        private static string PoolKey(DeviceConfiguration device)
            => $"{device.Protocol}|{device.ConnectionTimeout.Ticks}|{device.ConnectionString}";

        /// <summary>IO 断路器按逻辑设备隔离：同一连接池下不同 DeviceId 不共享熔断状态。</summary>
        private readonly record struct IoPipelineKey(string PoolKey, string DeviceId);

        private DeviceSharedPool GetOrCreatePool(DeviceConfiguration device)
        {
            var factory = ResolveFactory(device.Protocol);
            return _pools.GetOrAdd(PoolKey(device),
                _ => new Lazy<DeviceSharedPool>(
                    () => new DeviceSharedPool(
                        _loggerFactory.CreateLogger<DeviceSharedPool>(), device, factory, _options),
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
            if (_factoriesByProtocol.TryGetValue(protocol, out var factory))
                return factory;
            throw new InvalidOperationException($"No driver factory registered for protocol '{protocol}'.");
        }
    }
}
