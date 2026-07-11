using PlcLibrary.Modbus;

namespace PlcLibrary.Tests.Modbus;

public class ModbusDriverConfigTests
{
    [Fact]
    public void DefaultValues_WhenNewInstance()
    {
        var config = new ModbusDriverConfig();
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(502, config.Port);
        Assert.Equal(9600, config.BaudRate);
        Assert.Equal("None", config.Parity);
        Assert.Equal(8, config.DataBits);
        Assert.Equal("One", config.StopBits);
        Assert.Equal(3000, config.Timeout);
        Assert.Equal(1, config.SlaveId);
    }

    [Fact]
    public void Parse_TcpConnectionString_ReturnsTcpFields()
    {
        var config = ModbusDriverConfig.Parse("host:10.0.0.1;port:502;slaveid:2");
        Assert.Equal("10.0.0.1", config.Host);
        Assert.Equal(502, config.Port);
        Assert.Equal(2, config.SlaveId);
    }

    [Fact]
    public void Parse_RtuConnectionString_ReturnsSerialFields()
    {
        var config = ModbusDriverConfig.Parse("host:COM3;baudrate:19200;parity:Even;databits:7;stopbits:Two");
        Assert.Equal("COM3", config.Host);
        Assert.Equal(19200, config.BaudRate);
        Assert.Equal("Even", config.Parity);
        Assert.Equal(7, config.DataBits);
        Assert.Equal("Two", config.StopBits);
    }

    [Fact]
    public void Parse_CaseInsensitive_KeysAreNormalized()
    {
        var config = ModbusDriverConfig.Parse("HOST:10.0.0.1;SLAVEID:5");
        Assert.Equal("10.0.0.1", config.Host);
        Assert.Equal(5, config.SlaveId);
    }
}
