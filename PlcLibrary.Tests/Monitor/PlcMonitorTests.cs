using Microsoft.Extensions.Options;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.Monitor.Engine;
using PlcLibrary.Monitor.Models;

namespace PlcLibrary.Tests.Monitor;

public class PlcMonitorTests
{
    private static PlcMonitor CreateMonitor(TimeSpan? idleTimeout = null)
    {
        var options = Options.Create(new MonitorOptions
        {
            // 默认禁用空闲清理，保证测试确定性（个别用例显式开启）
            EntryIdleTimeout = idleTimeout ?? TimeSpan.Zero,
        });
        return new PlcMonitor(options);
    }

    private static DriverResult Good(string deviceId, string tagId, string address, object? value)
        => DriverResult.Good(address, value) with { DeviceId = deviceId, TagId = tagId };

    private static DriverResult Bad(string deviceId, string tagId, string address, QualityCode status)
        => DriverResult.Bad(address, status, "error") with { DeviceId = deviceId, TagId = tagId };

    private static async Task FeedAsync(PlcMonitor monitor, params DriverResult[] results)
    {
        foreach (var r in results)
            await monitor.HandleAsync(r, CancellationToken.None);
    }

    private static async Task<List<DriverResult>> DrainAsync(IAsyncEnumerator<DriverResult> e)
    {
        var list = new List<DriverResult>();
        while (await e.MoveNextAsync())
            list.Add(e.Current);
        return list;
    }

    [Fact]
    public async Task Get_ReturnsCachedValue()
    {
        var monitor = CreateMonitor();
        await FeedAsync(monitor, Good("d1", "t1", "40001", 42));

        var result = monitor.Get("d1", "t1");
        Assert.NotNull(result);
        Assert.Equal(42, (int)result.Value.Value!);
        Assert.Equal("40001", result.Value.Address);
    }

    [Fact]
    public void Get_ReturnsNull_ForUnknownPoint()
    {
        var monitor = CreateMonitor();
        Assert.Null(monitor.Get("d1", "missing"));
    }

    [Fact]
    public async Task GetDevice_ReturnsOnlyThatDevice()
    {
        var monitor = CreateMonitor();
        await FeedAsync(monitor,
            Good("d1", "t1", "40001", 1),
            Good("d1", "t2", "40002", 2),
            Good("d2", "t1", "40001", 999));

        var snapshot = monitor.GetDevice("d1");
        Assert.Equal(2, snapshot.Count);
        Assert.All(snapshot, r => Assert.Equal("d1", r.DeviceId));
        Assert.Contains(snapshot, r => r.TagId == "t1");
        Assert.Contains(snapshot, r => r.TagId == "t2");
    }

    [Fact]
    public async Task SameTagAcrossDevices_AreIsolated()
    {
        var monitor = CreateMonitor();
        await FeedAsync(monitor,
            Good("d1", "t1", "DB1.DBD0", 1),
            Good("d2", "t1", "DB1.DBD0", 99));

        Assert.Equal(1, (int)monitor.Get("d1", "t1")!.Value.Value!);
        Assert.Equal(99, (int)monitor.Get("d2", "t1")!.Value.Value!);
        Assert.Equal("d2", monitor.Get("d2", "t1")!.Value.DeviceId);
    }

