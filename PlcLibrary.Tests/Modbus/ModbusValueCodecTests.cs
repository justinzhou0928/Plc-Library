using PlcLibrary.Modbus;

namespace PlcLibrary.Tests.Modbus;

public class ModbusValueCodecTests
{
    private static ByteOrderMap Order(string text, int width)
    {
        Assert.True(ByteOrderMap.TryParse(text, width, out var map, out var error), error);
        return map;
    }

    [Theory]
    [InlineData("", "UInt16")]
    [InlineData(null, "UInt16")]
    [InlineData("ushort", "UInt16")]
    [InlineData("short", "Int16")]
    [InlineData("int", "Int32")]
    [InlineData("int32", "Int32")]
    [InlineData("dint", "Int32")]
    [InlineData("uint", "UInt32")]
    [InlineData("long", "Int64")]
    [InlineData("int64", "Int64")]
    [InlineData("ulong", "UInt64")]
    [InlineData("float", "Single")]
    [InlineData("real", "Single")]
    [InlineData("double", "Double")]
    [InlineData("lreal", "Double")]
    [InlineData("bool", "Boolean")]
    [InlineData("string", "String")]
    public void TryGetKind_KnownAliases(string? dataType, string expected)
    {
        Assert.True(ModbusValueCodec.TryGetKind(dataType, out var kind, out var error), error);
        Assert.Equal(expected, kind.ToString());
    }

