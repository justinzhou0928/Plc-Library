using Microsoft.Extensions.Logging;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Controller.Engine
{
    internal sealed class TaskActuator(
        ILogger<TaskActuator> logger,
        DeviceConfiguration device,
        IDeviceAccessor accessor,
        IDataPipeline pipeline) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        public Task StartAsync()
        {
            lock (_gate)
            {
                if (_loopTask is not null) return Task.CompletedTask;
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _loopTask = ExecuteAsync(_cts.Token);
                return Task.CompletedTask;
            }
        }

        public async Task StopAsync()
        {
            Task? loop;
            lock (_gate)
            {
                if (_loopTask is null) return;
                _cts!.Cancel();
                loop = _loopTask;
            }

            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            lock (_gate) { _loopTask = null; }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _cts?.Dispose();
        }

        private async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(device.CollectionInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                try { await CollectOnceAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { ControllerLog.LogCollectionFailed(logger, ex, device.Id); }
            }
        }

        private async Task CollectOnceAsync(CancellationToken ct)
        {
            var points = device.TagPoints;
            var values = await accessor.ReadAsync(device, points, ct).ConfigureAwait(false);
            if (values.Length != points.Length)
                ControllerLog.LogPointCountMismatch(logger, device.Id, points.Length, values.Length);

            for (var i = 0; i < values.Length; i++)
            {
                var v = values[i];
                var point = i < points.Length ? points[i] : null;
                try
                {
                    await pipeline.HandleAsync(v with
                    {
                        DeviceId = device.Id,
                        TagId = point?.TagId ?? v.Address
                    }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ControllerLog.LogCollectionFailed(logger, ex, device.Id); }
            }
        }
    }
}
