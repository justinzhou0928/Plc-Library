using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcLibrary.Controller.Engine;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General.Configuration;
using TaskScheduler = PlcLibrary.Controller.Engine.TaskScheduler;

namespace PlcLibrary.Tests.Controller;

/// <summary>
/// 停机与热更新并发：ApplyDevicesAsync 在创建推送驱动时被阻塞，StopSchedulerAsync 先行返回；
/// 放行后不得再启动执行器，否则宿主停机后仍会残留采集任务。
/// </summary>
public class SchedulerShutdownRaceTests
{
    [Fact]
    public async Task StopScheduler_WhileApplyIsBlocked_DoesNotLeaveRunningActuator()
    {
        var factory = new BlockingPushFactory();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPlcLibrary();
                services.AddSingleton(factory);
                services.AddSingleton<IDriverFactory>(sp => sp.GetRequiredService<BlockingPushFactory>());
            })
            .Build();

        await host.StartAsync();
        var scheduler = host.Services.GetRequiredService<TaskScheduler>();
        var device = new DeviceConfiguration
        {
            Id = "race-01",
            Protocol = "BlockingPush",
            ConnectionString = "host:127.0.0.1",
            TagPoints = [new TagPointConfiguration { TagId = "t1", Address = "n1" }],
        };

        try
        {
            var apply = scheduler.ApplyDevicesAsync([device]);
            await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await scheduler.StopSchedulerAsync().WaitAsync(TimeSpan.FromSeconds(5));
            factory.Release.TrySetResult();

            await apply.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(await scheduler.GetDeviceHealthAsync());
        }
        finally
        {
            factory.Release.TrySetResult();
            try
            { await scheduler.StopSchedulerAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
            try
            { await host.StopAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
    }

    private sealed class BlockingPushFactory : IDriverFactory
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProtocolDriverName => "BlockingPush";
        public bool SupportsPush => true;

        public async Task<IProtocolDriver> CreateAsync(DeviceConfiguration device, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new BlockingPushDriver();
        }
    }

    private sealed class BlockingPushDriver : IPushProtocolDriver
    {
        public DriverStatus DriverStatus => DriverStatus.Disconnected;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryReconnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<DriverResult>());
        public Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<DriverResult>());
        public Task StartPushingAsync(
            TagPointConfiguration[] points,
            Func<DriverResult, CancellationToken, ValueTask> onData,
            CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
        public Task StopPushingAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
    }
}
