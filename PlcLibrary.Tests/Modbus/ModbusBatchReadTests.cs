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

    [Fact]
    public async Task StringPoint_ReadsAllRegistersInOneRequest()
    {
        const string barcode = "ABCDEFGHIJKLMNOPQRSTUVWX";
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)2250, (ushort)12))
            .ReturnsAsync(StringRegisters(barcode, 24));

        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration
            {
                TagId = "barcode",
                Address = "42251",
                DataType = "string",
                Length = 24,
            },
        };

        var results = await driver.ReadAsync(points);

        _master.Verify(m => m.ReadHoldingRegistersAsync(1, (ushort)2250, (ushort)12), Times.Once);
        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal("42251", results[0].Address);
        Assert.Equal(barcode, results[0].Value);
    }

    [Fact]
    public async Task StringAndAdjacentFloat_MergedIntoSingleRequest()
    {
        const string barcode = "ABCDEFGHIJKLMNOPQRSTUVWX";
        var registers = new ushort[14];
        StringRegisters(barcode, 24).CopyTo(registers, 0);
        registers[12] = 0x0000;
        registers[13] = 0x41A0; // CDAB 下的 20.0f

        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)2250, (ushort)14))
            .ReturnsAsync(registers);

        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration
            {
                TagId = "barcode",
                Address = "42251",
                DataType = "string",
                Length = 24,
            },
            new TagPointConfiguration
            {
                TagId = "temp",
                Address = "42263",
                DataType = "float",
                ByteOrder = "CDAB",
            },
        };

        var results = await driver.ReadAsync(points);

        _master.Verify(m => m.ReadHoldingRegistersAsync(1, (ushort)2250, (ushort)14), Times.Once);
        Assert.Equal(barcode, results[0].Value);
        Assert.Equal(20.0f, Assert.IsType<float>(results[1].Value));
    }

    [Fact]
    public async Task OverlappingFloatAndUshort_BothDecodedFromSingleRequest()
    {
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)2262, (ushort)2))
            .ReturnsAsync(new ushort[] { 0x41A0, 0x0000 });

        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration { TagId = "temp", Address = "42263", DataType = "float" },
            new TagPointConfiguration { TagId = "raw", Address = "42264" },
        };

        var results = await driver.ReadAsync(points);

        _master.Verify(m => m.ReadHoldingRegistersAsync(1, (ushort)2262, (ushort)2), Times.Once);
        Assert.Equal(20.0f, Assert.IsType<float>(results[0].Value));
        Assert.Equal((ushort)0, results[1].Value);
    }

    [Fact]
    public async Task StringPoint_ByteOrderBa_DecodesSwappedRegisters()
    {
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)2250, (ushort)2))
            .ReturnsAsync(new ushort[] { 0x4241, 0x4443 });

        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration
            {
                TagId = "s",
                Address = "42251",
                DataType = "string",
                Length = 4,
                ByteOrder = "BA",
            },
        };

        var results = await driver.ReadAsync(points);

        Assert.Equal("ABCD", results[0].Value);
    }

    [Fact]
    public async Task InputRegisterString_ReadsAllRegisters()
    {
        _master.Setup(m => m.ReadInputRegistersAsync(1, (ushort)2250, (ushort)2))
            .ReturnsAsync(new ushort[] { 0x4142, 0x4344 });

        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration
            {
                TagId = "s",
                Address = "32251",
                DataType = "string",
                Length = 4,
            },
        };

        var results = await driver.ReadAsync(points);

        _master.Verify(m => m.ReadInputRegistersAsync(1, (ushort)2250, (ushort)2), Times.Once);
        Assert.Equal("ABCD", results[0].Value);
    }

    [Fact]
    public async Task LegacyPointWithoutDataType_StillReturnsUshort()
    {
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)1))
            .ReturnsAsync(new ushort[] { 0x1234 });

        var driver = Connect();
        var points = new[] { new TagPointConfiguration { TagId = "h", Address = "40001" } };

        var results = await driver.ReadAsync(points);

        Assert.Equal((ushort)0x1234, results[0].Value);
    }

    [Fact]
    public async Task DeviceByteOrder_AppliesOnlyToMatchingWidth()
    {
        _master.Setup(m => m.ReadHoldingRegistersAsync(1, (ushort)0, (ushort)3))
            .ReturnsAsync(new ushort[] { 0x0000, 0x41A0, 0x1234 });

        var config = new ModbusDriverConfig { ByteOrder = "CDAB" };
        var driver = new TestDriver(NullLogger.Instance, config, () => _master.Object);
        await driver.ConnectAsync();

        var points = new[]
        {
            new TagPointConfiguration { TagId = "temp", Address = "40001", DataType = "float" },
            new TagPointConfiguration { TagId = "raw", Address = "40003" },
        };

        var results = await driver.ReadAsync(points);

        Assert.Equal(20.0f, Assert.IsType<float>(results[0].Value));
        Assert.Equal((ushort)0x1234, results[1].Value);
    }

    [Fact]
    public async Task InvalidTypedConfig_ReturnsBadConfigError()
    {
        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration { TagId = "unknown", Address = "40001", DataType = "float32" },
            new TagPointConfiguration { TagId = "noLength", Address = "40002", DataType = "string" },
            new TagPointConfiguration { TagId = "lengthOnInt", Address = "40003", DataType = "int", Length = 2 },
            new TagPointConfiguration { TagId = "badOrder", Address = "40004", DataType = "float", ByteOrder = "BA" },
        };

        var results = await driver.ReadAsync(points);

        Assert.All(results, r => Assert.Equal(QualityCode.BadConfigError, r.Status));
        _master.Verify(m => m.ReadHoldingRegistersAsync(It.IsAny<byte>(), It.IsAny<ushort>(),
            It.IsAny<ushort>()), Times.Never);
    }

    [Fact]
    public async Task StringPointOverReadLimit_ReturnsBadConfigError()
    {
        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration
            {
                TagId = "long",
                Address = "40001",
                DataType = "string",
                Length = 252,
            },
        };

        var results = await driver.ReadAsync(points);

        Assert.Equal(QualityCode.BadConfigError, results[0].Status);
        Assert.Contains("read limit", results[0].ErrorMessage);
        _master.Verify(m => m.ReadHoldingRegistersAsync(It.IsAny<byte>(), It.IsAny<ushort>(),
            It.IsAny<ushort>()), Times.Never);
    }

    [Fact]
    public async Task StringPointBeyondAddressSpace_ReturnsBadConfigError()
    {
        var driver = Connect();
        var points = new[]
        {
            new TagPointConfiguration
            {
                TagId = "tail",
                Address = "465530",
                DataType = "string",
                Length = 24,
            },
        };

        var results = await driver.ReadAsync(points);

        Assert.Equal(QualityCode.BadConfigError, results[0].Status);
        Assert.Contains("address space", results[0].ErrorMessage);
    }

    private static ushort[] StringRegisters(string text, int length)
    {
        var padded = text.PadRight(length, '\0');
        var registers = new ushort[(length + 1) / 2];
        for (var i = 0; i < registers.Length; i++)
        {
            var hi = (byte)padded[2 * i];
            var lo = 2 * i + 1 < padded.Length ? (byte)padded[2 * i + 1] : (byte)0;
            registers[i] = (ushort)((hi << 8) | lo);
        }
        return registers;
    }

    private sealed class TestDriver : ModbusDriverBase
    {
        public TestDriver(ILogger logger, ModbusDriverConfig config, Func<IModbusMaster> factory)
            : base(logger, config, factory) { }
    }
}
