using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcLibrary.Controller.Engine;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;
using Polly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Controller.Collectors
{
    internal sealed class PushCollector : IDeviceCollector, ICollectorHeartbeat
    {
        private readonly IPushProtocolDriver _driver;
        private readonly DeviceConfiguration _device;
        private readonly ResiliencePipeline _resilience;
        private readonly IDataPipeline _pipeline;
        private readonly ILogger<PushCollector> _logger;
        private readonly IReadOnlyDictionary<string, string> _addressToTag;
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;

        public PushCollector(
            IPushProtocolDriver driver,
            DeviceConfiguration device,
            IDataPipeline pipeline,
            ILogger<PushCollector> logger,
            IOptions<PoolOptions> poolOptions,
            ILoggerFactory loggerFactory)
        {
            _driver = driver;
            _device = device;
            _pipeline = pipeline;
            _logger = logger;
            _resilience = new ResiliencePipelineBuilder()
                .AddPoolStrategies(poolOptions.Value, loggerFactory.CreateLogger("PushCollector"), device.Id)
                .Build();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var point in device.TagPoints.Where(p => !string.IsNullOrEmpty(p.Address)))
            {
                if (seen.Add(point.Address))
                    dict[point.Address] = point.TagId;
                else
                    ControllerLog.LogDeviceValidationFailed(_logger, device.Id,
                        $"Duplicate address '{point.Address}' in PushCollector, first tag '{dict[point.Address]}' wins");
            }
            _addressToTag = dict;
        }

        public DateTime LastActivityUtc => new(Volatile.Read(ref _lastActivityTicks), DateTimeKind.Utc);

        // 订阅建立后由心跳循环刷新；5 分钟仅用于“心跳循环本身卡死”的兜底。
        public TimeSpan StallTimeout { get; } = TimeSpan.FromMinutes(5);

        /// <summary>订阅存活检查周期；稳定工况下即使没有值变化也会刷新心跳。</summary>
        internal TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

        public async Task ExecuteAsync(CancellationToken ct)
        {
            await _resilience.ExecuteAsync(async token =>
            {
                if (_driver.DriverStatus is DriverStatus.Disconnected or DriverStatus.Faulted)
                {
                    if (_driver.DriverStatus == DriverStatus.Faulted)
                        await _driver.TryReconnectAsync(token).ConfigureAwait(false);
                    else
                        await _driver.ConnectAsync(token).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);

            var pushTask = _driver.StartPushingAsync(
                _device.TagPoints,
                async (result, token) =>
                {
                    try
                    {
                        var enriched = result with
                        {
                            DeviceId = _device.Id,
                            TagId = _addressToTag.TryGetValue(result.Address, out var tag)
                                ? tag
                                : result.TagId
                        };
                        await _pipeline.HandleAsync(enriched, token).ConfigureAwait(false);
                        Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        ControllerLog.LogCollectionFailed(_logger, ex, _device.Id);
                    }
                },
                ct);

            using var timer = new PeriodicTimer(HealthCheckInterval);
            try
            {
                while (!pushTask.IsCompleted)
                {
                    var tickTask = timer.WaitForNextTickAsync(ct).AsTask();
                    var completed = await Task.WhenAny(pushTask, tickTask).ConfigureAwait(false);
                    if (ReferenceEquals(completed, pushTask))
                    {
                        await pushTask.ConfigureAwait(false);
                        return;
                    }

                    await tickTask.ConfigureAwait(false);

                    // 关键：订阅型设备可能长时间没有值变化，不能把“无数据”等同于“无进展”。
                    // 只要驱动仍处于 Connected，就刷新心跳；驱动自己通过 KeepAlive 把断链置 Faulted。
                    Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                    if (_driver.DriverStatus is DriverStatus.Faulted or DriverStatus.Disconnected)
                    {
                        throw new InvalidOperationException(
                            $"Push driver for device '{_device.Id}' is {_driver.DriverStatus}.");
                    }
                }

                await pushTask.ConfigureAwait(false);
            }
            finally
            {
                if (!pushTask.IsCompleted)
                {
                    // 采集器因驱动故障/取消提前退出时，观察后台任务异常，避免未观察异常。
                    _ = pushTask.ContinueWith(
                        static t => _ = t.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        System.Threading.Tasks.TaskScheduler.Default);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await _driver.StopPushingAsync(default).ConfigureAwait(false); }
            catch { }
            try { await _driver.DisposeAsync().ConfigureAwait(false); }
            catch { }
            // 弹性管线随采集器一并释放（Polly 管线由 GC 回收，无显式 Dispose）
        }
    }
}
