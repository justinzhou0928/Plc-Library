using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NModbus;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.General.Configuration;
using PlcLibrary.Modbus;
using System.IO;
using System.Net.Sockets;

namespace PlcLibrary.Tests.Modbus;

/// <summary>针对审查发现缺陷的回归测试：Address 填充、PDU 上限切分、负数写入、断线置 Faulted。</summary>
public class ModbusRegressionTests
{
    private readonly Mock<IModbusMaster> _master = new();

    private TestDriver Connect(byte slaveId = 1)
    {
        var driver = new TestDriver(NullLogger.Instance, new ModbusDriverConfig { SlaveId = slaveId }, () => _master.Object);
        driver.ConnectAsync();
        return driver;
    }

    [Fact]
    public async Task SuccessfulReads_PreservePointAddress()
    {
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)2))
            .ReturnsAsync(new ushort[] { 100, 200 });

        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration { TagId = "t1", Address = "40001" },
            new TagPointConfiguration { TagId = "t2", Address = "40002" },
        };

        var results = await driver.ReadAsync(points);

        Assert.Equal("40001", results[0].Address);
        Assert.Equal("40002", results[1].Address);
        Assert.Equal((ushort)100, results[0].Value);
        Assert.Equal((ushort)200, results[1].Value);
    }

    [Fact]
    public async Task ConsecutiveRegisters_OverPduLimit_SplitIntoMultipleRequests()
    {
        // 130 个连续保持寄存器：NModbus 单次上限 125，应拆为 125 + 5 两次请求
        var points = Enumerable.Range(1, 130)
            .Select(i => new TagPointConfiguration { TagId = "t" + i, Address = (40000 + i).ToString() })
            .ToArray();
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)125)).ReturnsAsync(new ushort[125]);
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)125, (ushort)5)).ReturnsAsync(new ushort[5]);

        var driver = Connect();
        var results = await driver.ReadAsync(points);

        _master.Verify(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)125), Times.Once);
        _master.Verify(m => m.ReadHoldingRegistersAsync(1, (ushort)125, (ushort)5), Times.Once);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal(130, results.Length);
    }

    [Fact]
    public async Task NegativeValue_WriteRegisters_TwosComplement()
    {
        _master.Setup(m => m.WriteMultipleRegistersAsync(1, (ushort)0, It.IsAny<ushort[]>()))
            .Returns(Task.CompletedTask);

        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "h1", Address = "40001" }] = -1,
        };

        var results = await driver.WriteAsync(values);

        _master.Verify(m => m.WriteMultipleRegistersAsync(1, (ushort)0,
            It.Is<ushort[]>(a => a[0] == 0xFFFF)), Times.Once);
        Assert.Equal(QualityCode.Good, results[0].Status);
    }

    [Fact]
    public async Task BatchRead_TransportFailure_MarksDriverFaulted()
    {
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)1))
            .ThrowsAsync(new IOException("Connection lost"));

        var driver = Connect();
        var points = new[] { new TagPointConfiguration { TagId = "t1", Address = "40001" } };

        var results = await driver.ReadAsync(points);

        Assert.Equal(QualityCode.BadCommFailure, results[0].Status);
        Assert.Equal(DriverStatus.Faulted, driver.DriverStatus);
    }

    [Fact]
    public async Task BatchRead_BusinessError_DoesNotMarkFaulted()
    {
        // 设备返回的协议级错误（SlaveException）不是传输级故障，不应触发重建
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)1))
            .ThrowsAsync(new SlaveException("Slave device exception"));

        var driver = Connect();
        var points = new[] { new TagPointConfiguration { TagId = "t1", Address = "40001" } };

        var results = await driver.ReadAsync(points);

        Assert.Equal(QualityCode.BadCommFailure, results[0].Status);
        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);
    }

    [Fact]
    public async Task ConnectAsync_FactoryThrows_MarksFaulted()
    {
        var driver = new TestDriver(NullLogger.Instance, new ModbusDriverConfig(),
            () => throw new SocketException());

        await Assert.ThrowsAsync<SocketException>(() => driver.ConnectAsync());

        Assert.Equal(DriverStatus.Faulted, driver.DriverStatus);
    }

    [Fact]
    public async Task TryReconnectAsync_AfterTransportFailure_Reconnects()
    {
        // 通信故障置 Faulted 后，池会调用 TryReconnectAsync：断开旧连接并重新连接
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)1))
            .ThrowsAsync(new IOException("Connection lost"));

        var driver = Connect();
        var points = new[] { new TagPointConfiguration { TagId = "t1", Address = "40001" } };
        await driver.ReadAsync(points);
        Assert.Equal(DriverStatus.Faulted, driver.DriverStatus);

        var reconnected = await driver.TryReconnectAsync();
        Assert.True(reconnected);
        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);
    }

    private sealed class TestDriver : ModbusDriverBase
    {
        public TestDriver(ILogger logger, ModbusDriverConfig config, Func<IModbusMaster> factory)
            : base(logger, config, factory) { }
    }
}
