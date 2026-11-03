using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General;
using PlcLibrary.Pipeline.Engine;
using PlcLibrary.Pipeline.Interfaces;
using PlcLibrary.Pipeline.Models;
using System.Diagnostics.Metrics;

namespace PlcLibrary.Tests.Pipeline;

public class DriverResultPipelineTests
{
    private readonly ILogger<DriverResultPipeline> _logger = NullLogger<DriverResultPipeline>.Instance;
    private readonly PipelineOptions _options = new()
    {
        Capacity = 100,
        MaxHandlerParallelism = 2,
        HandlerTimeout = TimeSpan.FromSeconds(5),
    };
    private readonly Mock<IOptions<PipelineOptions>> _optionsWrapper = new();

    public DriverResultPipelineTests()
    {
        _optionsWrapper.Setup(o => o.Value).Returns(_options);
    }

    [Fact]
    public async Task ConsumeAsync_DispatchesToHandlers()
    {
        var handler = new Mock<IDataHandler>();
        var services = new ServiceCollection();
        services.AddSingleton(handler.Object);
        var sp = services.BuildServiceProvider();

        var pipeline = new DriverResultPipeline(sp, _logger, _optionsWrapper.Object);

        var consumeTask = pipeline.ConsumeAsync(CancellationToken.None);

        var result = DriverResult.Good("40001", 42);
        await pipeline.HandleAsync(result, CancellationToken.None);
        pipeline.StopConsuming();

        await consumeTask;
        handler.Verify(h => h.HandleAsync(It.IsAny<DriverResult>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WritesToChannel()
    {
        var handler = new Mock<IDataHandler>();
        var services = new ServiceCollection();
        services.AddSingleton(handler.Object);
        var sp = services.BuildServiceProvider();

        var pipeline = new DriverResultPipeline(sp, _logger, _optionsWrapper.Object);

        var consumeTask = pipeline.ConsumeAsync(CancellationToken.None);

        await pipeline.HandleAsync(DriverResult.Good("40001", 42), CancellationToken.None);
        pipeline.StopConsuming();

        await consumeTask;

        handler.Verify(h => h.HandleAsync(It.IsAny<DriverResult>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StopConsuming_CompletesConsumerLoop()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var pipeline = new DriverResultPipeline(sp, _logger, _optionsWrapper.Object);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = pipeline.ConsumeAsync(cts.Token);

        pipeline.StopConsuming();
        await consumeTask;

        Assert.True(consumeTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task HandlerFailure_DoesNotCrashPipeline()
    {
        var handler = new Mock<IDataHandler>();
        handler.Setup(h => h.HandleAsync(It.IsAny<DriverResult>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Handler error"));

        var services = new ServiceCollection();
        services.AddSingleton(handler.Object);
        var sp = services.BuildServiceProvider();

        var pipeline = new DriverResultPipeline(sp, _logger, _optionsWrapper.Object);

        var consumeTask = pipeline.ConsumeAsync(CancellationToken.None);

        await pipeline.HandleAsync(DriverResult.Good("40001", 42), CancellationToken.None);
        pipeline.StopConsuming();

        await consumeTask;
    }

    [Fact]
    public async Task ConsumerCancellationToken_PropagatesToHandlers()
    {
        // 回归保护：DispatchAsync 按次构造 ParallelOptions，消费侧取消必须传入 handler 调用。
        // handler 保持在途（阻塞在 release 上），确保断言时 linkedCts 尚未释放、链接仍生效。
        var observed = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Mock<IDataHandler>();
        handler.Setup(h => h.HandleAsync(It.IsAny<DriverResult>(), It.IsAny<CancellationToken>()))
            .Callback<DriverResult, CancellationToken>((_, token) => observed.TrySetResult(token))
            .Returns(async () => { await release.Task; });

        var services = new ServiceCollection();
        services.AddSingleton(handler.Object);
        var sp = services.BuildServiceProvider();

        var pipeline = new DriverResultPipeline(sp, _logger, _optionsWrapper.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var consumeTask = pipeline.ConsumeAsync(cts.Token);

        await pipeline.HandleAsync(DriverResult.Good("40001", 1), CancellationToken.None);
        var handlerToken = await observed.Task;

        cts.Cancel();
        Assert.True(handlerToken.IsCancellationRequested,
            "handler 收到的令牌应派生自 ConsumeAsync 的取消令牌");

        release.SetResult();
        pipeline.StopConsuming();
        await consumeTask;
    }

    [Fact]
    public async Task Dispatch_BadResult_IncrementsBadPointsCounter()
    {
        long badPoints = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, PlcMetrics.Meter) && instrument.Name == "plc.points.bad")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
            Interlocked.Add(ref badPoints, measurement));
        listener.Start();

        var services = new ServiceCollection().BuildServiceProvider();
        var pipeline = new DriverResultPipeline(services, _logger, _optionsWrapper.Object);
        var consumeTask = pipeline.ConsumeAsync(CancellationToken.None);

        await pipeline.HandleAsync(
            DriverResult.Bad("40001", QualityCode.BadCommFailure, "bad") with { DeviceId = "d1" },
            CancellationToken.None);
        pipeline.StopConsuming();
        await consumeTask;

        Assert.True(badPoints >= 1, $"plc.points.bad 未记录 Bad 结果，实际 {badPoints}");
    }
}
