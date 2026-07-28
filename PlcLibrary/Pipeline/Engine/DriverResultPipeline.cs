using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General;
using PlcLibrary.Pipeline.Interfaces;
using PlcLibrary.Pipeline.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PlcLibrary.Pipeline.Engine
{
    internal sealed class DriverResultPipeline : IDataPipeline
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<DriverResultPipeline> _logger;
        private readonly Channel<DriverResult> _channel;
        private readonly ConcurrentDictionary<Guid, Channel<DriverResult>> _subscribers = new();
        private readonly ParallelOptions _parallelOptions;
        private readonly TimeSpan _handlerTimeout;
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
            _parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.Value.MaxHandlerParallelism,
            };
            _handlerTimeout = options.Value.HandlerTimeout;
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
            foreach (var (_, sub) in _subscribers)
                sub.Writer.TryComplete();
            PipelineLog.LogPipelineStopped(_logger);
        }

        internal void DisposeResources()
        {
            _channel.Writer.TryComplete();
            foreach (var (_, sub) in _subscribers)
                sub.Writer.TryComplete();
        }

        public async ValueTask HandleAsync(DriverResult result, CancellationToken ct)
            => await _channel.Writer.WriteAsync(result, ct).ConfigureAwait(false);

        public async IAsyncEnumerable<DriverResult> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var sub = Channel.CreateBounded<DriverResult>(new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            var id = Guid.NewGuid();
            _subscribers[id] = sub;

            try
            {
                await foreach (var item in sub.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    yield return item;
            }
            finally
            {
                if (_subscribers.TryRemove(id, out _))
                    sub.Writer.TryComplete();
            }
        }

        private async ValueTask DispatchAsync(DriverResult result, CancellationToken ct)
        {
            PlcMetrics.PipelineDispatched.Add(1);
            var handlers = _handlers;
            if (handlers.Length > 0)
            {
                _parallelOptions.CancellationToken = ct;
                await Parallel.ForEachAsync(handlers, _parallelOptions, async (handler, token) =>
                {
                    try
                    {
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        linkedCts.CancelAfter(_handlerTimeout);
                        await handler.HandleAsync(result, linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) { PipelineLog.LogHandlerFailed(_logger, ex, handler.GetType().Name); }
                }).ConfigureAwait(false);
            }

            if (!_subscribers.IsEmpty)
            {
                foreach (var (_, sub) in _subscribers)
                {
                    if (!sub.Writer.TryWrite(result))
                    {
                        PlcMetrics.PipelineDropped.Add(1);
                        PipelineLog.LogSubscriberDropped(_logger, result.Address);
                    }
                }
            }
        }
    }
}
