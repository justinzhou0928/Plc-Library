using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.Pipeline.Engine;
using PlcLibrary.Pipeline.Interfaces;
using PlcLibrary.Pipeline.Models;

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
        var sp = new ServiceCollection().BuildServiceProvider();
        var pipeline = new DriverResultPipeline(sp, _logger, _optionsWrapper.Object);

        var consumeTask = pipeline.ConsumeAsync(CancellationToken.None);

        await pipeline.HandleAsync(DriverResult.Good("40001", 42), CancellationToken.None);
        pipeline.StopConsuming();

        await consumeTask;
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
}
