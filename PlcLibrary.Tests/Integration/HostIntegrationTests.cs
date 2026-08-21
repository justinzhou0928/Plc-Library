using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;
using System.Collections.Concurrent;

namespace PlcLibrary.Tests.Integration;

[ProtocolDriverName("Test")]
public sealed class TestDriver : IProtocolDriver
{
    private DriverStatus _status = DriverStatus.Disconnected;

    public TestDriver() { }
    public TestDriver(DeviceConfiguration device) { }

    public DriverStatus DriverStatus => _status;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _status = DriverStatus.Connected;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _status = DriverStatus.Disconnected;
        return Task.CompletedTask;
    }

    public Task<bool> TryReconnectAsync(CancellationToken ct = default)
    {
        _status = DriverStatus.Connected;
        return Task.FromResult(true);
    }

    public Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
    {
        var results = new DriverResult[points.Length];
        for (var i = 0; i < points.Length; i++)
            results[i] = DriverResult.Good(points[i].Address, 42);
        return Task.FromResult(results);
    }

    public Task<DriverResult[]> WriteAsync(IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
    {
        var results = new DriverResult[values.Count];
        int idx = 0;
        foreach (var kv in values)
            results[idx++] = DriverResult.Good(kv.Key.Address, null);
        return Task.FromResult(results);
    }

    public ValueTask DisposeAsync() => default;
}

public sealed class CaptureHandler : IDataHandler
{
    private readonly ConcurrentQueue<DriverResult> _results = new();

    public int Count => _results.Count;
    public IReadOnlyCollection<DriverResult> Results => _results;

    public ValueTask HandleAsync(DriverResult result, CancellationToken ct)
    {
        _results.Enqueue(result);
        return default;
    }
}

public class HostIntegrationTests
{
    private static DeviceConfiguration CreateDevice(string id, string connectionString, params string[] tags) => new()
    {
        Id = id,
        Protocol = "Test",
        ConnectionString = connectionString,
        CollectionInterval = TimeSpan.FromMilliseconds(100),
        TagPoints = tags.Select(t => new TagPointConfiguration { TagId = t, Address = t }).ToArray()
    };

    private static IHost BuildHost(CaptureHandler handler) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPlcLibrary();
                services.AddDriver<TestDriver>();
                services.AddSingleton(handler);
                services.AddSingleton<IDataHandler>(sp => sp.GetRequiredService<CaptureHandler>());
            })
            .Build();

    private static async Task StopHostAsync(IHost host)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StopAsync(cts.Token);
        (host as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task FullPipeline_AppliesDevice_DataFlowsToHandler()
    {
        var handler = new CaptureHandler();
        var host = BuildHost(handler);

        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var device = CreateDevice("dev-01", "host:127.0.0.1", "40001");

            await scheduler.ApplyDevicesAsync([device]);
            await Task.Delay(500);

            Assert.True(handler.Count > 0, "Expected at least one result from the pipeline");
            Assert.All(handler.Results, r => Assert.Equal("dev-01", r.DeviceId));
            Assert.All(handler.Results, r => Assert.Equal(QualityCode.Good, r.Status));
        }
        finally
        {
            await StopHostAsync(host);
        }
    }

    [Fact]
    public async Task FullPipeline_MultipleDevices_AllProduceData()
    {
        var handler = new CaptureHandler();
        var host = BuildHost(handler);

        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var devices = new[]
            {
                CreateDevice("dev-01", "host:127.0.0.1", "40001"),
                CreateDevice("dev-02", "host:127.0.0.1;port:502", "40002"),
            };

            await scheduler.ApplyDevicesAsync(devices);
            await Task.Delay(500);

            var deviceIds = handler.Results.Select(r => r.DeviceId).Distinct().ToHashSet();
            Assert.Contains("dev-01", deviceIds);
            Assert.Contains("dev-02", deviceIds);
        }
        finally
        {
            await StopHostAsync(host);
        }
    }

    [Fact]
    public async Task FullPipeline_RemovingDevice_StopsDataCollection()
    {
        var handler = new CaptureHandler();
        var host = BuildHost(handler);

        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var device = CreateDevice("dev-01", "host:127.0.0.1", "40001");

            await scheduler.ApplyDevicesAsync([device]);
            await Task.Delay(400);
            Assert.True(handler.Count > 0, "Expected data before removing device");

            await scheduler.ApplyDevicesAsync([]);

            // 等管道排空移除时刻的在途数据，然后验证计数不再增长（采集确已停止）
            await Task.Delay(600);
            var countAfterRemoval = handler.Count;
            await Task.Delay(400);
            var countStable = handler.Count;

            Assert.Equal(countAfterRemoval, countStable);

            // 设备列表应为空
            var health = await scheduler.GetDeviceHealthAsync();
            Assert.Empty(health);
        }
        finally
        {
            await StopHostAsync(host);
        }
    }

    [Fact]
    public async Task FullPipeline_HostLifetime_StartsAndStopsCleanly()
    {
        var handler = new CaptureHandler();
        var host = BuildHost(handler);

        await host.StartAsync();

        var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
        var device = CreateDevice("dev-01", "host:127.0.0.1", "40001");

        await scheduler.ApplyDevicesAsync([device]);
        await Task.Delay(200);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StopAsync(cts.Token);
        host.Dispose();
    }

    [Fact]
    public async Task FullPipeline_DisabledDevice_IsSkipped()
    {
        var handler = new CaptureHandler();
        var host = BuildHost(handler);

        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var device = CreateDevice("dev-01", "host:127.0.0.1", "40001") with { Enabled = false };

            await scheduler.ApplyDevicesAsync([device]);
            await Task.Delay(300);

            Assert.Equal(0, handler.Count);
        }
        finally
        {
            await StopHostAsync(host);
        }
    }

    [Fact]
    public async Task FullPipeline_UnregisteredProtocol_IsSkipped()
    {
        var handler = new CaptureHandler();
        var host = BuildHost(handler);

        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var device = CreateDevice("dev-01", "host:127.0.0.1", "40001") with { Protocol = "Unknown" };

            await scheduler.ApplyDevicesAsync([device]);
            await Task.Delay(300);

            Assert.Equal(0, handler.Count);
        }
        finally
        {
            await StopHostAsync(host);
        }
    }
}
