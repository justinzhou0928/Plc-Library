using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General.Configuration;
using Polly.Timeout;

namespace PlcLibrary.Tests.DriverPool;

/// <summary>
/// 现场“进程还在但采集停止”的回归：驱动/连接/释放忽略取消时，池必须在有界时间内返回，
/// 且信号量不泄漏、下一次采集能继续。
/// </summary>
public class DriverPoolStabilityTests
{
    private static readonly DeviceConfiguration Device = new()
    {
        Id = "plc-hang",
        Protocol = "Test",
        ConnectionString = "host:127.0.0.1;port:502",
        ConnectionTimeout = TimeSpan.FromMilliseconds(200),
        TagPoints = [new TagPointConfiguration { TagId = "t1", Address = "40001" }]
    };

    [Fact]
    public async Task ReadAsync_DriverIgnoresCancellation_ReturnsByHardTimeout_AndPoolRecovers()
    {
        var hanging = new ScriptedDriver
        {
            ReadHandler = _ => new TaskCompletionSource<DriverResult[]>().Task
        };
        var healthy = new ScriptedDriver();
        var drivers = new Queue<ScriptedDriver>([hanging, healthy]);
        var factory = new TestFactory(() => drivers.Dequeue());
        var pool = CreatePool(factory, operationTimeout: TimeSpan.FromMilliseconds(100));

        // 硬超时 = OperationTimeout + 2s；底层 Task 永远不完成也不响应取消
        await ExpectTimeoutAsync(() => pool.ReadAsync(Device, Device.TagPoints, CancellationToken.None));

        // 关键：槽位已释放、驱动被丢弃，下一次采集能创建新驱动并成功
        var results = await pool.ReadAsync(Device, Device.TagPoints, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(2, factory.CreatedCount);
        await WaitUntilAsync(() => hanging.Disposed, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReadAsync_ConnectIgnoresCancellation_ReturnsByHardTimeout_AndPoolRecovers()
    {
        var hanging = new ScriptedDriver
        {
            DriverStatus = DriverStatus.Disconnected,
            ConnectHandler = _ => new TaskCompletionSource<bool>().Task
        };
        var healthy = new ScriptedDriver();
        var drivers = new Queue<ScriptedDriver>([hanging, healthy]);
        var factory = new TestFactory(() => drivers.Dequeue());
        var pool = CreatePool(factory, operationTimeout: TimeSpan.FromMilliseconds(100));

        await ExpectTimeoutAsync(() => pool.ReadAsync(Device, Device.TagPoints, CancellationToken.None));

        var results = await pool.ReadAsync(Device, Device.TagPoints, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(2, factory.CreatedCount);
        await WaitUntilAsync(() => hanging.Disposed, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Return_HangingDispose_DoesNotLeakSemaphore()
    {
        var hangingDispose = new TaskCompletionSource<bool>();
        var first = new ScriptedDriver
        {
            DriverStatus = DriverStatus.Faulted,
            DisposeHandler = () => new ValueTask(hangingDispose.Task)
        };
        var second = new ScriptedDriver();
        var drivers = new Queue<ScriptedDriver>([first, second]);
        var factory = new TestFactory(() => drivers.Dequeue());
        var pool = CreatePool(factory, operationTimeout: TimeSpan.FromMilliseconds(100));

        // 直接借用/归还是一个 Faulted 驱动；Dispose 永远不完成
        var acquired = await pool.AcquireAsync(Device, CancellationToken.None);
        pool.Return(acquired, Device);

        // Dispose 卡住不应占着信号量：下一次借用必须很快成功
        var next = await pool.AcquireAsync(Device, CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(second, next);
        pool.Return(next, Device);
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task RepeatedTimeoutCycles_DoNotExhaustPool()
    {
        var drivers = new List<ScriptedDriver>();
        var factory = new TestFactory(() =>
        {
            var d = new ScriptedDriver
            {
                ReadHandler = _ => new TaskCompletionSource<DriverResult[]>().Task
            };
            drivers.Add(d);
            return d;
        });
        var pool = CreatePool(factory, operationTimeout: TimeSpan.FromMilliseconds(50));
        pool.HardTimeoutGrace = TimeSpan.FromMilliseconds(50);

        for (var i = 0; i < 10; i++)
        {
            await ExpectTimeoutAsync(() => pool.ReadAsync(Device, Device.TagPoints, CancellationToken.None));
        }

        // 每次超时都必须丢弃旧驱动并释放槽位，第 10 次后仍可创建新驱动
        Assert.Equal(10, factory.CreatedCount);
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task ManyConcurrentCycles_NoDeadlockOrLeak()
    {
        var factory = new TestFactory(() => new ScriptedDriver());
        var pool = CreatePool(factory, operationTimeout: TimeSpan.FromSeconds(1), maxConnections: 2);

        var tasks = Enumerable.Range(0, 16).Select(async _ =>
        {
            for (var i = 0; i < 100; i++)
                await pool.ReadAsync(Device, Device.TagPoints, CancellationToken.None);
        });

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentRecycleAndReturn_DoesNotDeadlockOrLeakSemaphore()
    {
        var factory = new TestFactory(() => new ScriptedDriver());
        var shared = new DeviceSharedPool(
            NullLogger<DeviceSharedPool>.Instance,
            Device,
            factory,
            new PoolOptions
            {
                MaxConnectionsPerDevice = 4,
                MaxRetryAttempts = 1,
                OperationTimeout = TimeSpan.FromSeconds(1),
                PoolIdleTimeout = TimeSpan.Zero,
            });

        for (var i = 0; i < 200; i++)
        {
            var driver = await shared.AcquireAsync(CancellationToken.None);
            var returning = Task.Run(() => shared.Return(driver), CancellationToken.None);
            var recycling = Task.Run(() => shared.TryBeginRecycle(), CancellationToken.None);
            await Task.WhenAll(returning, recycling).WaitAsync(TimeSpan.FromSeconds(2));
            var recycleStarted = await recycling;
            if (recycleStarted)
                shared.CancelRecycle();
        }

        await shared.DisposeAsync();
    }

    private static DeviceDriverPool CreatePool(
        TestFactory factory, TimeSpan operationTimeout, int maxConnections = 1)
        => new(Options.Create(new PoolOptions
        {
            MaxConnectionsPerDevice = maxConnections,
            MaxRetryAttempts = 1,
            RetryDelay = TimeSpan.FromMilliseconds(10),
            OperationTimeout = operationTimeout,
            PoolIdleTimeout = TimeSpan.Zero,
        }), [factory], NullLoggerFactory.Instance);

    private static async Task ExpectTimeoutAsync(Func<Task> action)
    {
        var ex = await Record.ExceptionAsync(action).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(ex);
        Assert.True(ex is TimeoutException or TimeoutRejectedException,
            $"Expected a timeout, but got {ex.GetType().Name}: {ex.Message}");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class TestFactory(Func<ScriptedDriver> create) : IDriverFactory
    {
        private int _created;

        public string ProtocolDriverName => "Test";
        public bool SupportsPush => false;
        public int CreatedCount => Volatile.Read(ref _created);

        public Task<IProtocolDriver> CreateAsync(DeviceConfiguration device, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _created);
            return Task.FromResult<IProtocolDriver>(create());
        }
    }

    private sealed class ScriptedDriver : IProtocolDriver
    {
        public DriverStatus DriverStatus { get; set; } = DriverStatus.Connected;
        public Func<CancellationToken, Task<DriverResult[]>> ReadHandler { get; set; } =
            _ => Task.FromResult(new[] { DriverResult.Good("40001", 42) });
        public Func<CancellationToken, Task> ConnectHandler { get; set; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task<bool>> ReconnectHandler { get; set; } = _ => Task.FromResult(true);
        public Func<CancellationToken, Task> DisconnectHandler { get; set; } = _ => Task.CompletedTask;
        public Func<ValueTask> DisposeHandler { get; set; } = () => default;

        public bool Disposed { get; private set; }

        public Task ConnectAsync(CancellationToken ct = default) => ConnectHandler(ct);
        public Task DisconnectAsync(CancellationToken ct = default) => DisconnectHandler(ct);
        public Task<bool> TryReconnectAsync(CancellationToken ct = default) => ReconnectHandler(ct);
        public Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
            => ReadHandler(ct);
        public Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<DriverResult>());

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return DisposeHandler();
        }
    }
}
