using PlcLibrary.Modbus;

namespace PlcLibrary.Tests.Modbus;

public class ModbusAddressParsingTests
{
    [Theory]
    [InlineData("00001", ModbusDriverBase.ModbusType.Coil, (ushort)0)]
    [InlineData("00042", ModbusDriverBase.ModbusType.Coil, (ushort)41)]
    [InlineData("10001", ModbusDriverBase.ModbusType.DiscreteInput, (ushort)0)]
    [InlineData("10042", ModbusDriverBase.ModbusType.DiscreteInput, (ushort)41)]
    [InlineData("30001", ModbusDriverBase.ModbusType.InputRegister, (ushort)0)]
    [InlineData("30042", ModbusDriverBase.ModbusType.InputRegister, (ushort)41)]
    [InlineData("40001", ModbusDriverBase.ModbusType.HoldingRegister, (ushort)0)]
    [InlineData("40042", ModbusDriverBase.ModbusType.HoldingRegister, (ushort)41)]
    public void TryParseAddress_ValidAddress_ReturnsCorrectTypeAndOffset(
        string address, ModbusDriverBase.ModbusType expectedType, ushort expectedOffset)
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
    public void TryParseAddress_InvalidAddress_ReturnsFalse(string? address)
    {
        var result = ModbusDriverBase.TryParseAddress(address!, out _, out _);
        Assert.False(result);
    }
}
