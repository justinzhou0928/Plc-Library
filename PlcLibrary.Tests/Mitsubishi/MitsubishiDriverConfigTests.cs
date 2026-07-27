using PlcLibrary.Mitsubishi;

namespace PlcLibrary.Tests.Mitsubishi;

public class MitsubishiDriverConfigTests
{
    [Fact]
    public void DefaultValues_WhenNewInstance()
    {
        var config = new MitsubishiDriverConfig();
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(6000, config.Port);
        Assert.Equal(3000, config.Timeout);
        Assert.Equal("MC", config.ProtocolType);
    }

    [Fact]
    public void Parse_HostPort_ReturnsCorrectValues()
    {
        var config = MitsubishiDriverConfig.Parse("host:10.0.0.1;port:6000");
        Assert.Equal("10.0.0.1", config.Host);
        Assert.Equal(6000, config.Port);
    }

    [Fact]
    public void Parse_FullConnectionString_ParsedCorrectly()
    {
        var config = MitsubishiDriverConfig.Parse("host:192.168.1.1;port:6000;timeout:5000;protocoltype:MC");
        Assert.Equal("192.168.1.1", config.Host);
        Assert.Equal(6000, config.Port);
        Assert.Equal(5000, config.Timeout);
        Assert.Equal("MC", config.ProtocolType);
    }

    [Fact]
    public void Parse_DifferentProtocolTypes()
    {
        var a1e = MitsubishiDriverConfig.Parse("protocoltype:A1E");
        Assert.Equal("A1E", a1e.ProtocolType);

        var fx = MitsubishiDriverConfig.Parse("protocoltype:FX");
        Assert.Equal("FX", fx.ProtocolType);
    }

    [Fact]
    public void Parse_CaseInsensitive_KeysAreNormalized()
    {
        var config = MitsubishiDriverConfig.Parse("HOST:10.0.0.1;PORT:6000;PROTOCOLTYPE:MC");
        Assert.Equal("10.0.0.1", config.Host);
        Assert.Equal(6000, config.Port);
        Assert.Equal("MC", config.ProtocolType);
    }
}
