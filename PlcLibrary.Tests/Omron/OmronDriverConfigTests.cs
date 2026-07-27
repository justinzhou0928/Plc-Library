using PlcLibrary.Omron;

namespace PlcLibrary.Tests.Omron;

public class OmronDriverConfigTests
{
    [Fact]
    public void DefaultValues_WhenNewInstance()
    {
        var config = new OmronDriverConfig();
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(9600, config.Port);
        Assert.Equal(3000, config.Timeout);
        Assert.Equal(1, config.LocalNode);
        Assert.Equal(2, config.DestinyNode);
        Assert.False(config.IsUdp);
    }

    [Fact]
    public void Parse_HostPort_ReturnsCorrectValues()
    {
        var config = OmronDriverConfig.Parse("host:10.0.0.1;port:9600");
        Assert.Equal("10.0.0.1", config.Host);
        Assert.Equal(9600, config.Port);
    }

    [Fact]
    public void Parse_FullConnectionString_ParsedCorrectly()
    {
        var config = OmronDriverConfig.Parse("host:192.168.1.1;port:9600;timeout:5000;localnode:10;destinynode:20;isudp:true");
        Assert.Equal("192.168.1.1", config.Host);
        Assert.Equal(9600, config.Port);
        Assert.Equal(5000, config.Timeout);
        Assert.Equal(10, config.LocalNode);
        Assert.Equal(20, config.DestinyNode);
        Assert.True(config.IsUdp);
    }

    [Fact]
    public void Parse_UdpProtocol()
    {
        var config = OmronDriverConfig.Parse("isudp:true");
        Assert.True(config.IsUdp);
    }

    [Fact]
    public void Parse_CaseInsensitive_KeysAreNormalized()
    {
        var config = OmronDriverConfig.Parse("HOST:10.0.0.1;LOCALNODE:3;DESTINYNODE:5");
        Assert.Equal("10.0.0.1", config.Host);
        Assert.Equal(3, config.LocalNode);
        Assert.Equal(5, config.DestinyNode);
    }
}