    [Fact]
    public async Task SubscribeAsync_YieldsCurrentSnapshotFirst()
    {
        var monitor = CreateMonitor();
        await FeedAsync(monitor, Good("d1", "t1", "40001", 42));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = monitor.SubscribeAsync("d1", "t1", cts.Token).GetAsyncEnumerator();
        try
        {
            // 订阅时已存在缓存 → 首个元素应为当前快照，而非等待下一次变化
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(42, (int)enumerator.Current.Value!);

            monitor.DisposeResources();
            Assert.Empty(await DrainAsync(enumerator));
        }
        finally
        {
            cts.Cancel();
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task SubscribeAsync_OnlyYieldsOnChange()
    {
        var monitor = CreateMonitor();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = monitor.SubscribeAsync("d1", "t1", cts.Token).GetAsyncEnumerator();
        try
        {
            // 变化（从无到有）
            await FeedAsync(monitor, Good("d1", "t1", "40001", 42));
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(42, (int)enumerator.Current.Value!);

            // 不变：喂两次相同值，不应产生新元素
            await FeedAsync(monitor,
                Good("d1", "t1", "40001", 42),
                Good("d1", "t1", "40001", 42));

            // 变化：43
            await FeedAsync(monitor, Good("d1", "t1", "40001", 43));
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(43, (int)enumerator.Current.Value!);

            // 收尾排空：上述「不变」不应遗留任何元素
            monitor.DisposeResources();
            Assert.Empty(await DrainAsync(enumerator));
        }
        finally
        {
            cts.Cancel();
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task SubscribeAsync_QualityChange_IsPublished()
    {
        var monitor = CreateMonitor();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = monitor.SubscribeAsync("d1", "t1", cts.Token).GetAsyncEnumerator();
        try
        {
            await FeedAsync(monitor, Good("d1", "t1", "40001", 42));
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(QualityCode.Good, enumerator.Current.Status);

            // 值可能未变，但质量状态变化也应推送
            await FeedAsync(monitor, Bad("d1", "t1", "40001", QualityCode.BadCommFailure));
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(QualityCode.BadCommFailure, enumerator.Current.Status);

            monitor.DisposeResources();
            Assert.Empty(await DrainAsync(enumerator));
        }
        finally
        {
            cts.Cancel();
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task SubscribeDeviceAsync_YieldsChangesAcrossTags()
    {
        var monitor = CreateMonitor();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = monitor.SubscribeDeviceAsync("d1", cts.Token).GetAsyncEnumerator();
        try
        {
            await FeedAsync(monitor, Good("d1", "t1", "40001", 1));
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("t1", enumerator.Current.TagId);

            await FeedAsync(monitor, Good("d1", "t2", "40002", 2));
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("t2", enumerator.Current.TagId);

            // 其他设备不流入 + 未变不产生
            await FeedAsync(monitor,
                Good("d2", "t1", "40001", 999),
                Good("d1", "t1", "40001", 1));

            monitor.DisposeResources();
            Assert.Empty(await DrainAsync(enumerator));
        }
        finally
        {
            cts.Cancel();
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task EvictStaleEntries_RemovesIdleEntry()
    {
        var monitor = CreateMonitor(idleTimeout: TimeSpan.FromMilliseconds(1));
        await FeedAsync(monitor, Good("d1", "t1", "40001", 42));

        Assert.NotNull(monitor.Get("d1", "t1"));

        await Task.Delay(50);
        monitor.EvictStaleEntries();

        Assert.Null(monitor.Get("d1", "t1"));
    }

    [Fact]
    public async Task EvictStaleEntries_KeepsRecentlyUpdatedEntry()
    {
        var monitor = CreateMonitor(idleTimeout: TimeSpan.FromMinutes(1));
        await FeedAsync(monitor, Good("d1", "t1", "40001", 42));

        monitor.EvictStaleEntries();

        Assert.NotNull(monitor.Get("d1", "t1"));
    }

    [Fact]
    public async Task ConcurrentHandleAsync_SameValue_NotifiesOnce()
    {
        var monitor = CreateMonitor();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = monitor.SubscribeAsync("d1", "t1", cts.Token).GetAsyncEnumerator();
        try
        {
            // 并发写入相同的值：锁保证去重后只通知一次
            await Task.WhenAll(Enumerable.Range(0, 1000)
                .Select(_ => Task.Run(() => monitor.HandleAsync(Good("d1", "t1", "40001", 42), CancellationToken.None).AsTask())));

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(42, (int)enumerator.Current.Value!);

            monitor.DisposeResources();
            Assert.Empty(await DrainAsync(enumerator));
        }
        finally
        {
            cts.Cancel();
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task SubscribeAsync_Cancellation_Propagates()
    {
        var monitor = CreateMonitor();
        using var cts = new CancellationTokenSource();
        var enumerator = monitor.SubscribeAsync("d1", "t1", cts.Token).GetAsyncEnumerator();

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();
    }
}
