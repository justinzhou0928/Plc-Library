using PlcLibrary.Modbus;

namespace PlcLibrary.Tests.Modbus;

public class ModbusAddressParsingTests
{
    [Theory]
    [InlineData("00001", ModbusType.Coil, (ushort)0)]
    [InlineData("00042", ModbusType.Coil, (ushort)41)]
    [InlineData("10001", ModbusType.DiscreteInput, (ushort)0)]
    [InlineData("10042", ModbusType.DiscreteInput, (ushort)41)]
    [InlineData("30001", ModbusType.InputRegister, (ushort)0)]
    [InlineData("30042", ModbusType.InputRegister, (ushort)41)]
    [InlineData("40001", ModbusType.HoldingRegister, (ushort)0)]
    [InlineData("40042", ModbusType.HoldingRegister, (ushort)41)]
    [InlineData("465536", ModbusType.HoldingRegister, (ushort)65535)]
    public void TryParseAddress_ValidAddress_ReturnsCorrectTypeAndOffset(
        string address, ModbusType expectedType, ushort expectedOffset)
    {
        var result = ModbusDriverBase.TryParseAddress(address, out var type, out var offset);
        Assert.True(result);
        Assert.Equal(expectedType, type);
        Assert.Equal(expectedOffset, offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("50001")]
    [InlineData("00000")]
    [InlineData(null)]
    [InlineData("465537")]
    [InlineData("499999")]
    [InlineData("400000")]
    public void TryParseAddress_InvalidAddress_ReturnsFalse(string? address)
    {
        var result = ModbusDriverBase.TryParseAddress(address!, out _, out _);
        Assert.False(result);
    }
}
