using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlcLibrary.Controller.Collectors;
using PlcLibrary.Controller.Engine;
using TaskScheduler = PlcLibrary.Controller.Engine.TaskScheduler;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;

namespace PlcLibrary.Tests.Controller;

/// <summary>
/// 加速浸泡：真实 TaskScheduler + 连接池 + PollingCollector，注入周期性传输故障，
/// 验证长时间运行后仍持续采集、故障驱动被重建、停机不挂起。
/// </summary>
public class CollectionSoakTests
{
    [Fact]
    public async Task PollingCollection_UnderRepeatedTransportFailures_KeepsRunningAndStopsCleanly()
    {
        var driverFactory = new SoakFactory();
        var pipeline = new CountingPipeline();
        var pool = new DeviceDriverPool(
            Options.Create(new PoolOptions
            {
                MaxConnectionsPerDevice = 1,
                MaxRetryAttempts = 1,
                RetryDelay = TimeSpan.FromMilliseconds(5),
                OperationTimeout = TimeSpan.FromMilliseconds(200),
                PoolIdleTimeout = TimeSpan.Zero,
            }),
            [driverFactory],
            NullLoggerFactory.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<ILogger<PollingCollector>>(NullLogger<PollingCollector>.Instance);
        services.AddSingleton<IDeviceAccessor>(pool);
        services.AddSingleton<IDataPipeline>(pipeline);
        await using var sp = services.BuildServiceProvider();

        var scheduler = new TaskScheduler(
            sp,
            NullLogger<TaskScheduler>.Instance,
            [driverFactory],
            pipeline)
        {
            SupervisionInterval = TimeSpan.FromMilliseconds(100)
        };

        using var cts = new CancellationTokenSource();
        var supervising = scheduler.SuperviseAsync(cts.Token);

        var device = new DeviceConfiguration
        {
            Id = "soak-01",
            Protocol = "Soak",
            ConnectionString = "host:127.0.0.1",
            ConnectionTimeout = TimeSpan.FromMilliseconds(200),
            CollectionInterval = TimeSpan.FromMilliseconds(20),
            TagPoints = [new TagPointConfiguration { TagId = "t1", Address = "40001" }],
        };

        await scheduler.ApplyDevicesAsync([device]);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(driverFactory.Reads > 20, $"Only {driverFactory.Reads} reads completed.");
        Assert.True(pipeline.Count > 20, $"Only {pipeline.Count} results dispatched.");
        Assert.True(driverFactory.Created > 1, "Transport failures should force driver recreation.");

        var health = await scheduler.GetDeviceHealthAsync();
        Assert.Single(health);
        Assert.True(health[0].IsRunning);

        cts.Cancel();
        await supervising.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopSchedulerAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await pool.DisposeAsync();
    }

    private sealed class CountingPipeline : IDataPipeline
    {
        private long _count;

        public long Count => Volatile.Read(ref _count);

        public ValueTask HandleAsync(DriverResult result, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            return default;
        }

        public async IAsyncEnumerable<DriverResult> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class SoakFactory : IDriverFactory
    {
        private int _created;
        private long _reads;

        public string ProtocolDriverName => "Soak";
        public bool SupportsPush => false;
        public int Created => Volatile.Read(ref _created);
        public long Reads => Interlocked.Read(ref _reads);

        public Task<IProtocolDriver> CreateAsync(DeviceConfiguration device, CancellationToken ct = default)
            => Task.FromResult<IProtocolDriver>(new SoakDriver(this, Interlocked.Increment(ref _created)));

        private sealed class SoakDriver(SoakFactory owner, int id) : IProtocolDriver
        {
            public DriverStatus DriverStatus { get; private set; } = DriverStatus.Connected;

            public async Task ConnectAsync(CancellationToken ct = default)
            {
                await Task.Delay(5, ct);
                DriverStatus = DriverStatus.Connected;
            }

            public Task DisconnectAsync(CancellationToken ct = default)
            {
                DriverStatus = DriverStatus.Disconnected;
                return Task.CompletedTask;
            }

            public async Task<bool> TryReconnectAsync(CancellationToken ct = default)
            {
                await ConnectAsync(ct);
                return true;
            }

            public async Task<DriverResult[]> ReadAsync(
                TagPointConfiguration[] points, CancellationToken ct = default)
            {
                var read = Interlocked.Increment(ref owner._reads);
                await Task.Delay(2, ct);

                // 每 10 次模拟一次传输失败；驱动置 Faulted，池必须丢弃并重建
                if (read % 10 == 0)
                {
                    DriverStatus = DriverStatus.Faulted;
                    throw new IOException($"simulated transport failure on driver {id}");
                }

                return [DriverResult.Good("40001", read)];
            }

            public Task<DriverResult[]> WriteAsync(
                IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
                => Task.FromResult(Array.Empty<DriverResult>());

            public ValueTask DisposeAsync() => default;
        }
    }
}
