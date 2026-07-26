using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NModbus;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using PlcLibrary.Modbus;

namespace PlcLibrary.Tests.Modbus;

public class ModbusBatchReadTests
{
    private readonly Mock<IModbusMaster> _master = new();

    private TestDriver Connect(byte slaveId = 1)
    {
        var config = new ModbusDriverConfig { SlaveId = slaveId };
        var driver = new TestDriver(NullLogger.Instance, config, () => _master.Object);
        driver.ConnectAsync();
        return driver;
    }

    [Fact]
    public async Task ConsecutiveHoldingRegisters_SingleBatch()
    {
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)3))
            .ReturnsAsync(new ushort[] { 100, 200, 300 });

        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration { TagId = "t1", Address = "40001" },
            new TagPointConfiguration { TagId = "t2", Address = "40002" },
            new TagPointConfiguration { TagId = "t3", Address = "40003" },
        };

        var results = await driver.ReadAsync(points);

        _master.Verify(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)3), Times.Once);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal((ushort)100, results[0].Value);
        Assert.Equal((ushort)200, results[1].Value);
        Assert.Equal((ushort)300, results[2].Value);
    }

    [Fact]
    public async Task NonConsecutiveHoldingRegisters_MultipleBatches()
    {
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)2))
            .ReturnsAsync(new ushort[] { 100, 200 });
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)4, (ushort)2))
            .ReturnsAsync(new ushort[] { 500, 600 });

        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration { TagId = "t1", Address = "40001" },
            new TagPointConfiguration { TagId = "t2", Address = "40002" },
            new TagPointConfiguration { TagId = "t5", Address = "40005" },
            new TagPointConfiguration { TagId = "t6", Address = "40006" },
        };

        var results = await driver.ReadAsync(points);

        _master.Verify(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)2), Times.Once);
        _master.Verify(m => m.ReadHoldingRegistersAsync(1, (ushort)4, (ushort)2), Times.Once);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal((ushort)100, results[0].Value);
        Assert.Equal((ushort)200, results[1].Value);
        Assert.Equal((ushort)500, results[2].Value);
        Assert.Equal((ushort)600, results[3].Value);
    }

    [Fact]
    public async Task MixedTypes_BatchedSeparately()
    {
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)2))
            .ReturnsAsync(new ushort[] { 100, 200 });
        _master.Setup(m => m.ReadCoilsAsync(1, (ushort)0, (ushort)2))
            .ReturnsAsync(new bool[] { true, false });

        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration { TagId = "h1", Address = "40001" },
            new TagPointConfiguration { TagId = "c1", Address = "00001" },
            new TagPointConfiguration { TagId = "h2", Address = "40002" },
            new TagPointConfiguration { TagId = "c2", Address = "00002" },
        };

        var results = await driver.ReadAsync(points);

        _master.Verify(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)2), Times.Once);
        _master.Verify(m => m.ReadCoilsAsync(1, (ushort)0, (ushort)2), Times.Once);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal((ushort)100, results[0].Value);
        Assert.Equal(true, results[1].Value);
        Assert.Equal((ushort)200, results[2].Value);
        Assert.Equal(false, results[3].Value);
    }

    [Fact]
    public async Task InvalidAddress_ReturnsBadResult()
    {
        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration { TagId = "t1", Address = "40001" },
            new TagPointConfiguration { TagId = "bad", Address = "99999" },
        };

        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)1))
            .ReturnsAsync(new ushort[] { 100 });

        var results = await driver.ReadAsync(points);

        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(QualityCode.BadConfigError, results[1].Status);
    }

    [Fact]
    public async Task BatchFails_AllMembersGetBadResult()
    {
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)3))
            .ThrowsAsync(new TimeoutException("Connection lost"));

        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration { TagId = "t1", Address = "40001" },
            new TagPointConfiguration { TagId = "t2", Address = "40002" },
            new TagPointConfiguration { TagId = "t3", Address = "40003" },
        };

        var results = await driver.ReadAsync(points);

        Assert.All(results, r => Assert.Equal(QualityCode.BadCommFailure, r.Status));
        Assert.All(results, r => Assert.Contains("Connection lost", r.ErrorMessage));
    }

    [Fact]
    public async Task PartialBatchFails_UnaffectedGroupStillReads()
    {
        _master.Setup(m => m.ReadCoilsAsync(1, (ushort)0, (ushort)2))
            .ReturnsAsync(new bool[] { true, false });
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)2))
            .ThrowsAsync(new TimeoutException("Timeout"));

        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration { TagId = "c1", Address = "00001" },
            new TagPointConfiguration { TagId = "c2", Address = "00002" },
            new TagPointConfiguration { TagId = "h1", Address = "40001" },
            new TagPointConfiguration { TagId = "h2", Address = "40002" },
        };

        var results = await driver.ReadAsync(points);

        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(QualityCode.Good, results[1].Status);
        Assert.Equal(QualityCode.BadCommFailure, results[2].Status);
        Assert.Equal(QualityCode.BadCommFailure, results[3].Status);
    }

    [Fact]
    public async Task AllFourRegisterTypes_BatchedCorrectly()
    {
        _master.Setup(m => m.ReadCoilsAsync(1, (ushort)0, (ushort)1))
            .ReturnsAsync(new bool[] { true });
        _master.Setup(m => m.ReadInputsAsync(1, (ushort)0, (ushort)1))
            .ReturnsAsync(new bool[] { false });
        _master.Setup(m => m.ReadInputRegistersAsync(1, (ushort)0, (ushort)1))
            .ReturnsAsync(new ushort[] { 50 });
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)1))
            .ReturnsAsync(new ushort[] { 100 });

        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration { TagId = "coil", Address = "00001" },
            new TagPointConfiguration { TagId = "di", Address = "10001" },
            new TagPointConfiguration { TagId = "ir", Address = "30001" },
            new TagPointConfiguration { TagId = "hr", Address = "40001" },
        };

        var results = await driver.ReadAsync(points);

        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
        Assert.Equal(true, results[0].Value);
        Assert.Equal(false, results[1].Value);
        Assert.Equal((ushort)50, results[2].Value);
        Assert.Equal((ushort)100, results[3].Value);

        _master.Verify(m => m.ReadCoilsAsync(1, (ushort)0, (ushort)1), Times.Once);
        _master.Verify(m => m.ReadInputsAsync(1, (ushort)0, (ushort)1), Times.Once);
        _master.Verify(m => m.ReadInputRegistersAsync(1, (ushort)0, (ushort)1), Times.Once);
        _master.Verify(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)1), Times.Once);
    }

    private sealed class TestDriver : ModbusDriverBase
    {
        public TestDriver(ILogger logger, ModbusDriverConfig config, Func<IModbusMaster> factory)
            : base(logger, config, factory) { }
    }
}
