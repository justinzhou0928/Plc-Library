using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PlcLibrary.Controller.Collectors;
using PlcLibrary.Controller.Engine;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;

namespace PlcLibrary.Tests.Controller;

public class TaskActuatorTests
{
    private readonly ILogger<PollingCollector> _pollerLogger = NullLogger<PollingCollector>.Instance;
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
    public async Task StartAsync_FirstCall_ReturnsCompletedTask()
    {
        await using var actuator = Create();
        var result = actuator.StartAsync();
        Assert.True(result.IsCompleted);
        Assert.True(actuator.IsRunning);
    }

    [Fact]
    public async Task StartAsync_DoubleCall_ReturnsImmediately()
    {
        await using var actuator = Create();
        _ = actuator.StartAsync();
        var result = actuator.StartAsync();
        Assert.True(result.IsCompleted);
        Assert.True(actuator.IsRunning);
    }

    [Fact]
    public async Task StopAsync_BeforeStart_ReturnsImmediately()
    {
        var actuator = Create();
        await actuator.StopAsync();
        Assert.False(actuator.IsRunning);
    }

    [Fact]
    public async Task StopAsync_AfterStart_Completes()
    {
        var actuator = Create();
        _ = actuator.StartAsync();
        await actuator.StopAsync();
        Assert.False(actuator.IsRunning);
    }

    [Fact]
    public async Task StartStopStart_RestartWorks()
    {
        var actuator = Create();
        _ = actuator.StartAsync();
        await actuator.StopAsync();
        _ = actuator.StartAsync();
        Assert.True(actuator.IsRunning);
        await actuator.StopAsync();
        Assert.False(actuator.IsRunning);
    }

    [Fact]
    public async Task PollingCollector_DeliversDataToPipeline()
    {
        _accessor.Setup(a => a.ReadAsync(_device, It.IsAny<TagPointConfiguration[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([DriverResult.Good("DB1.DBD0", 42)]);

        var actuator = Create();
        _ = actuator.StartAsync();
        await Task.Delay(150);
        await actuator.StopAsync();

        _pipeline.Verify(p => p.HandleAsync(
            It.Is<DriverResult>(r => r.Address == "DB1.DBD0" && Equals(r.Value, 42)),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DisposeAsync_WithoutStart_DoesNotThrow()
    {
        var actuator = Create();
        await actuator.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_AfterStart_StopsAndDisposes()
    {
        var actuator = Create();
        _ = actuator.StartAsync();
        await actuator.DisposeAsync();
    }

    private TaskActuator Create()
    {
        var collector = new PollingCollector(_pollerLogger, _device, _accessor.Object, _pipeline.Object);
        return new TaskActuator(_device, collector);
    }
}
