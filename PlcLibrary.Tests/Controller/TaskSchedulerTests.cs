using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;
using TaskScheduler = PlcLibrary.Controller.Engine.TaskScheduler;

namespace PlcLibrary.Tests.Controller;

public class TaskSchedulerTests
{
    private readonly Mock<IServiceProvider> _sp = new();
    private readonly ILogger<TaskScheduler> _logger = NullLogger<TaskScheduler>.Instance;
    private readonly Mock<IDriverFactory> _factory = new();
    private readonly Mock<IDataPipeline> _pipeline = new();

    private static DeviceConfiguration ValidDevice(string id, string protocol = "S7") => new()
    {
        Id = id,
        Protocol = protocol,
        ConnectionString = "host:127.0.0.1;port:102",
        CollectionInterval = TimeSpan.FromSeconds(1),
        TagPoints = [new TagPointConfiguration { TagId = "t1", Address = "DB1.DBD0" }]
    };

    [Fact]
    public async Task ApplyDevicesAsync_EmptyList_DoesNotThrow()
    {
        var scheduler = Create();
        await scheduler.ApplyDevicesAsync([]);
    }

    [Fact]
    public async Task ApplyDevicesAsync_DisabledDevice_IsSkipped()
    {
        var scheduler = Create();
        var d = ValidDevice("d1") with { Enabled = false };
        await scheduler.ApplyDevicesAsync([d]);
    }

    [Fact]
    public async Task ApplyDevicesAsync_InvalidDevice_IsSkipped()
    {
        var scheduler = Create();
        var d = ValidDevice("d1") with { Id = "" };
        await scheduler.ApplyDevicesAsync([d]);
    }

    [Fact]
    public async Task ApplyDevicesAsync_UnregisteredProtocol_IsSkipped()
    {
        var scheduler = Create();
        var d = ValidDevice("d1", "UnknownProtocol");
        await scheduler.ApplyDevicesAsync([d]);
    }

    [Fact]
    public async Task StopSchedulerAsync_EmptyActuators_DoesNotThrow()
    {
        var scheduler = Create();
        await scheduler.StopSchedulerAsync();
    }

    [Fact]
    public void DisposeResources_DoesNotThrow()
    {
        var scheduler = Create();
        scheduler.DisposeResources();
    }

    private TaskScheduler Create()
    {
        _factory.Setup(f => f.ProtocolDriverName).Returns("S7");
        _factory.Setup(f => f.SupportsPush).Returns(false);
        return new TaskScheduler(_sp.Object, _logger, [_factory.Object], _pipeline.Object);
    }
}
