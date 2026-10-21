using PlcLibrary.DriverDomain.Parser;

namespace PlcLibrary.Tests.DriverDomain;

public class DataTypeMapperTests
{
    [Theory]
    [InlineData("int32", typeof(int))]
    [InlineData("dint", typeof(int))]
    [InlineData("real", typeof(float))]
    [InlineData("lreal", typeof(double))]
    [InlineData("word", typeof(ushort))]
    [InlineData("dword", typeof(uint))]
    [InlineData("lword", typeof(ulong))]
    [InlineData("System.Single", typeof(float))]
    public void TryResolve_KnownAliases(string dataType, Type expected)
    {
        Assert.True(DataTypeMapper.TryResolve(dataType, out var type));
        Assert.Equal(expected, type);
    }

    [Fact]
    public void TryResolve_Unknown_ReturnsFalse()
    {
        Assert.False(DataTypeMapper.TryResolve("float32", out _));
    }

    [Fact]
    public void Resolve_Unknown_FallsBackToInt()
    {
        Assert.Equal(typeof(int), DataTypeMapper.Resolve("float32"));
    }

    [Fact]
    public void Resolve_Empty_ReturnsInt()
    {
        Assert.Equal(typeof(int), DataTypeMapper.Resolve(""));
    }
}
