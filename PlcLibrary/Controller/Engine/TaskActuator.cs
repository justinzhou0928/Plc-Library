using PlcLibrary.General.Configuration;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Controller.Engine
{
    internal sealed class TaskActuator(DeviceConfiguration device, IDeviceCollector collector) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        public DeviceConfiguration Device => device;

        public Task StartAsync()
        {
            CancellationTokenSource? oldCts;
            lock (_gate)
            {
                if (_loopTask is not null) return Task.CompletedTask;
                oldCts = _cts;
                _cts = new CancellationTokenSource();
                _loopTask = ExecuteAsync(_cts.Token);
            }
            oldCts?.Dispose();
            return Task.CompletedTask;
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
            try { await collector.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }

        private async Task ExecuteAsync(CancellationToken ct)
        {
            try { await collector.ExecuteAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }
}
