using PlcLibrary.AllenBradley;

namespace PlcLibrary.Tests.AllenBradley;

public class AllenBradleyDriverConfigTests
{
    [Fact]
    public void DefaultValues_WhenNewInstance()
    {
        var config = new AllenBradleyDriverConfig();
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(44818, config.Port);
        Assert.Equal(5000, config.Timeout);
        Assert.Equal("", config.Path);
        Assert.False(config.UseConnected);
    }

    [Fact]
    public void Parse_HostPort_ReturnsCorrectValues()
    {
        var config = AllenBradleyDriverConfig.Parse("host:10.0.0.1;port:44818");
        Assert.Equal("10.0.0.1", config.Host);
        Assert.Equal(44818, config.Port);
    }

    [Fact]
    public void Parse_FullConnectionString_ParsedCorrectly()
    {
        var config = AllenBradleyDriverConfig.Parse("host:192.168.1.96;port:44818;timeout:5000;path:1,0;useconnected:true");
        Assert.Equal("192.168.1.96", config.Host);
        Assert.Equal(44818, config.Port);
        Assert.Equal(5000, config.Timeout);
        Assert.Equal("1,0", config.Path);
        Assert.True(config.UseConnected);
    }

    [Fact]
    public void Parse_ControlLogixPath()
    {
        var config = AllenBradleyDriverConfig.Parse("host:192.168.1.96;path:1,0");
        Assert.Equal("1,0", config.Path);
    }

    [Fact]
    public void Parse_CaseInsensitive_KeysAreNormalized()
    {
        var config = AllenBradleyDriverConfig.Parse("HOST:10.0.0.1;PORT:44818;PATH:1,0;USECONNECTED:true");
        Assert.Equal("10.0.0.1", config.Host);
        Assert.Equal(44818, config.Port);
        Assert.Equal("1,0", config.Path);
        Assert.True(config.UseConnected);
    }
}
