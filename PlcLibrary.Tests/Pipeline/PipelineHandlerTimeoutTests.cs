using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.Pipeline.Engine;
using PlcLibrary.Pipeline.Interfaces;
using PlcLibrary.Pipeline.Models;

namespace PlcLibrary.Tests.Pipeline;

/// <summary>handler 忽略取消令牌时，管道消费者不能被永久卡住。</summary>
public class PipelineHandlerTimeoutTests
{
    [Fact]
    public async Task HandlerIgnoringCancellation_DoesNotBlockSubsequentResults()
    {
        var handler = new BlockingFirstHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IDataHandler>(handler);
        await using var sp = services.BuildServiceProvider();

        var pipeline = new DriverResultPipeline(
            sp,
            NullLogger<DriverResultPipeline>.Instance,
            Options.Create(new PipelineOptions
            {
                Capacity = 10,
                MaxHandlerParallelism = 1,
                HandlerTimeout = TimeSpan.FromMilliseconds(100),
            }));

        using var cts = new CancellationTokenSource();
        var consuming = pipeline.ConsumeAsync(cts.Token);

        await pipeline.HandleAsync(DriverResult.Good("a", 1), CancellationToken.None);
        await pipeline.HandleAsync(DriverResult.Good("b", 2), CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        // 第一条 handler 永不完成并忽略取消；100ms 后管道必须继续处理第二条
        await WaitUntilAsync(() => handler.Calls >= 2, TimeSpan.FromSeconds(3));

        cts.Cancel();
        await consuming.WaitAsync(TimeSpan.FromSeconds(2));
        pipeline.DisposeResources();
    }

    [Fact]
    public async Task EnqueueTimeout_DropsResult_WhenConsumerIsStuck()
    {
        var services = new ServiceCollection();
        await using var sp = services.BuildServiceProvider();

        var pipeline = new DriverResultPipeline(
            sp,
            NullLogger<DriverResultPipeline>.Instance,
            Options.Create(new PipelineOptions
            {
                Capacity = 1,
                MaxHandlerParallelism = 1,
                HandlerTimeout = TimeSpan.FromSeconds(1),
                EnqueueTimeout = TimeSpan.FromMilliseconds(100),
            }));

        // 没有消费者：第一次写入占满容量，第二次必须在 EnqueueTimeout 后丢弃而不是永久阻塞
        await pipeline.HandleAsync(DriverResult.Good("a", 1), CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await pipeline.HandleAsync(DriverResult.Good("b", 2), CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Second enqueue took {sw.Elapsed}.");
        pipeline.DisposeResources();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class BlockingFirstHandler : IDataHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask HandleAsync(DriverResult result, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
                return new ValueTask(new TaskCompletionSource<bool>().Task); // 忽略 ct，永不完成

            return ValueTask.CompletedTask;
        }
    }
}
