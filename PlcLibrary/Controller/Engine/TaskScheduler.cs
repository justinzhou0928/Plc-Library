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
        private readonly SemaphoreSlim _applyLock = new(1, 1);

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

        public async Task ApplyDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken ct = default)
        {
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
                    desired[d.Id] = d;
                }

                foreach (var id in _actuators.Keys.ToArray())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!desired.ContainsKey(id))
                        await StopActuatorAsync(id).ConfigureAwait(false);
                }

                foreach (var (id, device) in desired)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!_actuators.TryGetValue(id, out _))
                    {
                        await StartActuatorAsync(device, ct).ConfigureAwait(false);
                    }
                    else if (_activeConfigs.TryGetValue(id, out var old) && HasSignificantChange(old, device))
                    {
                        await StopActuatorAsync(id).ConfigureAwait(false);
                        await StartActuatorAsync(device, ct).ConfigureAwait(false);
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
            ControllerLog.LogSchedulerStopped(_logger);
        }

        public Task<IReadOnlyList<DeviceHealthInfo>> GetDeviceHealthAsync(CancellationToken ct = default)
        {
            var list = new List<DeviceHealthInfo>(_actuators.Count);
            foreach (var (id, actuator) in _actuators)
            {
                var protocol = _activeConfigs.TryGetValue(id, out var cfg) ? cfg.Protocol : "unknown";
                if (actuator.IsRunning)
                    list.Add(DeviceHealthInfo.Healthy(id, protocol));
                else
                    list.Add(DeviceHealthInfo.Faulted(id, protocol, "Actuator stopped unexpectedly"));
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

            var collector = factory.SupportsPush
                ? await CreatePushCollectorAsync(factory, device, ct).ConfigureAwait(false)
                : CreatePollingCollector(device);

            var actuator = ActivatorUtilities.CreateInstance<TaskActuator>(_sp, device, collector);
            _actuators[device.Id] = actuator;
            _activeConfigs[device.Id] = device;
            await actuator.StartAsync().ConfigureAwait(false);
            ControllerLog.LogTaskStarted(_logger, device.Id, device.Protocol, device.CollectionInterval);
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
    }

    internal sealed class TaskSchedulerHost(TaskScheduler scheduler) : BackgroundService
    {
        private readonly TaskScheduler _scheduler = scheduler;

        protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;

        public override async Task StopAsync(CancellationToken ct)
        {
            await _scheduler.StopSchedulerAsync().ConfigureAwait(false);
            await base.StopAsync(ct).ConfigureAwait(false);
        }

        public override void Dispose()
        {
            _scheduler.DisposeResources();
            base.Dispose();
        }
    }
}
