using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General;
using PlcLibrary.Pipeline.Interfaces;
using PlcLibrary.Pipeline.Models;

namespace PlcLibrary.Pipeline.Engine
{
    internal sealed class DriverResultPipeline : IDataPipeline
    {

        private readonly IServiceProvider _sp;
        private readonly ILogger<DriverResultPipeline> _logger;
        private readonly Channel<DriverResult> _channel;
        private readonly int _maxHandlerParallelism;
        private readonly TimeSpan _handlerTimeout;
        private readonly TimeSpan _enqueueTimeout;
        private IDataHandler[] _handlers = [];

        public DriverResultPipeline(
            IServiceProvider sp,
            ILogger<DriverResultPipeline> logger,
            IOptions<PipelineOptions> options)
        {
            _sp = sp;
            _logger = logger;
            _channel = Channel.CreateBounded<DriverResult>(
                new BoundedChannelOptions(options.Value.Capacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });
            // 不持有可变的 ParallelOptions：分发时按次构造，避免并发 DispatchAsync 竞争 CancellationToken
            _maxHandlerParallelism = options.Value.MaxHandlerParallelism;
            _handlerTimeout = options.Value.HandlerTimeout;
            _enqueueTimeout = options.Value.EnqueueTimeout;
        }

        public async Task ConsumeAsync(CancellationToken ct)
        {
            _handlers = _sp.GetServices<IDataHandler>().ToArray();
            PipelineLog.LogHandlersRegistered(_logger, _handlers.Length);

            try
            {
                await foreach (var result in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await DispatchAsync(result, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PipelineLog.LogFanoutFailed(_logger, ex); }
        }

        internal void StopConsuming()
        {
            _channel.Writer.TryComplete();
            PipelineLog.LogPipelineStopped(_logger);
        }

        internal void DisposeResources() => _channel.Writer.TryComplete();

        public async ValueTask HandleAsync(DriverResult result, CancellationToken ct)
        {
            if (_channel.Writer.TryWrite(result))
                return;

            if (_enqueueTimeout <= TimeSpan.Zero)
            {
                await _channel.Writer.WriteAsync(result, ct).ConfigureAwait(false);
                return;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_enqueueTimeout);
            try
            {
                await _channel.Writer.WriteAsync(result, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 管道消费者长时间不前进（handler 阻塞等）：丢弃本点并继续采集，避免采集循环被永久反压。
                PlcMetrics.PipelineDropped.Add(1, new TagList { { "device.id", result.DeviceId } });
                PipelineLog.LogEnqueueTimedOut(_logger, result.Address, _enqueueTimeout);
            }
        }

        private async ValueTask DispatchAsync(DriverResult result, CancellationToken ct)
        {
            using var activity = PlcActivity.Source.StartActivity("PlcLibrary.Dispatch");
            activity?.SetTag("device.id", result.DeviceId);
            activity?.SetTag("tag.id", result.TagId);
            activity?.SetTag("status", result.Status.ToString());

            PlcMetrics.PipelineDispatched.Add(1, new TagList { { "device.id", result.DeviceId } });
            if (result.Status != QualityCode.Good)
            {
                PlcMetrics.BadPoints.Add(1, new TagList
                {
                    { "device.id", result.DeviceId },
                    { "quality", result.Status.ToString() },
                });
            }
            var handlers = _handlers;
            if (handlers.Length == 1)
            {
                // 常见路径（单 handler）：直接调用，避免 Parallel.ForEachAsync 与链接 CTS 的分配
                await InvokeHandlerAsync(handlers[0], result, ct).ConfigureAwait(false);
            }
            else if (handlers.Length > 1)
            {
                if (_maxHandlerParallelism <= 1)
                {
                    foreach (var handler in handlers)
                        await InvokeHandlerAsync(handler, result, ct).ConfigureAwait(false);
                }
                else
                {
                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _maxHandlerParallelism,
                        CancellationToken = ct,
                    };
                    await Parallel.ForEachAsync(handlers, parallelOptions,
                        async (handler, _) => await InvokeHandlerAsync(handler, result, ct).ConfigureAwait(false))
                        .ConfigureAwait(false);
                }
            }

        }

        /// <summary>调用单个 IDataHandler，异常与超时互不影响其它 handler。</summary>
        private async Task InvokeHandlerAsync(IDataHandler handler, DriverResult result, CancellationToken ct)
        {
            CancellationTokenSource? linked = null;
            try
            {
                // 快路径：PlcMonitor 等同步 handler 已在调用内完成，无需链接 CTS/定时器。
                var pending = handler.HandleAsync(result, ct);
                if (pending.IsCompletedSuccessfully)
                {
                    // 同步完成也必须 GetResult，释放 IValueTaskSource 等池化实现
                    pending.GetAwaiter().GetResult();
                    return;
                }

                if (_handlerTimeout <= TimeSpan.Zero)
                {
                    await pending.ConfigureAwait(false);
                    return;
                }

                linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(_handlerTimeout);
                // WaitAsync 提供硬上限：即使 handler 忽略取消令牌，管道消费者也不会被永久卡住。
                await pending.AsTask()
                    .WaitAsync(_handlerTimeout, ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                CancelQuietly(linked);
                PipelineLog.LogHandlerFailed(_logger,
                    new TimeoutException($"Handler '{handler.GetType().Name}' timed out after {_handlerTimeout.TotalSeconds}s.", ex),
                    handler.GetType().Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancel 回调顺序可能让 WaitAsync 先完成；显式取消链接源，保证 handler 能观察到取消。
                CancelQuietly(linked);
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                CancelQuietly(linked);
                PipelineLog.LogHandlerFailed(_logger,
                    new TimeoutException($"Handler '{handler.GetType().Name}' was cancelled after {_handlerTimeout.TotalSeconds}s."),
                    handler.GetType().Name);
            }
            catch (Exception ex)
            {
                PipelineLog.LogHandlerFailed(_logger, ex, handler.GetType().Name);
            }
            finally
            {
                linked?.Dispose();
            }
        }

        private static void CancelQuietly(CancellationTokenSource? cts)
        {
            try
            { cts?.Cancel(); }
            catch { }
        }
    }
}
