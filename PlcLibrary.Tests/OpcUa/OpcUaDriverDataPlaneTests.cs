using Microsoft.Extensions.Logging.Abstractions;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.General.Configuration;
using PlcLibrary.OpcUa;
using PlcLibrary.Tests.Simulators;
using System.Collections.Concurrent;

namespace PlcLibrary.Tests.OpcUa;

/// <summary>
/// OPC UA 数据面端到端验证：本机起真实 OPC UA 服务端（标准节点空间），
/// 覆盖会话建立、批量 Read、Write（只读节点 → Bad）、订阅推送回调。
/// </summary>
public class OpcUaDriverDataPlaneTests
{
    private static string PkiRoot() => Path.Combine(Path.GetTempPath(), "plc-ua-client", Guid.NewGuid().ToString("N"));

    private static DeviceConfiguration CreateDevice(int port) => new()
    {
        Id = "ua-01",
        Protocol = "OPC_UA",
        ConnectionString =
            $"endpoint:opc.tcp://127.0.0.1:{port};security:None;timeout:10000;sessiontimeout:60000;autoacceptcertificate:true;" +
            $"pkiownpath:{Path.Combine(PkiRoot(), "own")};" +
            $"pkitrustedpath:{Path.Combine(PkiRoot(), "trusted")};" +
            $"pkiissuerpath:{Path.Combine(PkiRoot(), "issuer")};" +
            $"pkirejectedpath:{Path.Combine(PkiRoot(), "rejected")}",
    };

    private static TagPointConfiguration Point(string address, string dataType = "")
        => new() { TagId = address, Address = address, DataType = dataType };

    [Fact]
    public async Task Read_StandardNodes_ReturnsGoodValues_AndUnknownNodeIsBad()
    {
        await using var server = new OpcUaTestServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(cts.Token);

        var driver = new OpcUaDriver(NullLogger<OpcUaDriver>.Instance, CreateDevice(server.Port));
        await driver.ConnectAsync(cts.Token);
        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);

        try
        {
            var results = await driver.ReadAsync(
            [
                Point(OpcUaTestServer.ServerStatusNodeId.ToString()),
                Point(OpcUaTestServer.CurrentTimeNodeId.ToString()),
                Point("ns=2;s=no-such-node"),
            ], cts.Token);

            Assert.Equal(3, results.Length);
            Assert.Equal(QualityCode.Good, results[0].Status);
            Assert.NotNull(results[0].Value);
            Assert.Equal(QualityCode.Good, results[1].Status);
            Assert.IsType<DateTime>(results[1].Value);
            Assert.Equal(QualityCode.BadCommFailure, results[2].Status);
        }
        finally
        {
            await driver.DisposeAsync();
        }
    }

    [Fact]
    public async Task Read_InvalidNodeIdFormat_ReturnsBadConfigError()
    {
        await using var server = new OpcUaTestServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(cts.Token);

        var driver = new OpcUaDriver(NullLogger<OpcUaDriver>.Instance, CreateDevice(server.Port));
        await driver.ConnectAsync(cts.Token);

        try
        {
            var results = await driver.ReadAsync([Point("not a node id")], cts.Token);

            Assert.Equal(QualityCode.BadConfigError, Assert.Single(results).Status);
        }
        finally
        {
            await driver.DisposeAsync();
        }
    }

    [Fact]
    public async Task Write_ReadOnlyStandardNode_ReturnsBadQuality()
    {
        await using var server = new OpcUaTestServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(cts.Token);

        var driver = new OpcUaDriver(NullLogger<OpcUaDriver>.Instance, CreateDevice(server.Port));
        await driver.ConnectAsync(cts.Token);

        try
        {
            var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
            {
                [Point(OpcUaTestServer.ServerStatusNodeId.ToString())] = 123,
            }, cts.Token);

            Assert.Equal(QualityCode.BadCommFailure, Assert.Single(results).Status);
            Assert.False(string.IsNullOrWhiteSpace(results[0].ErrorMessage));
        }
        finally
        {
            await driver.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartPushing_ReceivesCurrentTimeNotifications()
    {
        await using var server = new OpcUaTestServer();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await server.StartAsync(lifetime.Token);

        var driver = new OpcUaDriver(NullLogger<OpcUaDriver>.Instance, CreateDevice(server.Port));
        await driver.ConnectAsync(lifetime.Token);

        var received = new ConcurrentQueue<DateTime>();
        using var pushCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var pushTask = driver.StartPushingAsync(
            [Point(OpcUaTestServer.CurrentTimeNodeId.ToString())],
            (result, _) =>
            {
                if (result.Status == QualityCode.Good && result.Value is DateTime time)
                {
                    received.Enqueue(time);
                    if (received.Count >= 2) pushCts.Cancel();
                }
                return ValueTask.CompletedTask;
            },
            pushCts.Token);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (received.Count < 2 && DateTime.UtcNow < deadline && !pushTask.IsCompleted)
                await Task.Delay(50);

            Assert.True(received.Count >= 2, $"未收到足够的订阅通知，实际 {received.Count}");
            try { await pushTask; } catch (OperationCanceledException) { }
        }
        finally
        {
            await driver.DisposeAsync();
        }
    }
}
