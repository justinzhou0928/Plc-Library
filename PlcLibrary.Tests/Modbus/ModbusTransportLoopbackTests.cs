using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NModbus;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.General.Configuration;
using PlcLibrary.Modbus;
using PlcLibrary.Tests.Monitor;

namespace PlcLibrary.Tests.Modbus;

/// <summary>
/// Modbus 传输层端到端验证：TCP 真实回环（写路径 + 读回）、UDP 真实回环（读写）、
/// 串口驱动的失败路径（无串口硬件时不挂起、正确置 Faulted）。
/// </summary>
public class ModbusTransportLoopbackTests
{
    private static DeviceConfiguration CreateDevice(string protocol, string connectionString, params TagPointConfiguration[] points) => new()
    {
        Id = $"modbus-{protocol}",
        Protocol = protocol,
        ConnectionString = connectionString,
        CollectionInterval = TimeSpan.FromMilliseconds(100),
        TagPoints = points,
    };

    private static TagPointConfiguration Point(string tagId, string address, string dataType = "")
        => new() { TagId = tagId, Address = address, DataType = dataType };

    [Fact]
    public async Task TcpDriver_WriteHoldingRegisterAndCoil_ReachesSlaveDataStore()
    {
        await using var server = new ModbusSlaveServer(1);
        var device = CreateDevice("Modbus_TCP",
            $"host:127.0.0.1;port:{server.Port};slaveid:1",
            Point("hr", "40010", "ushort"),
            Point("coil", "00020", "bool"));

        var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance, device);
        await driver.ConnectAsync();

        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [device.TagPoints[0]] = (ushort)4242,
            [device.TagPoints[1]] = true,
        });

        Assert.Equal(2, results.Length);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal((ushort)4242, server.Slave.DataStore.HoldingRegisters.ReadPoints(9, 1)[0]);
        Assert.True(server.Slave.DataStore.CoilDiscretes.ReadPoints(19, 1)[0]);

        // 读回同一地址，确认双向一致
        var read = await driver.ReadAsync([device.TagPoints[0]]);
        Assert.Equal(QualityCode.Good, read[0].Status);
        Assert.Equal((ushort)4242, Assert.IsType<ushort>(read[0].Value));

        await driver.DisposeAsync();
    }

    [Fact]
    public async Task UdpDriver_ReadsAndWrites_OverRealUdpLoopback()
    {
        var factory = new ModbusFactory();
        var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var network = factory.CreateSlaveNetwork(listener);
        var slave = factory.CreateSlave(1, null);
        network.AddSlave(slave);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var listen = network.ListenAsync(cts.Token);

        slave.DataStore.HoldingRegisters.WritePoints(0, [11]);

        var device = CreateDevice("Modbus_UDP", $"host:127.0.0.1;port:{port};slaveid:1", Point("hr", "40001"));
        var driver = new ModbusUdpDriver(NullLogger<ModbusUdpDriver>.Instance, device);
        await driver.ConnectAsync();

        var read = await driver.ReadAsync(device.TagPoints);
        Assert.Equal(QualityCode.Good, read[0].Status);
        Assert.Equal((ushort)11, Assert.IsType<ushort>(read[0].Value));

        var write = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [device.TagPoints[0]] = (ushort)33,
        });
        Assert.Equal(QualityCode.Good, write[0].Status);
        Assert.Equal((ushort)33, slave.DataStore.HoldingRegisters.ReadPoints(0, 1)[0]);

        await driver.DisposeAsync();
        cts.Cancel();
        try { await listen; } catch (Exception) { }
        listener.Dispose();
    }

    [Fact]
    public async Task TcpDriver_ConnectTimeout_IsHonoredAndFaults()
    {
        // TEST-NET-1 通常不可路由：连接要在 timeout 内失败，而不是等系统默认超时（20s+）。
        var device = CreateDevice("Modbus_TCP", "host:192.0.2.1;port:502;timeout:300", Point("hr", "40001"));
        var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance, device);

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => driver.ConnectAsync());
        sw.Stop();

        Assert.Equal(DriverStatus.Faulted, driver.DriverStatus);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"连接超时未生效: {sw.Elapsed}");

        await driver.DisposeAsync();
    }

    [Theory]
    [InlineData("Modbus_RTU")]
    [InlineData("Modbus_ASCII")]
    public async Task SerialDriver_UnavailablePort_FaultsImmediatelyWithoutHanging(string protocol)
    {
        var device = CreateDevice(protocol, "host:COM_NOT_EXIST;baudrate:9600;timeout:500", Point("hr", "40001"));
        var driver = protocol == "Modbus_RTU"
            ? (ModbusDriverBase)new ModbusRtuDriver(NullLogger<ModbusRtuDriver>.Instance, device)
            : new ModbusAsciiDriver(NullLogger<ModbusAsciiDriver>.Instance, device);

        var sw = Stopwatch.StartNew();
        Exception? connectError = null;
        try { await driver.ConnectAsync(); }
        catch (Exception ex) { connectError = ex; }

        // 串口在 ConnectAsync 阶段即被打开：端口不存在/被占用必须直接失败并置 Faulted，
        // 这样连接池会丢弃并重建驱动，而不是长期复用一个永远失败的连接。
        Assert.NotNull(connectError);
        Assert.Equal(DriverStatus.Faulted, driver.DriverStatus);

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"串口失败路径耗时过长: {sw.Elapsed}");

        await driver.DisposeAsync();
    }
}
