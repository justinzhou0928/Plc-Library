using Microsoft.Extensions.Logging.Abstractions;
using PlcLibrary.AllenBradley;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.General.Configuration;
using PlcLibrary.Tests.Simulators;

namespace PlcLibrary.Tests.AllenBradley;

public sealed class AllenBradleyDriverSimulatorTests
{
    [Fact]
    public async Task ConnectAsync_RegistersSessionWithoutDiscovery()
    {
        await using var sim = new EtherNetIpSimServer();
        await sim.StartAsync();
        await using var driver = CreateDriver(sim.Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await driver.ConnectAsync(cts.Token);

        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);
        Assert.Equal(0x0065, sim.ReceivedCommands[0]); // RegisterSession is the first frame
        Assert.DoesNotContain((ushort)0x0063, sim.ReceivedCommands); // no ListIdentity
        Assert.DoesNotContain((ushort)0x0064, sim.ReceivedCommands); // no ListServices

        await driver.DisconnectAsync();
        Assert.Equal(DriverStatus.Disconnected, driver.DriverStatus);
    }

    /// <summary>
    /// 回归测试：<c>TagClient.ReadMultipleAsync</c> 返回的每个嵌入 Read Tag 应答以 2 字节 CIP 数据类型码开头
    /// （DINT=C4 00、REAL=CA 00、BOOL=C1 00）。驱动必须跳过并校验类型码，否则 DINT/REAL 整体错位、
    /// BOOL 恒为 true，而且质量仍是 Good（静默数据污染）。
    /// </summary>
    [Fact]
    public async Task ReadAsync_NumericBatch_DecodesCipTypeCodeAndReturnsCorrectValues()
    {
        await using var sim = new EtherNetIpSimServer();
        sim.AddInt32("DINT_TAG", 123456);
        sim.AddSingle("REAL_TAG", 3.5f);
        sim.AddBool("BOOL_TAG", false);
        await sim.StartAsync();
        await using var driver = CreateDriver(sim.Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await driver.ConnectAsync(cts.Token);

        var results = await driver.ReadAsync(
        [
            Point("DINT_TAG", "int32"),
            Point("REAL_TAG", "float"),
            Point("BOOL_TAG", "bool"),
        ], cts.Token);

        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));

        // DINT 123456 = 0x0001E240 -> 应答数据 C4 00 40 E2 01 00，跳过类型码后解码
        Assert.Equal(123456, Assert.IsType<int>(results[0].Value));
        // REAL 3.5 = 0x40600000 -> 应答数据 CA 00 00 00 60 40
        Assert.Equal(3.5f, Assert.IsType<float>(results[1].Value));
        // BOOL false -> 应答数据 C1 00 00
        Assert.False(Assert.IsType<bool>(results[2].Value));

