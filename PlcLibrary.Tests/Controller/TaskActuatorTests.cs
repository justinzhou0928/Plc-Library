using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;
using TaskActuator = PlcLibrary.Controller.Engine.TaskActuator;

namespace PlcLibrary.Tests.Controller;

public class TaskActuatorTests
{
    private readonly ILogger<TaskActuator> _logger = NullLogger<TaskActuator>.Instance;
    private readonly Mock<IDeviceAccessor> _accessor = new();
    private readonly Mock<IDataPipeline> _pipeline = new();
    private readonly DeviceConfiguration _device = new()
    {
        Id = "plc-01",
        Protocol = "S7",
        ConnectionString = "host:127.0.0.1;port:102",
        CollectionInterval = TimeSpan.FromMilliseconds(50),
        TagPoints = [new TagPointConfiguration { TagId = "tag1", Address = "DB1.DBD0" }]
    };

    [Fact]
    public void StartAsync_FirstCall_ReturnsCompletedTask()
    {
        using var actuator = Create();
        var result = actuator.StartAsync();
        Assert.True(result.IsCompleted);
    }

    [Fact]
    public void StartAsync_DoubleCall_ReturnsImmediately()
    {
        using var actuator = Create();
        actuator.StartAsync();
        var result = actuator.StartAsync();
        Assert.True(result.IsCompleted);
    }

    [Fact]
    public async Task StopAsync_BeforeStart_ReturnsImmediately()
    {
        using var actuator = Create();
        await actuator.StopAsync();
    }

    [Fact]
    public async Task StopAsync_AfterStart_Completes()
    {
        using var actuator = Create();
        actuator.StartAsync();
        await actuator.StopAsync();
    }

    [Fact]
    public async Task StartStopStart_RestartWorks()
    {
        using var actuator = Create();
        actuator.StartAsync();
        await actuator.StopAsync();
        actuator.StartAsync();
        await actuator.StopAsync();
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        var actuator = Create();
        actuator.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_WithoutStart_DoesNotThrow()
    {
        var actuator = Create();
        await actuator.DisposeAsync();
    }

    private TaskActuator Create() => new(_logger, _device, _accessor.Object, _pipeline.Object);
}
