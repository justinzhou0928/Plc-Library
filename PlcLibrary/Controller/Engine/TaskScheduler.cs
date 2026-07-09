using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.General;
using PlcLibrary.General.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Controller.Engine
{
    internal sealed class TaskScheduler(
        IServiceProvider sp,
        ILogger<TaskScheduler> logger,
        IEnumerable<IDriverFactory> factories) : ITaskScheduler, IDisposable, IAsyncDisposable
    {
        private readonly IReadOnlyDictionary<string, IDriverFactory> _factories =
            factories.ToDictionary(f => f.ProtocolDriverName);
        private readonly ConcurrentDictionary<string, TaskActuator> _actuators = new();
        private readonly ConcurrentDictionary<string, DeviceConfiguration> _activeConfigs = new();
        private readonly SemaphoreSlim _applyLock = new(1, 1);

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken ct)
        {
            await Task.WhenAll(_actuators.Values.Select(a => a.StopAsync())).ConfigureAwait(false);
            ControllerLog.LogSchedulerStopped(logger);
        }

        public async Task ApplyDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken ct = default)
        {
            await _applyLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var desired = new Dictionary<string, DeviceConfiguration>();
                foreach (var d in devices)
                {
                    if (!d.Enabled) continue;
                    if (!d.Validate(logger)) continue;
                    if (!_factories.ContainsKey(d.Protocol))
                    {
                        ControllerLog.LogProtocolUnregistered(logger, d.Id, d.Protocol);
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
                        await StartActuatorAsync(device).ConfigureAwait(false);
                    }
                    else if (_activeConfigs.TryGetValue(id, out var old) && !old.Equals(device))
                    {
                        await StopActuatorAsync(id).ConfigureAwait(false);
                        await StartActuatorAsync(device).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _applyLock.Release();
            }
        }

        private async Task StartActuatorAsync(DeviceConfiguration device)
        {
            var actuator = ActivatorUtilities.CreateInstance<TaskActuator>(sp, device);
            _actuators[device.Id] = actuator;
            _activeConfigs[device.Id] = device;
            await actuator.StartAsync().ConfigureAwait(false);
            ControllerLog.LogTaskStarted(logger, device.Id, device.Protocol, device.CollectionInterval);
        }

        private async Task StopActuatorAsync(string deviceId)
        {
            if (_actuators.TryRemove(deviceId, out var actuator))
            {
                try
                {
                    await actuator.StopAsync().ConfigureAwait(false);
                    await actuator.DisposeAsync().ConfigureAwait(false);
                    ControllerLog.LogTaskStopped(logger, deviceId);
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

        public void Dispose()
        {
            foreach (var a in _actuators.Values)
                a.Dispose();
            _actuators.Clear();
            _applyLock.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await Task.WhenAll(_actuators.Values.Select(a => a.StopAsync())).ConfigureAwait(false);
            foreach (var a in _actuators.Values)
                await a.DisposeAsync().ConfigureAwait(false);
            _actuators.Clear();
            _applyLock.Dispose();
        }
    }

    internal sealed class TaskSchedulerHostedService(ITaskScheduler scheduler) : IHostedService
    {
        private readonly TaskScheduler _scheduler = (TaskScheduler)scheduler;

        public Task StartAsync(CancellationToken ct) => _scheduler.StartAsync(ct);
        public Task StopAsync(CancellationToken ct) => _scheduler.StopAsync(ct);
    }
}