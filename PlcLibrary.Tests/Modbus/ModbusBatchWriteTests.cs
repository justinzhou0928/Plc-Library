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

    private sealed class TestDriver : ModbusDriverBase
    {
        public TestDriver(ILogger logger, ModbusDriverConfig config, Func<IModbusMaster> factory)
            : base(logger, config, factory) { }
    }
}
