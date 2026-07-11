using PlcLibrary.S7;
using S7.Net;

namespace PlcLibrary.Tests.S7;

public class S7DriverConfigTests
{
    [Fact]
    public void DefaultValues_WhenNewInstance()
    {
        var config = new S7DriverConfig();
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(102, config.Port);
        Assert.Equal(3000, config.Timeout);
        Assert.Equal(0, config.Rack);
        Assert.Equal(0, config.Slot);
        Assert.Equal(CpuType.S71200, config.CpuType);
    }

    [Fact]
    public void Parse_HostPort_ReturnsCorrectValues()
    {
        var config = S7DriverConfig.Parse("host:10.0.0.1;port:502");
        Assert.Equal("10.0.0.1", config.Host);
        Assert.Equal(502, config.Port);
    }

    [Fact]
    public void Parse_FullConnectionString_ParsedCorrectly()
    {
        var config = S7DriverConfig.Parse("host:192.168.1.1;port:102;timeout:5000;rack:1;slot:2;cpu:S71500");
        Assert.Equal("192.168.1.1", config.Host);
        Assert.Equal(102, config.Port);
        Assert.Equal(5000, config.Timeout);
        Assert.Equal(1, config.Rack);
        Assert.Equal(2, config.Slot);
        Assert.Equal(CpuType.S71500, config.CpuType);
    }

    [Fact]
    public void Parse_CpuTypeCaseInsensitive_ReturnsCorrectEnum()
    {
        var config = S7DriverConfig.Parse("cpu:s71500");
        Assert.Equal(CpuType.S71500, config.CpuType);
    }

    [Fact]
    public void Parse_CaseInsensitive_KeysAreNormalized()
    {
        var config = S7DriverConfig.Parse("HOST:10.0.0.1;PORT:8080");
        Assert.Equal("10.0.0.1", config.Host);
        Assert.Equal(8080, config.Port);
    }
}
