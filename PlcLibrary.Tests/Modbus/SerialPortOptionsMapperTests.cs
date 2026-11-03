using System.IO.Ports;
using PlcLibrary.Modbus;

namespace PlcLibrary.Tests.Modbus;

public class SerialPortOptionsMapperTests
{
    [Theory]
    [InlineData("None", Parity.None)]
    [InlineData("NONE", Parity.None)]
    [InlineData("none", Parity.None)]
    [InlineData("Odd", Parity.Odd)]
    [InlineData("Even", Parity.Even)]
    [InlineData("Mark", Parity.Mark)]
    [InlineData("Space", Parity.Space)]
    [InlineData("", Parity.None)]
    [InlineData(null, Parity.None)]
    public void ParseParity_ReturnsExpected(string? value, Parity expected)
        => Assert.Equal(expected, SerialPortOptionsMapper.ParseParity(value));

    [Theory]
    [InlineData("One", StopBits.One)]
    [InlineData("Two", StopBits.Two)]
    [InlineData("OnePointFive", StopBits.OnePointFive)]
    [InlineData("", StopBits.One)]
    [InlineData(null, StopBits.One)]
    public void ParseStopBits_ReturnsExpected(string? value, StopBits expected)
        => Assert.Equal(expected, SerialPortOptionsMapper.ParseStopBits(value));

    [Fact]
    public void ParseParity_Unknown_Throws()
        => Assert.Throws<InvalidOperationException>(() => SerialPortOptionsMapper.ParseParity("bogus"));

    [Fact]
    public void ParseStopBits_Unknown_Throws()
        => Assert.Throws<InvalidOperationException>(() => SerialPortOptionsMapper.ParseStopBits("bogus"));
}