    [Theory]
    [InlineData("float32")]
    [InlineData("System.Byte")]
    [InlineData("System.SByte")]
    [InlineData("decimal")]
    public void TryGetKind_Unsupported_ReturnsFalse(string dataType)
    {
        Assert.False(ModbusValueCodec.TryGetKind(dataType, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("UInt16", 0, 1)]
    [InlineData("Int16", 0, 1)]
    [InlineData("Boolean", 0, 1)]
    [InlineData("Int32", 0, 2)]
    [InlineData("Single", 0, 2)]
    [InlineData("Int64", 0, 4)]
    [InlineData("Double", 0, 4)]
    [InlineData("String", 1, 1)]
    [InlineData("String", 2, 1)]
    [InlineData("String", 3, 2)]
    [InlineData("String", 24, 12)]
    [InlineData("String", 25, 13)]
    public void TryGetWordCount_ReturnsExpected(string kindName, int length, int expected)
    {
        var kind = Enum.Parse<ModbusValueKind>(kindName);
        Assert.True(ModbusValueCodec.TryGetWordCount(kind, length, out var actual, out var error), error);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("AB", 2, true)]
    [InlineData("BA", 2, true)]
    [InlineData("ABCD", 4, true)]
    [InlineData("CDAB", 4, true)]
    [InlineData("BADC", 4, true)]
    [InlineData("DCBA", 4, true)]
    [InlineData("GHEFCDAB", 8, true)]
    [InlineData("AB", 4, true)]
    [InlineData("BA", 4, false)]
    [InlineData("ABCD", 2, false)]
    [InlineData("ABC", 4, false)]
    [InlineData("ABCE", 4, false)]
    [InlineData("XYZ", 4, false)]
    public void TryParseByteOrder_ValidatesWidthAndPermutation(string text, int width, bool expected)
    {
        Assert.Equal(expected, ByteOrderMap.TryParse(text, width, out _, out _));
    }

    [Theory]
    [InlineData("AB", true)]
    [InlineData("ABCD", true)]
    [InlineData("GHEFCDAB", true)]
    [InlineData("", true)]
    [InlineData("ABC", false)]
    [InlineData("XYZ", false)]
    public void IsValidByteOrderText_ChecksSupportedWidths(string text, bool expected)
    {
        Assert.Equal(expected, ModbusValueCodec.IsValidByteOrderText(text));
    }

    [Fact]
    public void Decode_UInt16_ByteSwap()
    {
        var value = ModbusValueCodec.Decode(new ushort[] { 0x1234 }, ModbusValueKind.UInt16, 0, Order("BA", 2));
        Assert.Equal((ushort)0x3412, value);
    }

    [Theory]
    [InlineData("ABCD", 0x1234, 0x5678, 0x12345678)]
    [InlineData("CDAB", 0x5678, 0x1234, 0x12345678)]
    [InlineData("BADC", 0x3412, 0x7856, 0x12345678)]
    [InlineData("DCBA", 0x7856, 0x3412, 0x12345678)]
    public void Decode_Int32_ByteOrders(string order, ushort word0, ushort word1, int expected)
    {
        var value = ModbusValueCodec.Decode(new ushort[] { word0, word1 }, ModbusValueKind.Int32, 0,
            Order(order, 4));
        Assert.Equal(expected, Assert.IsType<int>(value));
    }

    [Theory]
    [InlineData("ABCD", 0x41A0, 0x0000)]
    [InlineData("CDAB", 0x0000, 0x41A0)]
    public void Decode_Single_ByteOrders(string order, ushort word0, ushort word1)
    {
        var value = ModbusValueCodec.Decode(new ushort[] { word0, word1 }, ModbusValueKind.Single, 0,
            Order(order, 4));
        Assert.Equal(20.0f, Assert.IsType<float>(value));
    }

    [Theory]
    [InlineData("ABCDEFGH", 0x1122, 0x3344, 0x5566, 0x7788)]
    [InlineData("GHEFCDAB", 0x7788, 0x5566, 0x3344, 0x1122)]
    [InlineData("HGFEDCBA", 0x8877, 0x6655, 0x4433, 0x2211)]
    public void Decode_Int64_ByteOrders(string order, ushort word0, ushort word1, ushort word2, ushort word3)
    {
        var value = ModbusValueCodec.Decode(new ushort[] { word0, word1, word2, word3 },
            ModbusValueKind.Int64, 0, Order(order, 8));
        Assert.Equal(0x1122334455667788L, Assert.IsType<long>(value));
    }

    [Fact]
    public void Decode_Double_WordSwap()
    {
        var value = ModbusValueCodec.Decode(
            new ushort[] { 0x0000, 0x0000, 0x0000, 0x3FF0 },
            ModbusValueKind.Double, 0, Order("GHEFCDAB", 8));
        Assert.Equal(1.0, Assert.IsType<double>(value));
    }

    [Fact]
    public void Decode_String_Ab()
    {
        var value = ModbusValueCodec.Decode(new ushort[] { 0x4142, 0x4344 }, ModbusValueKind.String, 4,
            Order("AB", 2));
        Assert.Equal("ABCD", value);
    }

    [Fact]
    public void Decode_String_Ba()
    {
        var value = ModbusValueCodec.Decode(new ushort[] { 0x4241, 0x4443 }, ModbusValueKind.String, 4,
            Order("BA", 2));
        Assert.Equal("ABCD", value);
    }

    [Fact]
    public void Decode_String_OddLength()
    {
        var value = ModbusValueCodec.Decode(new ushort[] { 0x4142, 0x4300 }, ModbusValueKind.String, 3,
            Order("AB", 2));
        Assert.Equal("ABC", value);
    }

    [Fact]
    public void Decode_String_StopsAtFirstNul()
    {
        var value = ModbusValueCodec.Decode(new ushort[] { 0x4142, 0x4300, 0x4445 },
            ModbusValueKind.String, 6, Order("AB", 2));
        Assert.Equal("ABC", value);
    }

    [Fact]
    public void Encode_Int32_Cdab_RoundTrips()
    {
        var buffer = new ushort[2];
        Assert.True(ModbusValueCodec.TryEncode(0x12345678, ModbusValueKind.Int32, 0, Order("CDAB", 4),
            buffer, out var error), error);

        Assert.Equal(new ushort[] { 0x5678, 0x1234 }, buffer);
        var decoded = ModbusValueCodec.Decode(buffer, ModbusValueKind.Int32, 0, Order("CDAB", 4));
        Assert.Equal(0x12345678, Assert.IsType<int>(decoded));
    }

    [Fact]
    public void Encode_Single_Cdab_RoundTrips()
    {
        var buffer = new ushort[2];
        Assert.True(ModbusValueCodec.TryEncode(20.0f, ModbusValueKind.Single, 0, Order("CDAB", 4),
            buffer, out var error), error);

        Assert.Equal(new ushort[] { 0x0000, 0x41A0 }, buffer);
        var decoded = ModbusValueCodec.Decode(buffer, ModbusValueKind.Single, 0, Order("CDAB", 4));
        Assert.Equal(20.0f, Assert.IsType<float>(decoded));
    }

    [Fact]
    public void Encode_String_PadsToLength()
    {
        var buffer = new ushort[4];
        Assert.True(ModbusValueCodec.TryEncode("ABC", ModbusValueKind.String, 8, Order("AB", 2),
            buffer, out var error), error);

        Assert.Equal(new ushort[] { 0x4142, 0x4300, 0x0000, 0x0000 }, buffer);
        Assert.Equal("ABC", ModbusValueCodec.Decode(buffer, ModbusValueKind.String, 8, Order("AB", 2)));
    }

    [Fact]
    public void Encode_String_TooLong_ReturnsFalse()
    {
        var buffer = new ushort[2];
        Assert.False(ModbusValueCodec.TryEncode("ABCDE", ModbusValueKind.String, 4, Order("AB", 2),
            buffer, out var error));
        Assert.NotNull(error);
        Assert.Contains("exceeds", error);
    }

    [Fact]
    public void Encode_String_NonAscii_ReturnsFalse()
    {
        var buffer = new ushort[2];
        Assert.False(ModbusValueCodec.TryEncode("中文", ModbusValueKind.String, 4, Order("AB", 2),
            buffer, out var error));
        Assert.NotNull(error);
        Assert.Contains("ASCII", error);
    }

    [Theory]
    [InlineData(-1, 0xFFFF)]
    [InlineData(1, 0x0001)]
    public void Encode_UInt16_NegativeTwosComplement(int value, ushort expected)
    {
        var buffer = new ushort[1];
        Assert.True(ModbusValueCodec.TryEncode(value, ModbusValueKind.UInt16, 0, Order("AB", 2),
            buffer, out var error), error);
        Assert.Equal(expected, buffer[0]);
    }
}
