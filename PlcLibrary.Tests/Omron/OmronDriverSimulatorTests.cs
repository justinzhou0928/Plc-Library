using Microsoft.Extensions.Logging.Abstractions;
using NewLife.Omron.Protocols;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.General.Configuration;
using PlcLibrary.Omron;
using PlcLibrary.Tests.Simulators;

namespace PlcLibrary.Tests.Omron;

/// <summary>
/// Omron FINS 驱动端到端验证：使用 NewLife.Omron 自带的真实 FINS/TCP 服务端（FinsServer）作为 PLC 模拟器，
/// 覆盖握手、类型化读取、写回、PLC 返回错误码、非法类型隔离、以及配置项是否真正生效。
/// </summary>
public class OmronDriverSimulatorTests
{
    /// <summary>DM 区（字访问）区域代码，对应 FINS 存储区 0x82。</summary>
    private const byte DmWordArea = 0x82;

    private static DeviceConfiguration CreateDevice(int port, params TagPointConfiguration[] points) => new()
    {
        Id = "omron-sim",
        Protocol = "Omron_FINS",
        ConnectionString = $"host:127.0.0.1;port:{port};timeout:2000",
        CollectionInterval = TimeSpan.FromMilliseconds(100),
        TagPoints = points,
    };

    private static TagPointConfiguration Point(string tagId, string address, string dataType)
        => new() { TagId = tagId, Address = address, DataType = dataType };

    [Fact]
    public async Task Read_TypedPoints_ReturnsValuesInInputOrder()
    {
        using var server = new FinsServer();
        server.Start(0);
        server.WriteMemory(DmWordArea, 100, [0x04, 0xD2]);                 // D100 = 1234 (AB)
        server.WriteMemory(DmWordArea, 101, [0xFF, 0xFE]);                 // D101 = 65534
        server.WriteMemory(DmWordArea, 102, [0x00, 0x00, 0x40, 0x60]);     // D102 = 3.5f（Omron CDAB）
        server.WriteMemory(DmWordArea, 110, [0x00, 0x01]);                 // D110.0 = true

        var device = CreateDevice(server.Port,
            Point("i16", "D100", "short"),
            Point("u16", "D101", "ushort"),
            Point("f32", "D102", "float"),
            Point("bit", "D110.0", "bool"));

        var driver = new OmronDriver(NullLogger<OmronDriver>.Instance, device);
        await driver.ConnectAsync();
        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);

        var results = await driver.ReadAsync(device.TagPoints);

