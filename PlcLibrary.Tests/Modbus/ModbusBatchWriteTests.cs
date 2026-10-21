using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NModbus;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using PlcLibrary.Modbus;

namespace PlcLibrary.Tests.Modbus;

public class ModbusBatchWriteTests
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
        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "h1", Address = "40001" }] = (ushort)100,
            [new() { TagId = "h2", Address = "40002" }] = (ushort)200,
            [new() { TagId = "h3", Address = "40003" }] = (ushort)300,
        };

        var results = await driver.WriteAsync(values);

        _master.Verify(m => m.WriteMultipleRegistersAsync(
            1, (ushort)0, It.Is<ushort[]>(a => a[0] == 100 && a[1] == 200 && a[2] == 300)), Times.Once);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
    }

    [Fact]
    public async Task ConsecutiveCoils_SingleBatch()
    {
        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "c1", Address = "00001" }] = true,
            [new() { TagId = "c2", Address = "00002" }] = false,
        };

        var results = await driver.WriteAsync(values);

        _master.Verify(m => m.WriteMultipleCoilsAsync(
            1, (ushort)0, It.Is<bool[]>(a => a[0] == true && a[1] == false)), Times.Once);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
    }

    [Fact]
    public async Task NonConsecutiveAddresses_MultipleBatches()
    {
        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "h1", Address = "40001" }] = (ushort)100,
            [new() { TagId = "h5", Address = "40005" }] = (ushort)500,
        };

        var results = await driver.WriteAsync(values);

        _master.Verify(m => m.WriteMultipleRegistersAsync(1, (ushort)0, It.IsAny<ushort[]>()), Times.Once);
        _master.Verify(m => m.WriteMultipleRegistersAsync(1, (ushort)4, It.IsAny<ushort[]>()), Times.Once);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
    }

    [Fact]
    public async Task WriteFails_AllMembersGetBadResult()
    {
        _master.Setup(m => m.WriteMultipleRegistersAsync(1, (ushort)0, It.IsAny<ushort[]>()))
            .ThrowsAsync(new TimeoutException("No response"));

        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "h1", Address = "40001" }] = (ushort)100,
            [new() { TagId = "h2", Address = "40002" }] = (ushort)200,
        };

        var results = await driver.WriteAsync(values);

        Assert.All(results, r => Assert.Equal(QualityCode.BadCommFailure, r.Status));
        Assert.All(results, r => Assert.Contains("No response", r.ErrorMessage));
    }

    [Fact]
    public async Task UnwritableType_ReturnsBadResult()
    {
        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "ir", Address = "30001" }] = (ushort)100,
        };

        var results = await driver.WriteAsync(values);

        Assert.Equal(QualityCode.BadConfigError, results[0].Status);
        Assert.Contains("Write not supported for", results[0].ErrorMessage);
    }

    [Fact]
    public async Task InvalidAddress_ReturnsBadResult()
    {
        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "bad", Address = "99999" }] = (ushort)100,
        };

        var results = await driver.WriteAsync(values);

        Assert.Equal(QualityCode.BadConfigError, results[0].Status);
    }

    [Fact]
    public async Task MixedValidInvalid_ValidStillWritten()
    {
        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "bad", Address = "99999" }] = (ushort)100,
            [new() { TagId = "h1", Address = "40001" }] = (ushort)200,
        };

        var results = await driver.WriteAsync(values);

        Assert.Equal(QualityCode.BadConfigError, results[0].Status);
        Assert.Equal(QualityCode.Good, results[1].Status);
        _master.Verify(m => m.WriteMultipleRegistersAsync(1, (ushort)0, It.IsAny<ushort[]>()), Times.Once);
    }

    [Fact]
    public async Task FloatPoint_WritesTwoRegisters()
    {
        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "temp", Address = "42263", DataType = "float", ByteOrder = "CDAB" }] = 20.0f,
        };

        var results = await driver.WriteAsync(values);

        _master.Verify(m => m.WriteMultipleRegistersAsync(1, (ushort)2262,
            It.Is<ushort[]>(a => a.Length == 2 && a[0] == 0x0000 && a[1] == 0x41A0)), Times.Once);
        Assert.Equal(QualityCode.Good, results[0].Status);
    }

    [Fact]
    public async Task StringPoint_WritesPaddedRegisters()
    {
        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "s", Address = "42251", DataType = "string", Length = 8 }] = "ABC",
        };

        var results = await driver.WriteAsync(values);

        _master.Verify(m => m.WriteMultipleRegistersAsync(1, (ushort)2250,
            It.Is<ushort[]>(a => a.Length == 4
                && a[0] == 0x4142 && a[1] == 0x4300 && a[2] == 0x0000 && a[3] == 0x0000)), Times.Once);
        Assert.Equal(QualityCode.Good, results[0].Status);
    }

    [Fact]
    public async Task StringAndAdjacentFloat_SingleWriteRequest()
    {
        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "s", Address = "42251", DataType = "string", Length = 4 }] = "ABCD",
            [new() { TagId = "f", Address = "42253", DataType = "float" }] = 20.0f,
        };

        var results = await driver.WriteAsync(values);

        _master.Verify(m => m.WriteMultipleRegistersAsync(1, (ushort)2250,
            It.Is<ushort[]>(a => a.Length == 4
                && a[0] == 0x4142 && a[1] == 0x4344
                && a[2] == 0x41A0 && a[3] == 0x0000)), Times.Once);
        Assert.All(results, r => Assert.Equal(QualityCode.Good, r.Status));
    }

    [Fact]
    public async Task OverlappingWritePoints_ReturnBadConfigError()
    {
        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "s", Address = "42251", DataType = "string", Length = 24 }]
                = "ABCDEFGHIJKLMNOPQRSTUVWX",
            [new() { TagId = "raw", Address = "42255" }] = (ushort)1,
        };

        var results = await driver.WriteAsync(values);

        Assert.All(results, r => Assert.Equal(QualityCode.BadConfigError, r.Status));
        Assert.All(results, r => Assert.Contains("Overlapping", r.ErrorMessage));
        _master.Verify(m => m.WriteMultipleRegistersAsync(It.IsAny<byte>(), It.IsAny<ushort>(),
            It.IsAny<ushort[]>()), Times.Never);
    }

    [Fact]
    public async Task StringValueTooLong_ReturnsBadConfigError()
    {
        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "s", Address = "42251", DataType = "string", Length = 4 }] = "ABCDE",
        };

        var results = await driver.WriteAsync(values);

        Assert.Equal(QualityCode.BadConfigError, results[0].Status);
        Assert.Contains("exceeds", results[0].ErrorMessage);
    }

    [Fact]
    public async Task NonAsciiStringValue_ReturnsBadConfigError()
    {
        var driver = Connect();
        var values = new Dictionary<TagPointConfiguration, object>
        {
            [new() { TagId = "s", Address = "42251", DataType = "string", Length = 4 }] = "中文",
        };

        var results = await driver.WriteAsync(values);

        Assert.Equal(QualityCode.BadConfigError, results[0].Status);
        Assert.Contains("ASCII", results[0].ErrorMessage);
    }

    private sealed class TestDriver : ModbusDriverBase
    {
        public TestDriver(ILogger logger, ModbusDriverConfig config, Func<IModbusMaster> factory)
            : base(logger, config, factory) { }
    }
}
