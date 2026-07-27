using PlcLibrary.Bacnet;

namespace PlcLibrary.Tests.Bacnet;

public class BacnetDriverConfigTests
{
    [Fact]
    public void DefaultValues_WhenNewInstance()
    {
        var config = new BacnetDriverConfig();
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(47808, config.Port);
        Assert.Equal(5000, config.Timeout);
        Assert.Equal(0u, config.DeviceInstance);
        Assert.Equal("", config.LocalEndpointIp);
    }

    [Fact]
    public void Parse_HostPort_ReturnsCorrectValues()
    {
        var config = BacnetDriverConfig.Parse("host:10.0.0.1;port:47808");
        Assert.Equal("10.0.0.1", config.Host);
        Assert.Equal(47808, config.Port);
    }

    [Fact]
    public void Parse_FullConnectionString_ParsedCorrectly()
    {
        var config = BacnetDriverConfig.Parse("host:192.168.1.50;port:47808;timeout:10000;deviceinstance:12345;localendpointip:192.168.1.100");
        Assert.Equal("192.168.1.50", config.Host);
        Assert.Equal(47808, config.Port);
        Assert.Equal(10000, config.Timeout);
        Assert.Equal(12345u, config.DeviceInstance);
        Assert.Equal("192.168.1.100", config.LocalEndpointIp);
    }

    [Fact]
    public void Parse_DeviceInstance()
    {
        var config = BacnetDriverConfig.Parse("host:10.0.0.1;deviceinstance:9999");
        Assert.Equal(9999u, config.DeviceInstance);
    }

    [Fact]
    public void Parse_CaseInsensitive_KeysAreNormalized()
    {
        var config = BacnetDriverConfig.Parse("HOST:10.0.0.1;PORT:47808;DEVICEINSTANCE:42");
        Assert.Equal("10.0.0.1", config.Host);
        Assert.Equal(47808, config.Port);
        Assert.Equal(42u, config.DeviceInstance);
    }
}
