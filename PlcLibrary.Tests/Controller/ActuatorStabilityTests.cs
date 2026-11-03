using PlcLibrary.Controller.Engine;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.Tests.Controller;

/// <summary>采集循环不响应取消时，停机/监督不能永久等待。</summary>
public class ActuatorStabilityTests
{
    private static readonly DeviceConfiguration Device = new()
    {
        Id = "plc-stall",
        Protocol = "Test",
        ConnectionString = "host:127.0.0.1",
        TagPoints = [new TagPointConfiguration { TagId = "t1", Address = "40001" }]
    };

    [Fact]
    public async Task IsStalled_WhenHeartbeatIsOld()
    {
        var collector = new HangingCollector
        {
            LastActivityUtc = DateTime.UtcNow.AddMinutes(-10),
            StallTimeout = TimeSpan.FromSeconds(1),
        };
        var actuator = new TaskActuator(Device, collector);
        await actuator.StartAsync();

        Assert.True(actuator.IsRunning);
        Assert.True(actuator.IsStalled);
    }

    [Fact]
    public async Task StopAsync_WhenLoopIgnoresCancellation_CompletesWithinBound()
    {
        var collector = new HangingCollector();
        var actuator = new TaskActuator(Device, collector)
        {
            StopTimeout = TimeSpan.FromMilliseconds(100),
            CollectorDisposeTimeout = TimeSpan.FromMilliseconds(100),
        };
        await actuator.StartAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await actuator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        sw.Stop();

        Assert.False(actuator.IsRunning);
        Assert.NotNull(actuator.LastError);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"StopAsync took {sw.Elapsed}.");
    }

    [Fact]
    public async Task StopAsync_IsIdempotent_AfterHungLoopDetached()
    {
        var collector = new HangingCollector();
        var actuator = new TaskActuator(Device, collector)
        {
            StopTimeout = TimeSpan.FromMilliseconds(50),
            CollectorDisposeTimeout = TimeSpan.FromMilliseconds(50),
        };
        await actuator.StartAsync();

        await actuator.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await actuator.StopAsync().WaitAsync(TimeSpan.FromSeconds(1)); // 第二次立即返回，不等待挂起循环
    }

    private sealed class HangingCollector : IDeviceCollector, ICollectorHeartbeat
    {
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

        public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(1);

        public Task ExecuteAsync(CancellationToken ct) => new TaskCompletionSource<bool>().Task;

        public ValueTask DisposeAsync() => new(new TaskCompletionSource<bool>().Task);
    }
}
