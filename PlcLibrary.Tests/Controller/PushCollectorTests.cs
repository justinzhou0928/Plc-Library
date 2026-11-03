using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlcLibrary.Controller.Collectors;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;

namespace PlcLibrary.Tests.Controller;

/// <summary>
/// 推送采集看门狗的边界：订阅型设备可能长时间没有值变化，
/// 只要订阅仍存活就必须刷新心跳，不能把“无数据”当成“无进展”。
/// </summary>
public class PushCollectorTests
{
    [Fact]
    public async Task ExecuteAsync_NoData_RefreshesHeartbeat()
    {
        var driver = new FakePushDriver();
        var collector = CreateCollector(driver);
        collector.HealthCheckInterval = TimeSpan.FromMilliseconds(50);
        var before = collector.LastActivityUtc;
        using var cts = new CancellationTokenSource();

        var run = collector.ExecuteAsync(cts.Token);
        try
        {
            await Task.Delay(250);

            Assert.True(collector.LastActivityUtc > before,
                $"无数据时心跳未刷新：before={before:O}, after={collector.LastActivityUtc:O}");
            Assert.False(run.IsCompleted);
        }
        finally
        {
            cts.Cancel();
            try
            { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_DriverFaulted_EndsWithError()
    {
        var driver = new FakePushDriver();
        var collector = CreateCollector(driver);
        collector.HealthCheckInterval = TimeSpan.FromMilliseconds(50);
        using var cts = new CancellationTokenSource();

        var run = collector.ExecuteAsync(cts.Token);
        try
        {
            await Task.Delay(80);
            driver.Status = DriverStatus.Faulted;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("Faulted", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            cts.Cancel();
        }
    }

    private static PushCollector CreateCollector(FakePushDriver driver)
        => new(
            driver,
            new DeviceConfiguration
            {
                Id = "push-01",
                Protocol = "FakePush",
                ConnectionString = "host:127.0.0.1",
                TagPoints = [new TagPointConfiguration { TagId = "t1", Address = "n1" }],
            },
            new NoopPipeline(),
            NullLogger<PushCollector>.Instance,
            Options.Create(new PoolOptions
            {
                MaxRetryAttempts = 1,
                RetryDelay = TimeSpan.FromMilliseconds(100),
                OperationTimeout = TimeSpan.FromSeconds(1),
            }),
            NullLoggerFactory.Instance);

    private sealed class NoopPipeline : IDataPipeline
    {
        public ValueTask HandleAsync(DriverResult result, CancellationToken ct) => default;

        public async IAsyncEnumerable<DriverResult> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakePushDriver : IPushProtocolDriver
    {
        public DriverStatus Status { get; set; } = DriverStatus.Disconnected;

        public DriverStatus DriverStatus => Status;

        public Task ConnectAsync(CancellationToken ct = default)
        {
            Status = DriverStatus.Connected;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            Status = DriverStatus.Disconnected;
            return Task.CompletedTask;
        }

        public Task<bool> TryReconnectAsync(CancellationToken ct = default)
        {
            Status = DriverStatus.Connected;
            return Task.FromResult(true);
        }

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
