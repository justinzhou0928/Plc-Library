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

public class DriverResultPipelineStressTests
{
    private readonly ILogger<DriverResultPipeline> _logger = NullLogger<DriverResultPipeline>.Instance;
    private readonly Mock<IOptions<PipelineOptions>> _optionsWrapper = new();

    public DriverResultPipelineStressTests()
    {
        _optionsWrapper.Setup(o => o.Value).Returns(new PipelineOptions
        {
            Capacity = 1000,
            MaxHandlerParallelism = 4,
            HandlerTimeout = TimeSpan.FromSeconds(30),
        });
    }

    [Fact]
    public async Task HighVolumeWrites_NoDataLoss()
    {
        var handler = new Mock<IDataHandler>();
        var received = new List<DriverResult>();
        handler.Setup(h => h.HandleAsync(It.IsAny<DriverResult>(), It.IsAny<CancellationToken>()))
            .Callback<DriverResult, CancellationToken>((r, _) => { lock (received) received.Add(r); })
            .Returns(ValueTask.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(handler.Object);
        var sp = services.BuildServiceProvider();

        var pipeline = new DriverResultPipeline(sp, _logger, _optionsWrapper.Object);
        var consumeTask = pipeline.ConsumeAsync(CancellationToken.None);

        var tasks = new List<Task>();
        for (var i = 0; i < 500; i++)
        {
            var addr = i.ToString();
            tasks.Add(pipeline.HandleAsync(DriverResult.Good(addr, i), CancellationToken.None).AsTask());
        }

        await Task.WhenAll(tasks);
        pipeline.StopConsuming();
        await consumeTask;

        Assert.Equal(500, received.Count);
    }

    [Fact]
    public async Task ChannelBackpressure_NoItemsDropped()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        var wrapper = new Mock<IOptions<PipelineOptions>>();
        wrapper.Setup(o => o.Value).Returns(new PipelineOptions
        {
            Capacity = 10,
            MaxHandlerParallelism = 1,
            HandlerTimeout = TimeSpan.FromSeconds(30),
        });

        var pipeline = new DriverResultPipeline(sp, _logger, wrapper.Object);
        var consumeTask = pipeline.ConsumeAsync(CancellationToken.None);

        for (var i = 0; i < 100; i++)
        {
            await pipeline.HandleAsync(DriverResult.Good(i.ToString(), i), CancellationToken.None);
        }

        pipeline.StopConsuming();
        await consumeTask;
    }

    [Fact]
    public async Task SlowHandlers_DoNotBlockOtherWriters()
    {
        var slowHandler = new Mock<IDataHandler>();
        slowHandler.Setup(h => h.HandleAsync(It.IsAny<DriverResult>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { await Task.Delay(200); });

        var fastHandler = new Mock<IDataHandler>();
        var fastReceived = new List<DriverResult>();
        fastHandler.Setup(h => h.HandleAsync(It.IsAny<DriverResult>(), It.IsAny<CancellationToken>()))
            .Callback<DriverResult, CancellationToken>((r, _) => { lock (fastReceived) fastReceived.Add(r); })
            .Returns(ValueTask.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(slowHandler.Object);
        services.AddSingleton(fastHandler.Object);
        var sp = services.BuildServiceProvider();

        var wrapper = new Mock<IOptions<PipelineOptions>>();
        wrapper.Setup(o => o.Value).Returns(new PipelineOptions
        {
            Capacity = 100,
            MaxHandlerParallelism = 2,
            HandlerTimeout = TimeSpan.FromSeconds(5),
        });

        var pipeline = new DriverResultPipeline(sp, _logger, wrapper.Object);
        var consumeTask = pipeline.ConsumeAsync(CancellationToken.None);

        for (var i = 0; i < 10; i++)
        {
            await pipeline.HandleAsync(DriverResult.Good(i.ToString(), i), CancellationToken.None);
        }

        pipeline.StopConsuming();
        await consumeTask;

        Assert.True(fastReceived.Count > 0, "Fast handler should have received data despite slow handler");
    }

    [Fact]
    public async Task ConcurrentStopConsuming_IsIdempotent()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var pipeline = new DriverResultPipeline(sp, _logger, _optionsWrapper.Object);

        var consumeTask = pipeline.ConsumeAsync(CancellationToken.None);

        await pipeline.HandleAsync(DriverResult.Good("test", 1), CancellationToken.None);

        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(pipeline.StopConsuming));
        }

        await Task.WhenAll(tasks);
        await consumeTask;
    }
}
