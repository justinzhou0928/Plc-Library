using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlcLibrary.Controller.Engine;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General.Configuration;
using TaskScheduler = PlcLibrary.Controller.Engine.TaskScheduler;

namespace PlcLibrary.Tests.Controller;

/// <summary>
/// 推送型设备（SupportsPush=true）在 Connect 失败时的调度语义验证：
/// 该路径不经过轮询采集器的 try/catch，异常会终止 TaskActuator 循环，
/// 必须由调度器监督循环按退避策略重建执行器，且停机不能被该异常破坏。
/// </summary>
public class PushDeviceFailureTests
{
    private static IHost BuildHost() =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPlcLibrary();
                services.Configure<PoolOptions>(o =>
                {
                    o.MaxRetryAttempts = 1;
                    o.RetryDelay = TimeSpan.FromMilliseconds(10);
                    o.OperationTimeout = TimeSpan.FromSeconds(2);
                });
                services.AddDriver<FailingPushDriver>();
            })
            .Build();

    private static void UseFastSupervision(IHost host)
        => host.Services.GetRequiredService<TaskScheduler>().SupervisionInterval = TimeSpan.FromMilliseconds(50);

    private static DeviceConfiguration CreateDevice() => new()
    {
        Id = "push-01",
        Protocol = "FakePush",
        ConnectionString = "host:127.0.0.1",
        CollectionInterval = TimeSpan.FromSeconds(1),
        TagPoints = [new TagPointConfiguration { TagId = "t1", Address = "n1", DataType = "int" }],
    };

    [Fact]
    public async Task PushDevice_ConnectFailure_IsRestartedBySupervisor_AndRecovers()
    {
        // 前 3 次 Connect 失败：第一个驱动耗尽池内重试（2 次）后结束，监督循环重建第二个驱动并成功
        FailingPushDriver.Reset(failuresBeforeSuccess: 3);
        using var host = BuildHost();
        UseFastSupervision(host);
        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            await scheduler.ApplyDevicesAsync([CreateDevice()]);

            await WaitUntilAsync(
                () => Task.FromResult(FailingPushDriver.StartPushingCount >= 1),
                TimeSpan.FromSeconds(10));

            var health = await scheduler.GetDeviceHealthAsync();
            Assert.True(health is [{ IsRunning: true }]);
            Assert.True(FailingPushDriver.Created >= 2,
                $"supervisor 未重建执行器，created={FailingPushDriver.Created}, attempts={FailingPushDriver.ConnectAttempts}");
            Assert.True(FailingPushDriver.ConnectAttempts >= 4,
                $"connect 尝试次数不足，attempts={FailingPushDriver.ConnectAttempts}");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task PushDevice_PermanentConnectFailure_KeepsRetryingWithBackoff()
    {
        FailingPushDriver.Reset(failuresBeforeSuccess: int.MaxValue);
        using var host = BuildHost();
        UseFastSupervision(host);
        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            await scheduler.ApplyDevicesAsync([CreateDevice()]);

            // 重启过程中执行器会短暂从字典移除（Stop→Start），健康状态本身是瞬时的；
            // 这里同时等待「已发生至少 3 次重建」与「稳定处于故障态且带最后错误」，避免与监督循环竞态。
            await WaitUntilAsync(async () =>
            {
                if (FailingPushDriver.Created < 3) return false;
                var health = await scheduler.GetDeviceHealthAsync();
                return health is [{ IsRunning: false, Error: not null }] h
                       && h[0].Error!.Contains("simulated push connect failure", StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(10));

            Assert.True(FailingPushDriver.Created >= 3, $"重试次数不足，created={FailingPushDriver.Created}");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task PushDevice_ConnectFailure_HostShutdownCompletesCleanly()
    {
        FailingPushDriver.Reset(failuresBeforeSuccess: int.MaxValue);
        using var host = BuildHost();
        UseFastSupervision(host);
        await host.StartAsync();

        var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
        await scheduler.ApplyDevicesAsync([CreateDevice()]);

        await WaitUntilAsync(async () =>
        {
            var health = await scheduler.GetDeviceHealthAsync();
            return health is [{ IsRunning: false }];
        }, TimeSpan.FromSeconds(5));

        // 停机：监督循环先取消，再停止执行器；采集异常只记录，不应冒泡
        var error = await Record.ExceptionAsync(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StopAsync(cts.Token);
        });

        Assert.Null(error);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("条件未在超时时间内满足");
            await Task.Delay(20);
        }
    }

    /// <summary>仅用于测试的推送驱动：可配置前 N 次 Connect 失败，StartPushingAsync 永不返回。</summary>
    [ProtocolDriverName("FakePush")]
    internal sealed class FailingPushDriver : IPushProtocolDriver
    {
        private static int _created;
        private static int _connectAttempts;
        private static int _startPushing;
        private static int _failuresBeforeSuccess;

        public FailingPushDriver(ILogger<FailingPushDriver> logger, DeviceConfiguration device)
        {
            Interlocked.Increment(ref _created);
        }

        public static int Created => Volatile.Read(ref _created);

        public static int ConnectAttempts => Volatile.Read(ref _connectAttempts);

        public static int StartPushingCount => Volatile.Read(ref _startPushing);

        public static void Reset(int failuresBeforeSuccess)
        {
            Volatile.Write(ref _created, 0);
            Volatile.Write(ref _connectAttempts, 0);
            Volatile.Write(ref _startPushing, 0);
            Volatile.Write(ref _failuresBeforeSuccess, failuresBeforeSuccess);
        }

        public DriverStatus DriverStatus => DriverStatus.Disconnected;

        public Task ConnectAsync(CancellationToken ct = default)
        {
            var attempt = Interlocked.Increment(ref _connectAttempts);
            if (attempt <= Volatile.Read(ref _failuresBeforeSuccess))
                throw new InvalidOperationException("simulated push connect failure");
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> TryReconnectAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<DriverResult>());

        public Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<DriverResult>());

        public Task StartPushingAsync(
            TagPointConfiguration[] points,
            Func<DriverResult, CancellationToken, ValueTask> onData,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _startPushing);
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task StopPushingAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => default;
    }
}
