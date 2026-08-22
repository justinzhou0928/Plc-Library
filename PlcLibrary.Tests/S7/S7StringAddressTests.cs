using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using PlcLibrary.S7;
using S7.Net;
using S7.Net.Types;

namespace PlcLibrary.Tests.S7;

/// <summary>S7 string 地址解析与 DataItem 构造测试（离线，无需 PLC）。</summary>
public class S7StringAddressTests
{
    [Theory]
    [InlineData("DB6000.DBB504.100", 6000, 504, 100)]
    [InlineData("DB1.DBB100.10", 1, 100, 10)]
    [InlineData("DB21.DBB0.254", 21, 0, 254)]
    [InlineData("db6000.dbb504.100", 6000, 504, 100)] // 大小写不敏感
    public void TryParseStringAddress_Valid_ReturnsParts(string address, int db, int start, int len)
    {
        Assert.True(S7Driver.TryParseStringAddress(address, out var actualDb, out var actualStart, out var actualLen));
        Assert.Equal(db, actualDb);
        Assert.Equal(start, actualStart);
        Assert.Equal(len, actualLen);
    }

    [Theory]
    [InlineData("")]
    [InlineData("DB6000")]
    [InlineData("DB6000.DBB504")]          // 无长度
    [InlineData("DB6000.DBB504.0")]        // 长度 0
    [InlineData("DB6000.DBB504.-1")]       // 负长度
    [InlineData("DB6000.DBW504.100")]      // 非 DBB
    [InlineData("DB6000.DBX604.1")]        // 位地址不是字符串
    [InlineData("M10.10")]
    [InlineData("DB6000.ABC504.100")]
    public void TryParseStringAddress_Invalid_ReturnsFalse(string address)
        => Assert.False(S7Driver.TryParseStringAddress(address, out _, out _, out _));

    [Fact]
    public void IsStringPoint_AddressWithLength_ReturnsTrue()
    {
        var point = new TagPointConfiguration { TagId = "s", Address = "DB6000.DBB504.100" };
        Assert.True(S7Driver.IsStringPoint(point));
    }

    [Fact]
    public void IsStringPoint_ExplicitStringDataType_ReturnsTrue()
    {
        var point = new TagPointConfiguration { TagId = "s", Address = "DB6000.DBB504", DataType = "string" };
        Assert.True(S7Driver.IsStringPoint(point));
    }

    [Fact]
    public void IsStringPoint_ScalarAddress_ReturnsFalse()
    {
        var point = new TagPointConfiguration { TagId = "b", Address = "DB6000.DBX604.1" };
        Assert.False(S7Driver.IsStringPoint(point));
    }

    [Fact]
    public void CreateDataItem_StringAddress_BuildsS7StringItem()
    {
        var point = new TagPointConfiguration { TagId = "s", Address = "DB6000.DBB504.100" };

        var item = S7Driver.CreateDataItem(point);

        Assert.Equal(DataType.DataBlock, item.DataType);
        Assert.Equal(VarType.S7String, item.VarType);
        Assert.Equal(6000, item.DB);
        Assert.Equal(504, item.StartByteAdr);
        Assert.Equal(100, item.Count);
    }

    [Fact]
    public void CreateDataItem_ScalarAddress_DelegatesToFromAddress()
    {
        var point = new TagPointConfiguration { TagId = "b", Address = "DB6000.DBX604.1" };

        var item = S7Driver.CreateDataItem(point);

        Assert.Equal(VarType.Bit, item.VarType);
        Assert.Equal(604, item.StartByteAdr);
        Assert.Equal(1, item.BitAdr);
    }

    [Fact]
    public void CreateDataItem_StringDataTypeWithoutLength_Throws()
    {
        var point = new TagPointConfiguration { TagId = "s", Address = "DB6000.DBB504", DataType = "string" };

        Assert.Throws<InvalidAddressException>(() => S7Driver.CreateDataItem(point));
    }

    [Fact]
    public void S7String_RoundTrip_WithTwoByteHeader()
    {
        // s7netplus 官方 S7String：bytes[0]=capacity, bytes[1]=length, 数据从 index 2 开始
        var bytes = S7String.ToByteArray("ABC", 100);
        Assert.Equal(102, bytes.Length);            // 2 头 + 100 容量
        Assert.Equal(100, bytes[0]);                // capacity
        Assert.Equal(3, bytes[1]);                  // length
        Assert.Equal("ABC", S7String.FromByteArray(bytes));

        // 长度超容量会报错（S7String 校验）
        Assert.Throws<PlcException>(() => S7String.FromByteArray(new byte[] { 2, 3, (byte)'A', (byte)'B' }));
    }
}
