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
    public sealed class DriverResultPipeline(
        IServiceProvider sp,
        ILogger<DriverResultPipeline> logger,
        IOptions<PipelineOptions> options) : IDataPipeline, IAsyncDisposable
    {
        private readonly Channel<DriverResult> _channel = Channel.CreateBounded<DriverResult>(
            new BoundedChannelOptions(options.Value.Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        private readonly ConcurrentDictionary<Guid, Channel<DriverResult>> _subscribers = new();
        private readonly IDataHandler[] _handlers = sp.GetServices<IDataHandler>().ToArray();
        private readonly SemaphoreSlim _handlerGate = new(Math.Max(1, options.Value.MaxHandlerParallelism));

        public async Task StartAsync(CancellationToken ct)
        {
            if (_handlers.Length > 0)
                PipelineLog.LogHandlersRegistered(logger, _handlers.Length);

            try
            {
                await foreach (var result in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await DispatchAsync(result, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PipelineLog.LogFanoutFailed(logger, ex); }
            finally
            {
                foreach (var (_, sub) in _subscribers)
                    sub.Writer.TryComplete();
                PipelineLog.LogPipelineStopped(logger);
            }
        }

        public Task StopAsync(CancellationToken ct)
        {
            _channel.Writer.TryComplete();
            return Task.CompletedTask;
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

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            foreach (var (_, sub) in _subscribers)
                sub.Writer.TryComplete();
            _handlerGate.Dispose();
            return default;
        }

        private async ValueTask DispatchAsync(DriverResult result, CancellationToken ct)
        {
            if (_handlers.Length > 0)
            {
                await Task.WhenAll(_handlers.Select(h => InvokeHandlerAsync(h, result, ct))).ConfigureAwait(false);
            }

            if (!_subscribers.IsEmpty)
            {
                foreach (var (_, sub) in _subscribers)
                    sub.Writer.TryWrite(result);
            }
        }

        private async Task InvokeHandlerAsync(IDataHandler handler, DriverResult result, CancellationToken ct)
        {
            await _handlerGate.WaitAsync(ct).ConfigureAwait(false);
            try { await handler.HandleAsync(result, ct).ConfigureAwait(false); }
            catch (Exception ex) { PipelineLog.LogHandlerFailed(logger, ex, handler.GetType().Name); }
            finally { _handlerGate.Release(); }
        }
    }

    internal sealed class PipelineHostedService(IDataPipeline pipeline) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => pipeline.StartAsync(ct);
        public Task StopAsync(CancellationToken ct) => pipeline.StopAsync(ct);
    }
}