        Assert.Equal(4, results.Length);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal((short)1234, Assert.IsType<short>(results[0].Value));
        Assert.Equal((ushort)65534, Assert.IsType<ushort>(results[1].Value));
        Assert.Equal(3.5f, Assert.IsType<float>(results[2].Value));
        Assert.True(Assert.IsType<bool>(results[3].Value));

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Write_Int16_ReachesPlcMemory()
    {
        using var server = new FinsServer();
        server.Start(0);

        var device = CreateDevice(server.Port, Point("i16", "D200", "short"));
        var driver = new OmronDriver(NullLogger<OmronDriver>.Instance, device);
        await driver.ConnectAsync();

        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [device.TagPoints[0]] = (short)-2,
        });

        Assert.Single(results);
        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, server.ReadMemory(DmWordArea, 200, 1));

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Read_WriteFloat_RoundTripsWithoutWordOrderLoss()
    {
        using var server = new FinsServer();
        server.Start(0);

        var device = CreateDevice(server.Port, Point("f32", "D300", "float"));
        var driver = new OmronDriver(NullLogger<OmronDriver>.Instance, device);
        await driver.ConnectAsync();

        await driver.WriteAsync(new Dictionary<TagPointConfiguration, object> { [device.TagPoints[0]] = 1.5f });
        var results = await driver.ReadAsync(device.TagPoints);

        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(1.5f, Assert.IsType<float>(results[0].Value));

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Read_PlcReturnsErrorCode_MapsToBadQualityWithoutBreakingBatch()
    {
        using var server = new FinsServer();
        server.Start(0);
        server.WriteMemory(DmWordArea, 100, [0x00, 0x2A]);

        var device = CreateDevice(server.Port, Point("i16", "D100", "short"));
        var driver = new OmronDriver(NullLogger<OmronDriver>.Instance, device);
        await driver.ConnectAsync();

        server.ForceErrorCode = 0x1101;
        var results = await driver.ReadAsync(device.TagPoints);

        Assert.Equal(QualityCode.BadCommFailure, results[0].Status);
        Assert.False(string.IsNullOrWhiteSpace(results[0].ErrorMessage));

        // PLC 侧返回错误码属于业务错误，驱动不应把连接标记为 Faulted
        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Read_UnknownDataType_ReturnsBadConfigError()
    {
        using var server = new FinsServer();
        server.Start(0);
        server.WriteMemory(DmWordArea, 100, [0x00, 0x2A, 0x00, 0x00]);

        var device = CreateDevice(server.Port,
            Point("unknown", "D100", "decimal"),
            Point("ok", "D100", "short"));

        var driver = new OmronDriver(NullLogger<OmronDriver>.Instance, device);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(device.TagPoints);

        // 未知 DataType 明确报配置错误，不再静默回退 int 读取（避免误读类型）
        Assert.Equal(QualityCode.BadConfigError, results[0].Status);
        Assert.Contains("unsupported DataType", results[0].ErrorMessage);
        Assert.Equal(QualityCode.Good, results[1].Status);
        Assert.Equal((short)42, Assert.IsType<short>(results[1].Value));

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Read_ManyPoints_MergesIntoSingleWordAndBitRequest()
    {
        using var server = new FinsServer();
        server.Start(0);
        for (ushort i = 0; i < 5; i++)
            server.WriteMemory(DmWordArea, (ushort)(100 + i), [0x00, (byte)(i + 1)]);
        server.WriteMemory(DmWordArea, 110, [0x00, 0x07]); // 字值 0x0007：bit0..bit2 = 1

        await using var proxy = new FinsFrameCounterProxy(server.Port);

        var device = CreateDevice(proxy.Port,
            Point("w0", "D100", "short"),
            Point("w1", "D101", "short"),
            Point("w2", "D102", "short"),
            Point("w3", "D103", "short"),
            Point("w4", "D104", "short"),
            Point("b0", "D110.0", "bool"),
            Point("b1", "D110.1", "bool"),
            Point("b2", "D110.2", "bool"));

        var driver = new OmronDriver(NullLogger<OmronDriver>.Instance, device);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(device.TagPoints);

        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal([1, 2, 3, 4, 5], results.Take(5).Select(r => (int)Assert.IsType<short>(r.Value)));
        Assert.All(results.Skip(5), r => Assert.True(Assert.IsType<bool>(r.Value)));

        // 8 个点位合并为 2 次请求（连续字 1 次 + 连续位 1 次），逐点实现应为 8 次
        Assert.Equal(2, proxy.DataFrameCount);

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Read_NumericPointWithBitSuffix_ReturnsBadConfigErrorWithoutWireRequest()
    {
        using var server = new FinsServer();
        server.Start(0);
        await using var proxy = new FinsFrameCounterProxy(server.Port);

        var device = CreateDevice(proxy.Port, Point("bad", "D100.5", "short"));
        var driver = new OmronDriver(NullLogger<OmronDriver>.Instance, device);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(device.TagPoints);

        Assert.Equal(QualityCode.BadConfigError, Assert.Single(results).Status);
        Assert.Contains("bit suffix", results[0].ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, proxy.DataFrameCount);

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Read_BoolPointWithBitOffsetAbove15_ReturnsBadConfigErrorWithoutWireRequest()
    {
        using var server = new FinsServer();
        server.Start(0);
        await using var proxy = new FinsFrameCounterProxy(server.Port);

        var device = CreateDevice(proxy.Port, Point("bad", "D100.16", "bool"));
        var driver = new OmronDriver(NullLogger<OmronDriver>.Instance, device);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(device.TagPoints);

        Assert.Equal(QualityCode.BadConfigError, Assert.Single(results).Status);
        Assert.Contains("0-15", results[0].ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, proxy.DataFrameCount);

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Read_AfterDisconnect_ThrowsInvalidOperation()
    {
        using var server = new FinsServer();
        server.Start(0);

        var device = CreateDevice(server.Port, Point("i16", "D100", "short"));
        var driver = new OmronDriver(NullLogger<OmronDriver>.Instance, device);
        await driver.ConnectAsync();
        await driver.DisconnectAsync();

        Assert.Equal(DriverStatus.Disconnected, driver.DriverStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ReadAsync(device.TagPoints));

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Connect_IsUdpConfigured_StillUsesTcp_DocumentsCurrentBehavior()
    {
        // 连接串里的 isudp/localnode/destinynode 被 OmronDriverConfig 解析，但驱动从未使用：
        // 这里用「只监听 TCP 的 FINS 服务端 + isudp:true」证明该配置被静默忽略。
        using var server = new FinsServer();
        server.Start(0);

        var device = new DeviceConfiguration
        {
            Id = "omron-udp-config",
            Protocol = "Omron_FINS",
            ConnectionString = $"host:127.0.0.1;port:{server.Port};timeout:2000;isudp:true;localnode:5;destinynode:9",
            TagPoints = [Point("i16", "D100", "short")],
        };

        var driver = new OmronDriver(NullLogger<OmronDriver>.Instance, device);
        await driver.ConnectAsync();

        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);
        Assert.Equal(QualityCode.Good, (await driver.ReadAsync(device.TagPoints))[0].Status);

        await driver.DisposeAsync();
    }
}