        Assert.Contains(sim.Requests, r => r.Service == 0x0A && !r.InsideMultiService);
    }

    [Fact]
    public async Task ReadAsync_BatchDataTypeMismatch_ReturnsBadQualityInsteadOfGarbage()
    {
        await using var sim = new EtherNetIpSimServer();
        sim.AddSingle("REAL_TAG", 3.5f);
        await sim.StartAsync();
        await using var driver = CreateDriver(sim.Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await driver.ConnectAsync(cts.Token);

        // 配置成 int32、实际是 REAL：类型码不匹配时必须显式失败，不能解出 Good 垃圾值
        var results = await driver.ReadAsync([Point("REAL_TAG", "int32")], cts.Token);

        Assert.Equal(QualityCode.BadCommFailure, results[0].Status);
        Assert.Null(results[0].Value);
    }

    [Fact]
    public async Task ReadAsync_PerPointFallback_ReturnsCorrectValuesInInputOrder()
    {
        await using var sim = new EtherNetIpSimServer(rejectMultiService: true);
        sim.AddInt32("DINT_TAG", 123456);
        sim.AddSingle("REAL_TAG", 3.5f);
        sim.AddBool("BOOL_TAG", true);
        await sim.StartAsync();
        await using var driver = CreateDriver(sim.Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await driver.ConnectAsync(cts.Token);

        var points = new[]
        {
            Point("BOOL_TAG", "bool"),
            Point("DINT_TAG", "int32"),
            Point("REAL_TAG", "float"),
        };
        var results = await driver.ReadAsync(points, cts.Token);

        Assert.Equal(points.Length, results.Length);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal("BOOL_TAG", results[0].Address);
        Assert.Equal("DINT_TAG", results[1].Address);
        Assert.Equal("REAL_TAG", results[2].Address);
        Assert.True(Assert.IsType<bool>(results[0].Value));
        Assert.Equal(123456, Assert.IsType<int>(results[1].Value));
        Assert.Equal(3.5f, Assert.IsType<float>(results[2].Value));

        // The batch attempt failed, then one Read Tag per point was sent.
        Assert.Contains(sim.Requests, r => r.Service == 0x0A && !r.InsideMultiService);
        Assert.Equal(3, sim.Requests.Count(r => r.Service == 0x4C && !r.InsideMultiService));
        Assert.Equal(DriverStatus.Connected, driver.DriverStatus); // non-transport failure must not fault the driver
    }

    [Fact]
    public async Task ReadAsync_WithUnknownTag_IsolatesErrorToThatPointWithoutFallback()
    {
        await using var sim = new EtherNetIpSimServer();
        sim.AddInt32("DINT_TAG", 123456);
        sim.AddSingle("REAL_TAG", 3.5f);
        sim.AddBool("BOOL_TAG", true);
        await sim.StartAsync();
        await using var driver = CreateDriver(sim.Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await driver.ConnectAsync(cts.Token);

        var results = await driver.ReadAsync(
        [
            Point("DINT_TAG", "int32"),
            Point("MISSING_TAG", "int32"),
            Point("REAL_TAG", "float"),
        ], cts.Token);

        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(QualityCode.BadCommFailure, results[1].Status);
        Assert.Equal(QualityCode.Good, results[2].Status);
        Assert.Equal("MISSING_TAG", results[1].Address);
        Assert.False(string.IsNullOrEmpty(results[1].ErrorMessage));

        // The per-tag failure came back inside the Multiple Service Packet reply, so the
        // driver did not need to enter the per-point fallback path.
        Assert.Contains(sim.Requests, r => r.Service == 0x0A && !r.InsideMultiService);
        Assert.DoesNotContain(sim.Requests, r => r.Service == 0x4C && !r.InsideMultiService);
    }

    [Fact]
    public async Task ReadAsync_WithUnknownTag_WhenBatchRejected_FallsBackAndIsolates()
    {
        await using var sim = new EtherNetIpSimServer(rejectMultiService: true);
        sim.AddInt32("DINT_TAG", 123456);
        sim.AddSingle("REAL_TAG", 3.5f);
        await sim.StartAsync();
        await using var driver = CreateDriver(sim.Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await driver.ConnectAsync(cts.Token);

        var results = await driver.ReadAsync(
        [
            Point("DINT_TAG", "int32"),
            Point("MISSING_TAG", "int32"),
            Point("REAL_TAG", "float"),
        ], cts.Token);

        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(123456, Assert.IsType<int>(results[0].Value));
        Assert.Equal(QualityCode.BadCommFailure, results[1].Status);
        Assert.Equal("MISSING_TAG", results[1].Address);
        Assert.Equal(QualityCode.Good, results[2].Status);
        Assert.Equal(3.5f, Assert.IsType<float>(results[2].Value));
        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);

        // Top-level batch failure (0x08) followed by one Read Tag per point; the missing
        // tag is rejected with a per-point error while the other two still succeed.
        Assert.Contains(sim.Requests, r => r.Service == 0x0A && !r.InsideMultiService);
        Assert.Equal(3, sim.Requests.Count(r => r.Service == 0x4C && !r.InsideMultiService));
    }

    [Fact]
    public async Task WriteAsync_Int32AndFloat_ReachSimulatorAndReadBack()
    {
        await using var sim = new EtherNetIpSimServer(rejectMultiService: true);
        sim.AddInt32("DINT_TAG", 0);
        sim.AddSingle("REAL_TAG", 0);
        await sim.StartAsync();
        await using var driver = CreateDriver(sim.Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await driver.ConnectAsync(cts.Token);

        var dintPoint = Point("DINT_TAG", "int32");
        var realPoint = Point("REAL_TAG", "float");
        var writeResults = await driver.WriteAsync(
            new Dictionary<TagPointConfiguration, object>
            {
                [dintPoint] = 777,
                [realPoint] = 2.25f,
            }, cts.Token);

        Assert.Equal(2, writeResults.Length);
        Assert.All(writeResults, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal(777, sim.GetInt32("DINT_TAG"));
        Assert.Equal(2.25f, sim.GetSingle("REAL_TAG"));
        Assert.Contains(sim.Requests, r => r.Service == 0x4D && r.TagName == "DINT_TAG");
        Assert.Contains(sim.Requests, r => r.Service == 0x4D && r.TagName == "REAL_TAG");

        // Read back through the per-point path so the batch type-code defect does not
        // obscure the values that the Write Tag requests actually delivered.
        var readBack = await driver.ReadAsync([dintPoint, realPoint], cts.Token);
        Assert.All(readBack, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal(777, Assert.IsType<int>(readBack[0].Value));
        Assert.Equal(2.25f, Assert.IsType<float>(readBack[1].Value));
    }

    [Fact]
    public async Task ReadAsync_AfterDisconnect_ThrowsInvalidOperationException()
    {
        await using var sim = new EtherNetIpSimServer();
        sim.AddInt32("DINT_TAG", 1);
        await sim.StartAsync();
        await using var driver = CreateDriver(sim.Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await driver.ConnectAsync(cts.Token);
        await driver.DisconnectAsync(cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.ReadAsync([Point("DINT_TAG", "int32")], cts.Token));
    }

    [Fact]
    public async Task ReadAsync_String_ReturnsDecodedString()
    {
        await using var sim = new EtherNetIpSimServer();
        sim.AddString("STRING_TAG", "HELLO");
        await sim.StartAsync();
        await using var driver = CreateDriver(sim.Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await driver.ConnectAsync(cts.Token);

        var results = await driver.ReadAsync([Point("STRING_TAG", "string")], cts.Token);

        Assert.Single(results);
        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal("HELLO", Assert.IsType<string>(results[0].Value));
        Assert.Contains(sim.Requests, r => r.Service == 0x4C && r.TagName == "STRING_TAG");
    }

    [Fact]
    public async Task ConnectedMode_UsesForwardOpenAndSendUnitData()
    {
        await using var sim = new EtherNetIpSimServer(rejectMultiService: true);
        sim.AddInt32("DINT_TAG", 111);
        await sim.StartAsync();
        await using var driver = CreateDriver(sim.Port, useConnected: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await driver.ConnectAsync(cts.Token);
        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);
        Assert.True(sim.SawService(0x54)); // Forward_Open

        var dintPoint = Point("DINT_TAG", "int32");
        var write = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object> { [dintPoint] = 42 }, cts.Token);
        Assert.Equal(QualityCode.Good, write[0].Status);
        Assert.Equal(42, sim.GetInt32("DINT_TAG"));

        var read = await driver.ReadAsync([dintPoint], cts.Token);
        Assert.Equal(QualityCode.Good, read[0].Status);
        Assert.Equal(42, Assert.IsType<int>(read[0].Value));
        Assert.True(sim.SawInboundCommand(0x0070)); // SendUnitData is the connected wire path

        await driver.DisposeAsync();
        Assert.True(sim.SawService(0x4E)); // Forward_Close on dispose
    }

    [Fact]
    public async Task ReadAsync_PeerStopsResponding_TimesOutWithBadQualityAndFaults()
    {
        await using var sim = new EtherNetIpSimServer();
        sim.AddInt32("DINT_TAG", 123456);
        await sim.StartAsync();
        await using var driver = CreateDriver(sim.Port, timeoutMs: 300);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await driver.ConnectAsync(cts.Token);

        sim.PauseResponses = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await driver.ReadAsync([Point("DINT_TAG", "int32")], cts.Token);
        sw.Stop();

        Assert.Equal(QualityCode.BadCommFailure, results[0].Status);
        Assert.Equal(DriverStatus.Faulted, driver.DriverStatus);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"timeout 未生效: {sw.Elapsed}");
    }

    [Fact]
    public async Task ReadAsync_UnknownDataType_ReturnsBadConfigErrorWithoutWireRequest()
    {
        await using var sim = new EtherNetIpSimServer();
        sim.AddInt32("DINT_TAG", 123456);
        await sim.StartAsync();
        await using var driver = CreateDriver(sim.Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await driver.ConnectAsync(cts.Token);
        var before = sim.Requests.Count;

        var results = await driver.ReadAsync([Point("DINT_TAG", "decimal")], cts.Token);

        Assert.Equal(QualityCode.BadConfigError, results[0].Status);
        Assert.Contains("unsupported DataType", results[0].ErrorMessage);
        Assert.Equal(before, sim.Requests.Count);
    }

    private static AllenBradleyDriver CreateDriver(int port, bool useConnected = false, int timeoutMs = 5000)
        => new(NullLogger<AllenBradleyDriver>.Instance, new DeviceConfiguration
        {
            Id = "sim",
            Protocol = "AllenBradley",
            ConnectionString = $"host:127.0.0.1;port:{port};path:;timeout:{timeoutMs};useconnected:{(useConnected ? "true" : "false")}",
        });

    private static TagPointConfiguration Point(string address, string dataType)
        => new() { TagId = address, Address = address, DataType = dataType };
}
