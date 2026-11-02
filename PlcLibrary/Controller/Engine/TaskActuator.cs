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
        private Exception? _lastError;

        public DeviceConfiguration Device => device;

        /// <summary>停止时等待采集循环退出的上限；超时后强制释放采集器并放弃旧循环。</summary>
        internal TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>强制释放采集器（驱动/订阅）时的等待上限。</summary>
        internal TimeSpan CollectorDisposeTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public bool IsRunning
        {
            get { lock (_gate) { return _loopTask is { IsCompleted: false }; } }
        }

        /// <summary>任务仍在运行，但心跳超过采集器阈值没有更新（典型：阻塞在 ReadAsync/分发）。</summary>
        public bool IsStalled
        {
            get
            {
                if (!IsRunning || collector is not ICollectorHeartbeat heartbeat) return false;
                return DateTime.UtcNow - heartbeat.LastActivityUtc > heartbeat.StallTimeout;
            }
        }

        /// <summary>后台循环最近一次异常（用于健康状态展示）；正常运行时为 null。</summary>
        public Exception? LastError
        {
            get { lock (_gate) return _lastError; }
        }

        public Task StartAsync()
        {
            CancellationTokenSource? oldCts;
            lock (_gate)
            {
                if (_loopTask is not null) return Task.CompletedTask;
                oldCts = _cts;
                _cts = new CancellationTokenSource();
                _lastError = null;
                _loopTask = ExecuteAsync(_cts.Token);
            }
            oldCts?.Dispose();
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            Task? loop;
            CancellationTokenSource? cts;
            lock (_gate)
            {
                if (_loopTask is null) return;
                cts = _cts;
                loop = _loopTask;
            }

            cts?.Cancel();

            var completed = await Task.WhenAny(loop, Task.Delay(StopTimeout)).ConfigureAwait(false);
            if (completed != loop)
            {
                // 循环不响应取消：强制释放采集器（关闭 socket/订阅），再给一次退出机会。
                try
                {
                    await collector.DisposeAsync().AsTask()
                        .WaitAsync(CollectorDisposeTimeout)
                        .ConfigureAwait(false);
                }
                catch { }

                await Task.WhenAny(loop, Task.Delay(CollectorDisposeTimeout)).ConfigureAwait(false);
            }

            if (loop.IsCompleted)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // 采集循环失败不应让停机流程抛异常（否则 StopAsync 会沿宿主生命周期冒泡）
                    lock (_gate) _lastError = ex;
                }
            }
            else
            {
                lock (_gate)
                {
                    _lastError ??= new TimeoutException(
                        $"Collector for device '{device.Id}' did not stop within {StopTimeout.TotalSeconds}s.");
                }
            }

            lock (_gate) { if (ReferenceEquals(_loopTask, loop)) _loopTask = null; }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _cts?.Dispose();
            try
            {
                await collector.DisposeAsync().AsTask()
                    .WaitAsync(CollectorDisposeTimeout)
                    .ConfigureAwait(false);
            }
            catch { }
        }

        private async Task ExecuteAsync(CancellationToken ct)
        {
            try { await collector.ExecuteAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // 记录并结束循环：由 TaskScheduler 的监督循环按退避策略重建执行器
                lock (_gate) _lastError = ex;
            }
        }
    }
}
