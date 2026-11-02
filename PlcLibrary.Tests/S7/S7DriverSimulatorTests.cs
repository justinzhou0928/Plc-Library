using Microsoft.Extensions.Logging.Abstractions;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.General.Configuration;
using PlcLibrary.S7;
using PlcLibrary.Tests.Simulators;

namespace PlcLibrary.Tests.S7;

/// <summary>
/// S7 驱动端到端验证：本机起最小 S7comm 从站（真实 ISO-on-TCP/COTP/S7 报文），
/// 覆盖批量读、真实值解码、单点/批量写、非法地址隔离、链路中断后的状态机。
/// </summary>
public class S7DriverSimulatorTests
{
    private static DeviceConfiguration CreateDevice(int port, params TagPointConfiguration[] points) => new()
    {
        Id = "s7-sim",
        Protocol = "S7",
        ConnectionString = $"host:127.0.0.1;port:{port};rack:0;slot:0;timeout:3000;cpu:S71200",
        CollectionInterval = TimeSpan.FromMilliseconds(100),
        TagPoints = points,
    };

    private static TagPointConfiguration Point(string tagId, string address, string dataType = "")
        => new() { TagId = tagId, Address = address, DataType = dataType };

    [Fact]
    public async Task Read_MixedTypes_ReturnsCorrectValuesInInputOrder()
    {
        await using var sim = new S7SimServer();
        sim.WriteInt16(1, 0, -1234);          // DB1.DBW0
        sim.WriteReal(1, 4, 3.5f);            // DB1.DBD4
        sim.WriteBit(1, 10, 2, true);         // DB1.DBX10.2
        sim.WriteBit(1, 10, 3, false);        // DB1.DBX10.3
        sim.SetString(1, 20, 10, "HELLO");    // DB1.DBB20.10

        var device = CreateDevice(sim.Port,
            Point("word", "DB1.DBW0", "short"),
            Point("real", "DB1.DBD4", "float"),
            Point("bitOn", "DB1.DBX10.2", "bool"),
            Point("bitOff", "DB1.DBX10.3", "bool"),
            Point("text", "DB1.DBB20.10", "string"));

        var driver = new S7Driver(NullLogger<S7Driver>.Instance, device);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(device.TagPoints);

        Assert.Equal(5, results.Length);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal("DB1.DBW0", results[0].Address);
        Assert.Equal((ushort)64302, Assert.IsType<ushort>(results[0].Value));
        // 标量类型按地址推断（README「点位数据类型」章节已说明）：DBD → uint(DWord)。
        // 因此 DataType="float" 不会生效，REAL 点位需要调用方按位转换 —— 见评估报告的一致性风险。
        Assert.Equal(3.5f, BitConverter.UInt32BitsToSingle(Assert.IsType<uint>(results[1].Value)));
        Assert.True(Assert.IsType<bool>(results[2].Value));
        Assert.False(Assert.IsType<bool>(results[3].Value));
        Assert.Equal("HELLO", Assert.IsType<string>(results[4].Value));

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Read_MultiplePoints_SentAsOneBatchRequest()
    {
        await using var sim = new S7SimServer();
        sim.WriteInt16(1, 0, 1);
        sim.WriteInt16(1, 2, 2);

        var device = CreateDevice(sim.Port, Point("a", "DB1.DBW0"), Point("b", "DB1.DBW2"));
        var driver = new S7Driver(NullLogger<S7Driver>.Instance, device);
        await driver.ConnectAsync();

        await driver.ReadAsync(device.TagPoints);

        // 驱动对非字符串点位使用 ReadMultipleVars：两个点位应合并为一次 S7 读请求
        Assert.Equal(1, sim.ReadRequestCount);
        Assert.Equal(1, sim.ConnectionCount);

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Write_SingleWord_UpdatesSimulatorMemoryAndReturnsGood()
    {
        await using var sim = new S7SimServer();
        var device = CreateDevice(sim.Port, Point("w", "DB1.DBW30", "short"));
        var driver = new S7Driver(NullLogger<S7Driver>.Instance, device);
        await driver.ConnectAsync();

        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [device.TagPoints[0]] = (short)777,
        });

        Assert.Single(results);
        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal((short)777, sim.ReadInt16(1, 30));

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Write_MultiplePoints_UsesBatchWriteAndUpdatesAll()
    {
        await using var sim = new S7SimServer();
        var device = CreateDevice(sim.Port, Point("w1", "DB1.DBW40", "short"), Point("r1", "DB1.DBD44", "float"));
        var driver = new S7Driver(NullLogger<S7Driver>.Instance, device);
        await driver.ConnectAsync();

        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [device.TagPoints[0]] = (short)-55,
            [device.TagPoints[1]] = 12.25f,
        });

