using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using PlcLibrary.Mitsubishi;
using PlcLibrary.Tests.Simulators;

namespace PlcLibrary.Tests.Mitsubishi;

/// <summary>
/// 离线端到端验证：本机起最小化 MELSEC MC 3E 二进制帧从站（<see cref="McProtocolSimServer"/>），
/// 用真实 <see cref="MitsubishiDriver"/> 走 TCP 读写，覆盖连接、批量读写、错误码与响应键匹配。
/// </summary>
public class MitsubishiDriverSimulatorTests
{
    [Fact]
    public async Task ConnectAsync_Simulator_BecomesConnected_WithoutAnyHandshakeFrame()
    {
        await using var sim = new McProtocolSimServer();
        await using var driver = CreateDriver(sim.Port);

        await driver.ConnectAsync();

        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);
        // Snet 的 OnAsync 只建立 TCP 连接，不发送任何 MC 握手/探测帧。
        Assert.Equal(0, sim.RequestCount);
    }

    [Fact]
    public async Task ConnectAsync_ClosedPort_ThrowsInvalidOperationExceptionAndFaults()
    {
        var port = ReserveClosedPort();
        await using var driver = CreateDriver(port, timeoutMs: 300);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ConnectAsync());

        Assert.Contains("Mitsubishi connection failed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(DriverStatus.Faulted, driver.DriverStatus);
    }

    [Fact]
    public async Task ConnectAsync_UnknownProtocolType_ThrowsInvalidOperationExceptionListingSupportedValues()
    {
        var device = new DeviceConfiguration
        {
            Id = "mc-sim",
            Protocol = "Mitsubishi",
            // 未知协议类型在 CreateBasics 中就地失败，不会真正建立连接。
            ConnectionString = "host:127.0.0.1;port:1;protocoltype:MODBUS;timeout:300",
        };
        await using var driver = new MitsubishiDriver(NullLogger<MitsubishiDriver>.Instance, device);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ConnectAsync());

        Assert.Contains("Unknown Mitsubishi protocol type 'MODBUS'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MC / A1E / A3C / FX", ex.Message, StringComparison.Ordinal);
        Assert.Equal(DriverStatus.Faulted, driver.DriverStatus);
    }

    [Fact]
    public async Task ConnectAsync_UnreachableHost_DoesNotHangBeyondConfiguredTimeout()
    {
        var device = new DeviceConfiguration
        {
            Id = "mc-unreachable",
            Protocol = "Mitsubishi",
            // TEST-NET-1，通常不可路由；connect 应受 timeout:500 约束而不是等系统默认超时。
            ConnectionString = "host:192.0.2.1;port:9;protocoltype:MC;timeout:500",
        };
        await using var driver = new MitsubishiDriver(NullLogger<MitsubishiDriver>.Instance, device);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ConnectAsync());
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Connect took {sw.Elapsed}, expected < 5s");
        Assert.Equal(DriverStatus.Faulted, driver.DriverStatus);
    }

    [Fact]
    public async Task ReadAsync_D100D101D102M110_ReturnsConfiguredClrTypesInInputOrder()
    {
        await using var sim = new McProtocolSimServer();
        sim.SetWord(100, 1234);
        sim.SetWord(101, 65000);
        SetFloat(sim, 102, 12.5f);
        sim.SetBit(110, true);
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(
        [
            Point("p1", "D100", "short"),
            Point("p2", "D101", "ushort"),
            Point("p3", "D102", "float"),
            Point("p4", "M110", "bool"),
        ]);

        Assert.Equal(4, results.Length);
        Assert.Equal(new[] { "D100", "D101", "D102", "M110" }, results.Select(r => r.Address));
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));

        // Snet 返回的 ResultValue 是 CLR 原生类型，与配置的 DataType 一一对应。
        Assert.Equal((short)1234, Assert.IsType<short>(results[0].Value));
        Assert.Equal((ushort)65000, Assert.IsType<ushort>(results[1].Value));
        Assert.Equal(12.5f, Assert.IsType<float>(results[2].Value));
        Assert.True(Assert.IsType<bool>(results[3].Value));

        // 每个点位独立一帧请求：float 点位在一帧内请求 2 个字。
        Assert.Equal(4, sim.RequestCount);
        Assert.Equal(4, sim.ReadRequestCount);
        Assert.Equal("500000FFFF00000C000A0001040000640000A80100", sim.RequestFrames[0]);
        Assert.Equal("500000FFFF00000C000A0001040000660000A80200", sim.RequestFrames[2]);
        Assert.Equal("500000FFFF00000C000A00010401006E0000900100", sim.RequestFrames[3]);
    }

    [Fact]
    public async Task ReadAsync_ContiguousPoints_MergeIntoSingleRequest_AndKeepInputOrder()
    {
        await using var sim = new McProtocolSimServer();
        sim.SetWord(100, 111);
        sim.SetWord(200, 222);
        sim.SetWord(101, 333);
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(
        [
            Point("a", "D100", "short"),
            Point("b", "D200", "short"),
            Point("c", "D101", "short"),
        ]);

        // D100+D101 连续 → 合并为一次 Int16Array 读；D200 独立一次：共 2 次请求（逐点实现为 3 次）。
        Assert.Equal(2, sim.RequestCount);
        Assert.Equal(2, sim.ReadRequestCount);
        Assert.Equal(0, sim.WriteRequestCount);
        Assert.Equal(
            new[]
            {
                "500000FFFF00000C000A0001040000640000A80200",
                "500000FFFF00000C000A0001040000C80000A80100",
            },
            sim.RequestFrames);
        Assert.Equal(new[] { "D100", "D200", "D101" }, results.Select(r => r.Address));
        Assert.Equal((short)111, Assert.IsType<short>(results[0].Value));
        Assert.Equal((short)222, Assert.IsType<short>(results[1].Value));
        Assert.Equal((short)333, Assert.IsType<short>(results[2].Value));
    }

    [Fact]
    public async Task ReadAsync_FiveContiguousShorts_SingleRequest()
    {
        await using var sim = new McProtocolSimServer();
        for (ushort i = 0; i < 5; i++)
            sim.SetWord((ushort)(100 + i), (ushort)(10 + i));
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(
        [
            Point("p0", "D100", "short"),
            Point("p1", "D101", "short"),
            Point("p2", "D102", "short"),
            Point("p3", "D103", "short"),
            Point("p4", "D104", "short"),
        ]);

        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal([10, 11, 12, 13, 14], results.Select(r => (int)Assert.IsType<short>(r.Value)));
        Assert.Equal(1, sim.RequestCount);
        Assert.Equal("500000FFFF00000C000A0001040000640000A80500", sim.RequestFrames[0]);
    }

    [Fact]
    public async Task ReadAsync_ThreeContiguousFloats_SingleRequest()
    {
        await using var sim = new McProtocolSimServer();
        SetFloat(sim, 200, 1.5f);
        SetFloat(sim, 202, -2.25f);
        SetFloat(sim, 204, 3.75f);
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(
        [
            Point("f0", "D200", "float"),
            Point("f1", "D202", "float"),
            Point("f2", "D204", "float"),
        ]);

        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal(1.5f, Assert.IsType<float>(results[0].Value));
        Assert.Equal(-2.25f, Assert.IsType<float>(results[1].Value));
        Assert.Equal(3.75f, Assert.IsType<float>(results[2].Value));
        Assert.Equal(1, sim.RequestCount);
        // FloatArray 两点起一次读取：head=D200、2 字 × 3 = 6 字
        Assert.Equal("500000FFFF00000C000A0001040000C80000A80600", sim.RequestFrames[0]);
    }

    [Fact]
    public async Task WriteAsync_D200_Int16_UpdatesSimulatorMemoryAndReadsBack()
    {
        await using var sim = new McProtocolSimServer();
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();
        var point = Point("w1", "D200", "short");

        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object> { [point] = (short)4321 });

        Assert.Single(results);
        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Null(results[0].Value);
        Assert.Equal(4321, sim.GetWord(200));
        Assert.Equal(1, sim.WriteRequestCount);
        Assert.Equal("500000FFFF00000E000A0001140000C80000A80100E110", sim.RequestFrames[0]);

        var readBack = await driver.ReadAsync([point]);
        Assert.Equal(QualityCode.Good, readBack[0].Status);
        Assert.Equal((short)4321, Assert.IsType<short>(readBack[0].Value));
    }

    [Fact]
    public async Task WriteAsync_M200_Bool_PacksBitAsHighNibble()
    {
        await using var sim = new McProtocolSimServer();
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();
        var point = Point("w2", "M200", "bool");

        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object> { [point] = true });

        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.True(sim.GetBit(200));
        // 位写：1 点 true 打包为 0x10（首点在高 4 位）。
        Assert.Equal("500000FFFF00000D000A0001140100C8000090010010", sim.RequestFrames[0]);

        var readBack = await driver.ReadAsync([point]);
        Assert.Equal(QualityCode.Good, readBack[0].Status);
        Assert.True(Assert.IsType<bool>(readBack[0].Value));
    }

    [Fact]
    public async Task WriteAsync_DuplicateAddressInBatch_RejectsSecondPointWithBadConfigError()
    {
        await using var sim = new McProtocolSimServer();
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var first = Point("dup-a", "D200", "short");
        var second = Point("dup-b", "D200", "short");
        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [first] = (short)1,
            [second] = (short)2,
        });

        Assert.Equal(2, results.Length);
        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(QualityCode.BadConfigError, results[1].Status);
        Assert.Equal("Duplicate address in write batch", results[1].ErrorMessage);
        Assert.Equal(1, sim.GetWord(200));
        // 重复地址不进入批量写：只发了一帧。
        Assert.Equal(1, sim.WriteRequestCount);
    }

    [Fact]
    public async Task ReadAsync_DeviceErrors_ReturnBadCommFailureForThatPointOnly()
    {
        await using var sim = new McProtocolSimServer();
        sim.SetWord(100, 1234);
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(
        [
            Point("out-of-range", "D60000", "short"),
            Point("unsupported-device", "Z1", "short"),
            Point("ok", "D100", "short"),
        ]);

        Assert.Equal(QualityCode.BadCommFailure, results[0].Status);
        Assert.Equal("Quality: Exception", results[0].ErrorMessage);
        Assert.Equal(QualityCode.BadCommFailure, results[1].Status);
        Assert.Equal("Quality: Exception", results[1].ErrorMessage);
        Assert.Equal(QualityCode.Good, results[2].Status);
        Assert.Equal((short)1234, Assert.IsType<short>(results[2].Value));

        // 协议级错误不置 Faulted，连接在错误点之后仍可继续使用。
        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);
        Assert.Equal(3, sim.RequestCount);
    }

    [Fact]
    public async Task WriteAsync_PartiallyFailingBatch_MarksEveryPointBadCommFailure_DocumentsCurrentBehavior()
    {
        await using var sim = new McProtocolSimServer();
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var ok = Point("ok", "D200", "short");
        var bad = Point("bad", "D60000", "short");
        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [ok] = (short)7,
            [bad] = (short)8,
        });

        // Snet 的 WriteAsync 只返回一个整体结果：其中一个地址失败时，
        // 驱动把整批点位都标记为 BadCommFailure，包括实际已经写成功的 D200。
        Assert.Equal(2, results.Length);
        Assert.All(results, r => Assert.Equal(QualityCode.BadCommFailure, r.Status));
        Assert.All(results, r => Assert.Contains("D60000", r.ErrorMessage!, StringComparison.Ordinal));
        Assert.Equal(7, sim.GetWord(200));
        Assert.Equal(2, sim.WriteRequestCount);
    }

    [Fact]
    public async Task ReadAsync_ResponseWithMissingWord_ReportsBadCommFailureForThatPoint()
    {
        await using var sim = new McProtocolSimServer();
        sim.SetWord(100, 1234);
        sim.SetWord(200, 65000);
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        // 第一个请求（D100，非连续点单独成组）的响应丢弃该数据字：Snet 解析失败并给出 Quality: Exception；
        // D200 属于另一个请求组，不受影响（组级隔离）。
        sim.OmitLastResponsePointOnce();
        var results = await driver.ReadAsync(
        [
            Point("p1", "D100", "short"),
            Point("p2", "D200", "short"),
        ]);

        Assert.Equal(QualityCode.BadCommFailure, results[0].Status);
        Assert.Equal("Quality: Exception", results[0].ErrorMessage);
        Assert.Equal(QualityCode.Good, results[1].Status);
        Assert.Equal(unchecked((short)65000), Assert.IsType<short>(results[1].Value));
        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);
    }

    [Fact]
    public async Task ReadAsync_BoolOnDDevice_ReadsWordThenExtractsConfiguredBit()
    {
        await using var sim = new McProtocolSimServer();
        sim.SetWord(110, 0x0021); // bit0=1, bit5=1, bit1=0
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(
        [
            Point("bit0", "D110", "bool"),
            Point("bit5", "D110.5", "bool"),
            Point("bit1", "D110.1", "bool"),
            Point("m110", "M110", "bool"),
        ]);

        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.True(Assert.IsType<bool>(results[0].Value));
        Assert.True(Assert.IsType<bool>(results[1].Value));
        Assert.False(Assert.IsType<bool>(results[2].Value));
        Assert.False(Assert.IsType<bool>(results[3].Value));

        // 字软元件上的 bool 统一按「读字 + 取 bit」实现：D110 / D110.5 / D110.1 共用同一条字读请求，
        // M110 是位软元件，单独一条位读请求。
        Assert.Equal(2, sim.ReadRequestCount);
        Assert.Equal("500000FFFF00000C000A00010400006E0000A80100", sim.RequestFrames[0]);
    }

    [Fact]
    public async Task WriteAsync_BoolOnDDevice_UsesReadModifyWriteAndPreservesOtherBits()
    {
        await using var sim = new McProtocolSimServer();
        sim.SetWord(110, 0x0002); // bit1 已置位，写入 bit0 不应破坏它
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [Point("bit0", "D110", "bool")] = true,
        });

        Assert.Equal(QualityCode.Good, Assert.Single(results).Status);
        Assert.Equal((ushort)0x0003, sim.GetWord(110));
        // 必须走字写（subcommand 0000）而不是 MC 位写（规范下 head=110 会落到 D6.14）
        Assert.Equal("500000FFFF00000E000A00011400006E0000A801000300", sim.RequestFrames[^1]);
    }

    [Fact]
    public async Task WriteAsync_BoolOnDDevice_ClearsOnlyTargetBit()
    {
        await using var sim = new McProtocolSimServer();
        sim.SetWord(110, 0x0021); // bit0 + bit5
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [Point("bit5", "D110.5", "bool")] = false,
        });

        Assert.Equal(QualityCode.Good, Assert.Single(results).Status);
        Assert.Equal((ushort)0x0001, sim.GetWord(110));
    }

    [Fact]
    public async Task BoolOnDDevice_InvalidBitIndex_ReturnsBadConfigError()
    {
        await using var sim = new McProtocolSimServer();
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var read = await driver.ReadAsync([Point("bad", "D110.16", "bool")]);
        Assert.Equal(QualityCode.BadConfigError, read[0].Status);
        Assert.Contains("0-15", read[0].ErrorMessage);
        Assert.Equal(0, sim.RequestCount);

        var write = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [Point("bad", "D110.16", "bool")] = true,
        });
        Assert.Equal(QualityCode.BadConfigError, write[0].Status);
        Assert.Equal(0, sim.WriteRequestCount);
    }

    [Fact]
    public async Task ReadAsync_TagIdDiffersOnlyByCaseFromAddress_StillResolvesByAddress()
    {
        await using var sim = new McProtocolSimServer();
        sim.SetWord(100, 1234);
        sim.SetWord(101, 65000);
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(
        [
            // TagId 与 Address 仅大小写不同：Snet 以 AddressName 为键，按 Address 查即可命中。
            Point("d100", "D100", "short"),
            Point("ok", "D101", "short"),
        ]);

        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal((short)1234, Assert.IsType<short>(results[0].Value));
        Assert.Equal(QualityCode.Good, results[1].Status);
        Assert.Equal(unchecked((short)65000), Assert.IsType<short>(results[1].Value));
    }

    [Fact]
    public async Task ReadAsync_TagIdEqualsAnotherPointsAddress_DoesNotCrossWireValues()
    {
        await using var sim = new McProtocolSimServer();
        sim.SetWord(100, 1234);
        sim.SetWord(101, 65000);
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(
        [
            // TagId "D101" 与另一个点位的 Address 同名：必须按各自的 Address 取值，不能串点。
            Point("D101", "D100", "short"),
            Point("p2", "D101", "short"),
        ]);

        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal((short)1234, Assert.IsType<short>(results[0].Value));
        Assert.Equal(QualityCode.Good, results[1].Status);
        Assert.Equal(unchecked((short)65000), Assert.IsType<short>(results[1].Value));
    }

    [Fact]
    public async Task ReadAsync_AfterDisconnect_ThrowsInvalidOperationException()
    {
        await using var sim = new McProtocolSimServer();
        sim.SetWord(100, 1234);
        await using var driver = CreateDriver(sim.Port);
        await driver.ConnectAsync();
        await driver.DisconnectAsync();

        Assert.Equal(DriverStatus.Disconnected, driver.DriverStatus);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.ReadAsync([Point("p", "D100", "short")]));

        Assert.Equal("Mitsubishi driver is not connected", ex.Message);
        Assert.Equal(0, sim.RequestCount);
    }

    private static MitsubishiDriver CreateDriver(int port, string protocolType = "MC", int timeoutMs = 1000)
    {
        var device = new DeviceConfiguration
        {
            Id = "mc-sim",
            Protocol = "Mitsubishi",
            ConnectionString = $"host:127.0.0.1;port:{port};protocoltype:{protocolType};timeout:{timeoutMs}",
        };
        return new MitsubishiDriver(NullLogger<MitsubishiDriver>.Instance, device);
    }

    private static TagPointConfiguration Point(string tagId, string address, string dataType)
        => new() { TagId = tagId, Address = address, DataType = dataType };

    private static int ReserveClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static void SetFloat(McProtocolSimServer sim, int address, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        sim.SetWord(address, BitConverter.ToUInt16(bytes, 0));
        sim.SetWord(address + 1, BitConverter.ToUInt16(bytes, 2));
    }
}
