using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General.Configuration;
using PlcLibrary.Monitor.Extensions;
using PlcLibrary.Monitor.Interfaces;
using PlcLibrary.Tests.Integration;

namespace PlcLibrary.Tests.Monitor;

public class MonitorIntegrationTests
{
    private static DeviceConfiguration CreateDevice(string id, string connectionString, params string[] tags) => new()
    {
        Id = id,
        Protocol = "Test",
        ConnectionString = connectionString,
        CollectionInterval = TimeSpan.FromMilliseconds(100),
        TagPoints = tags.Select(t => new TagPointConfiguration { TagId = t, Address = t }).ToArray()
    };

    private static IHost BuildHost() =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPlcLibrary();
                services.AddDriver<TestDriver>();
                services.AddPlcMonitor();
            })
            .Build();

    [Fact]
    public async Task Monitor_ReceivesPipelineData_AndServesToSubscriber()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var host = BuildHost();
        await host.StartAsync();
        IAsyncEnumerator<DriverResult>? enumerator = null;
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var monitor = host.Services.GetRequiredService<IPlcMonitor>();

            await scheduler.ApplyDevicesAsync([CreateDevice("dev-01", "host:127.0.0.1", "40001")], cts.Token);

            enumerator = monitor.SubscribeAsync("dev-01", "40001", cts.Token).GetAsyncEnumerator();

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("dev-01", enumerator.Current.DeviceId);
            Assert.Equal("40001", enumerator.Current.TagId);
            Assert.Equal(42, (int)enumerator.Current.Value!);
            Assert.Equal(QualityCode.Good, enumerator.Current.Status);
        }
        finally
        {
            cts.Cancel();
            if (enumerator is not null)
                await enumerator.DisposeAsync();
            await StopHostAsync(host);
        }
    }

    [Fact]
    public async Task Monitor_SuppressesUnchangedValues()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var host = BuildHost();
        await host.StartAsync();
        IAsyncEnumerator<DriverResult>? enumerator = null;
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var monitor = host.Services.GetRequiredService<IPlcMonitor>();

            await scheduler.ApplyDevicesAsync([CreateDevice("dev-01", "host:127.0.0.1", "40001")], cts.Token);

            using var subCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            enumerator = monitor.SubscribeAsync("dev-01", "40001", subCts.Token).GetAsyncEnumerator();

            // 首个轮询值
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(42, (int)enumerator.Current.Value!);

            // 驱动每次返回固定 42：后续多次轮询（间隔 100ms）应全部被去重，
            // 600ms 内不应有第二个元素——读操作只能被超时取消。
            subCts.CancelAfter(600);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }
        finally
        {
            cts.Cancel();
            if (enumerator is not null)
                await enumerator.DisposeAsync();
            await StopHostAsync(host);
        }
    }

    [Fact]
    public async Task FullPipeline_DuplicateDeviceId_FirstWins()
    {
        var host = BuildHost();
        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var monitor = host.Services.GetRequiredService<IPlcMonitor>();

            // 两个设备 Id 相同，但点位数量不同（用于区分 first-wins / last-wins）
            var first = CreateDevice("dev-01", "host:127.0.0.1", "40001");
            var second = CreateDevice("dev-01", "host:127.0.0.1;port:502", "40001", "40002");

            await scheduler.ApplyDevicesAsync([first, second]);
            await Task.Delay(500);

            // first wins：只有第一个设备的 1 个点位被采集（若 last-wins 会是 2 个）
            Assert.Single(monitor.GetDevice("dev-01"));
        }
        finally
        {
            await StopHostAsync(host);
        }
    }

    private static async Task StopHostAsync(IHost host)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StopAsync(cts.Token);
        (host as IDisposable)?.Dispose();
    }
}