        Assert.Equal(2, results.Length);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal((short)-55, sim.ReadInt16(1, 40));
        Assert.Equal(12.25f, sim.ReadReal(1, 44));

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Write_InvalidAddress_IsolatesFailureAndStillWritesValidPoints()
    {
        await using var sim = new S7SimServer();
        var device = CreateDevice(sim.Port, Point("valid", "DB1.DBW30"), Point("bad", "NOT_AN_S7_ADDRESS"));
        var driver = new S7Driver(NullLogger<S7Driver>.Instance, device);
        await driver.ConnectAsync();

        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [device.TagPoints[0]] = (short)555,
            [device.TagPoints[1]] = (short)1,
        });

        Assert.Equal(2, results.Length);
        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(QualityCode.BadConfigError, results[1].Status);
        Assert.Equal((short)555, sim.ReadInt16(1, 30));
        Assert.Equal(1, sim.WriteRequestCount);

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Read_InvalidAddress_IsolatesFailureToThatPoint()
    {
        await using var sim = new S7SimServer();
        sim.WriteInt16(1, 0, 321);

        var device = CreateDevice(sim.Port, Point("bad", "NOT_AN_S7_ADDRESS"), Point("good", "DB1.DBW0"));
        var driver = new S7Driver(NullLogger<S7Driver>.Instance, device);
        await driver.ConnectAsync();

        var results = await driver.ReadAsync(device.TagPoints);

        Assert.Equal(2, results.Length);
        Assert.Equal(QualityCode.BadCommFailure, results[0].Status);
        Assert.Equal(QualityCode.Good, results[1].Status);
        Assert.Equal((ushort)321, Assert.IsType<ushort>(results[1].Value));

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task Read_AfterLinkDrop_SurfacesBadQuality_AndReconnectRecovers()
    {
        await using var sim = new S7SimServer();
        sim.WriteInt16(1, 0, 11);

        var device = CreateDevice(sim.Port, Point("w", "DB1.DBW0"));
        var driver = new S7Driver(NullLogger<S7Driver>.Instance, device);
        await driver.ConnectAsync();
        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);

        // 服务端在读到请求后立即断开，模拟现场断链
        sim.DropNextReads(1);
        var failed = await driver.ReadAsync(device.TagPoints);

        Assert.Single(failed);
        Assert.NotEqual(QualityCode.Good, failed[0].Status);

        // 记录驱动在链路中断后的状态机行为（是否识别为传输级故障）
        var statusAfterDrop = driver.DriverStatus;

        // 重连后应恢复读取
        sim.WriteInt16(1, 0, 22);
        Assert.True(await driver.TryReconnectAsync());
        var recovered = await driver.ReadAsync(device.TagPoints);
        Assert.Equal(QualityCode.Good, recovered[0].Status);
        Assert.Equal((ushort)22, Assert.IsType<ushort>(recovered[0].Value));

        // 断链后状态必须是 Faulted，否则连接池不会丢弃失效连接（重连语义依赖该标记）
        Assert.Equal(DriverStatus.Faulted, statusAfterDrop);

        await driver.DisposeAsync();
    }
}
