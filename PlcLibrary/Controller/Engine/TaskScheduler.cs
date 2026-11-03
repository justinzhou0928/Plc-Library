using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlcLibrary.Controller.Collectors;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.Controller.Models;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.General;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Controller.Engine
{
    internal sealed class TaskScheduler : IDeviceScheduler
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<TaskScheduler> _logger;
        private readonly IReadOnlyDictionary<string, IDriverFactory> _factories;
        private readonly IDataPipeline _pipeline;
        private readonly ConcurrentDictionary<string, TaskActuator> _actuators = new();
        private readonly ConcurrentDictionary<string, DeviceConfiguration> _activeConfigs = new();
        private readonly ConcurrentDictionary<string, RestartState> _restarts = new();
        private readonly SemaphoreSlim _applyLock = new(1, 1);
        private int _stopping;

        public TaskScheduler(
            IServiceProvider sp,
            ILogger<TaskScheduler> logger,
            IEnumerable<IDriverFactory> factories,
            IDataPipeline pipeline)
        {
            _sp = sp;
            _logger = logger;
            _factories = factories.ToDictionary(f => f.ProtocolDriverName);
            _pipeline = pipeline;
        }

        private const int SupervisionIntervalSeconds = 5;
        private const int MaxRestartBackoffSeconds = 60;
        private const int MaxRestartBackoffExponent = 8;

        /// <summary>监督循环周期：扫描已停止的采集任务并按指数退避重建（测试可缩短）。</summary>
        internal TimeSpan SupervisionInterval { get; set; } = TimeSpan.FromSeconds(SupervisionIntervalSeconds);

        private static readonly TimeSpan MaxRestartBackoff = TimeSpan.FromSeconds(MaxRestartBackoffSeconds);

        public async Task ApplyDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _stopping) != 0) return;

            await _applyLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Dictionary<string, DeviceConfiguration> desired = [];
                foreach (var d in devices)
                {
                    if (!d.Enabled) continue;
                    if (!d.Validate(out var errors)) { LogValidationErrors(d.Id, errors); continue; }
                    if (!_factories.ContainsKey(d.Protocol))
                    {
                        ControllerLog.LogProtocolUnregistered(_logger, d.Id, d.Protocol);
                        continue;
                    }
                    if (!desired.TryAdd(d.Id, d))
                    {
                        ControllerLog.LogDeviceValidationFailed(_logger, d.Id, "Id 重复，跳过");
                        continue;
                    }
                }

                foreach (var id in _actuators.Keys.ToArray())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!desired.ContainsKey(id))
                        await StopActuatorAsync(id).ConfigureAwait(false);
                }

                foreach (var (id, device) in desired)
                {
                    // 停机可能发生在 Apply 等待驱动创建/连接期间；停止后再启动会留下无人回收的执行器。
                    if (Volatile.Read(ref _stopping) != 0) return;
                    ct.ThrowIfCancellationRequested();
                    if (!_actuators.TryGetValue(id, out _))
                    {
                        try
                        {
                            await StartActuatorAsync(device, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            // 单个设备配置/驱动构造失败不能拖垮整次热更新。
                            ControllerLog.LogCollectionFailed(_logger, ex, id);
                            await StopActuatorAsync(id).ConfigureAwait(false);
                            continue;
                        }

                        if (Volatile.Read(ref _stopping) != 0)
                            await StopActuatorAsync(id).ConfigureAwait(false);
                    }
                    else if (_activeConfigs.TryGetValue(id, out var old) && HasSignificantChange(old, device))
                    {
                        await StopActuatorAsync(id).ConfigureAwait(false);
                        try
                        {
                            await StartActuatorAsync(device, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            ControllerLog.LogCollectionFailed(_logger, ex, id);
                            await StopActuatorAsync(id).ConfigureAwait(false);
                            continue;
                        }

                        if (Volatile.Read(ref _stopping) != 0)
                            await StopActuatorAsync(id).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _applyLock.Release();
            }
        }

        /// <summary>
        /// 监督循环：推送型采集器的连接失败会让后台循环提前结束（轮询采集器内部自行吞异常），
        /// 这里按指数退避重建执行器，避免设备永久僵死。
        /// </summary>
        internal async Task SuperviseAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(SupervisionInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    if (Volatile.Read(ref _stopping) != 0) return;
                    try
                    {
                        await RestartFaultedActuatorsAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // 监督循环本身不能因单次异常退出；否则卡死设备将永久无人重启。
                        ControllerLog.LogCollectionFailed(_logger, ex, "supervisor");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        internal async Task RestartFaultedActuatorsAsync(CancellationToken ct)
        {
            await _applyLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = Environment.TickCount64;
                foreach (var (id, actuator) in _actuators)
                {
                    ct.ThrowIfCancellationRequested();
                    if (actuator.IsRunning && !actuator.IsStalled)
                    {
                        _restarts.TryRemove(id, out _);
                        continue;
                    }

                    if (!_activeConfigs.TryGetValue(id, out var device)) continue;

                    var state = _restarts.GetOrAdd(id, static _ => new RestartState());
                    if (now < state.NextAllowedTicks) continue;

                    state.Attempts++;
                    var backoffMs = Math.Min(
                        SupervisionInterval.TotalMilliseconds * Math.Pow(2, Math.Min(state.Attempts - 1, MaxRestartBackoffExponent)),
                        MaxRestartBackoff.TotalMilliseconds);
                    state.NextAllowedTicks = now + (long)backoffMs;

                    var reason = actuator.IsStalled
                        ? "collection stalled: no progress within the collector stall timeout"
                        : actuator.LastError?.Message;
                    ControllerLog.LogActuatorRestarting(_logger, id, state.Attempts, reason);
                    try
                    {
                        await StopActuatorAsync(id).ConfigureAwait(false);
                        await StartActuatorAsync(device, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        ControllerLog.LogCollectionFailed(_logger, ex, id);
                    }
                }
            }
            finally
            {
                _applyLock.Release();
            }
        }

        internal async Task StopSchedulerAsync()
        {
            Interlocked.Exchange(ref _stopping, 1);

            var actuators = _actuators.Values.ToArray();
            if (actuators.Length == 0) return;

            var tasks = new Task[actuators.Length];
            for (var i = 0; i < actuators.Length; i++)
                tasks[i] = actuators[i].StopAsync();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            for (var i = 0; i < actuators.Length; i++)
            {
                try { await actuators[i].DisposeAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { ControllerLog.LogCollectionFailed(_logger, ex, actuators[i].Device.Id); }
            }

            _actuators.Clear();
            _activeConfigs.Clear();
            _restarts.Clear();
            ControllerLog.LogSchedulerStopped(_logger);
        }

        public Task<IReadOnlyList<DeviceHealthInfo>> GetDeviceHealthAsync(CancellationToken ct = default)
        {
            var list = new List<DeviceHealthInfo>(_actuators.Count);
            foreach (var (id, actuator) in _actuators)
            {
                var protocol = _activeConfigs.TryGetValue(id, out var cfg) ? cfg.Protocol : "unknown";
                if (actuator.IsRunning && !actuator.IsStalled)
                    list.Add(DeviceHealthInfo.Healthy(id, protocol));
                else
                    list.Add(DeviceHealthInfo.Faulted(id, protocol,
                        actuator.IsStalled
                            ? "Collection stalled: no progress within the collector stall timeout"
                            : actuator.LastError?.Message ?? "Actuator stopped unexpectedly"));
            }
            return Task.FromResult<IReadOnlyList<DeviceHealthInfo>>(list);
        }

        internal void DisposeResources()
        {
            _applyLock.Dispose();
        }

        private async Task StartActuatorAsync(DeviceConfiguration device, CancellationToken ct)
        {
            var factory = _factories[device.Protocol];

            // 轮询驱动由连接池在首次读取时创建；这里先构造/释放一次，让连接串解析错误
            // 在 Apply 阶段暴露（而不是每个采集周期重复记录日志）。
            if (!factory.SupportsPush)
                await ValidateDriverConfigurationAsync(factory, device, ct).ConfigureAwait(false);

            var collector = factory.SupportsPush
                ? await CreatePushCollectorAsync(factory, device, ct).ConfigureAwait(false)
                : CreatePollingCollector(device);

            var actuator = ActivatorUtilities.CreateInstance<TaskActuator>(_sp, device, collector);
            _actuators[device.Id] = actuator;
            _activeConfigs[device.Id] = device;
            await actuator.StartAsync().ConfigureAwait(false);
            ControllerLog.LogTaskStarted(_logger, device.Id, device.Protocol, device.CollectionInterval);
        }

        private static async Task ValidateDriverConfigurationAsync(
            IDriverFactory factory, DeviceConfiguration device, CancellationToken ct)
        {
            var probe = await factory.CreateAsync(device, ct).ConfigureAwait(false);
            try { await probe.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }

        private async Task<IDeviceCollector> CreatePushCollectorAsync(
            IDriverFactory factory, DeviceConfiguration device, CancellationToken ct)
        {
            var driver = await factory.CreateAsync(device, ct).ConfigureAwait(false);
            return ActivatorUtilities.CreateInstance<PushCollector>(
                _sp, (IPushProtocolDriver)driver, device, _pipeline);
        }

        private PollingCollector CreatePollingCollector(DeviceConfiguration device)
            => ActivatorUtilities.CreateInstance<PollingCollector>(_sp, device);

        private async Task StopActuatorAsync(string deviceId)
        {
            if (_actuators.TryRemove(deviceId, out var actuator))
            {
                try
                {
                    await actuator.StopAsync().ConfigureAwait(false);
                    await actuator.DisposeAsync().ConfigureAwait(false);
                    ControllerLog.LogTaskStopped(_logger, deviceId);
                }
                finally
                {
                    _activeConfigs.TryRemove(deviceId, out _);
                }
            }
            else
            {
                _activeConfigs.TryRemove(deviceId, out _);
            }
        }

        private void LogValidationErrors(string deviceId, IReadOnlyList<ValidationResult> errors)
        {
            foreach (var r in errors)
                ControllerLog.LogDeviceValidationFailed(_logger, deviceId, r.ErrorMessage ?? "");
        }

        private static bool HasSignificantChange(DeviceConfiguration old, DeviceConfiguration device)
            => old.Enabled != device.Enabled
            || old.Protocol != device.Protocol
            || old.ConnectionString != device.ConnectionString
            || old.ConnectionTimeout != device.ConnectionTimeout
            || old.CollectionInterval != device.CollectionInterval
            || !old.TagPoints.SequenceEqual(device.TagPoints);

        private sealed class RestartState
        {
            public int Attempts;
            public long NextAllowedTicks;
        }
    }

    internal sealed class TaskSchedulerHost(TaskScheduler scheduler) : BackgroundService
    {
        private readonly TaskScheduler _scheduler = scheduler;

        protected override Task ExecuteAsync(CancellationToken ct) => _scheduler.SuperviseAsync(ct);

        public override async Task StopAsync(CancellationToken ct)
        {
            // 先停监督循环（取消并等待），再停止采集任务，避免停机过程中被重启
            await base.StopAsync(ct).ConfigureAwait(false);
            await _scheduler.StopSchedulerAsync().ConfigureAwait(false);
        }

        public override void Dispose()
        {
            _scheduler.DisposeResources();
            base.Dispose();
        }
    }
}
