using PlcLibrary.OpcUa;

namespace PlcLibrary.Tests.OpcUa;

public class OpcUaDriverConfigTests
{
    [Fact]
    public void DefaultValues_WhenNewInstance()
    {
        var config = new OpcUaDriverConfig();
        Assert.Equal("opc.tcp://localhost:4840", config.Endpoint);
        Assert.Null(config.UserName);
        Assert.Equal("None", config.Security);
        Assert.Equal(5000, config.Timeout);
        Assert.Equal(1000, config.PublishingInterval);
        Assert.Equal(60000, config.SessionTimeout);
        Assert.False(config.AutoAcceptCertificate);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsDefaults()
    {
        var config = OpcUaDriverConfig.Parse("endpoint:opc.tcp://localhost:4840");
        Assert.Equal("opc.tcp://localhost:4840", config.Endpoint);
    }

    [Fact]
    public void Parse_Endpoint_FromConnectionString()
    {
        var config = OpcUaDriverConfig.Parse("endpoint:opc.tcp://192.168.1.1:4840;timeout:10000");
        Assert.Equal("opc.tcp://192.168.1.1:4840", config.Endpoint);
        Assert.Equal(10000, config.Timeout);
    }

    [Fact]
    public void Parse_UserCredentials()
    {
        var config = OpcUaDriverConfig.Parse("username:admin;password:secret");
        Assert.Equal("admin", config.UserName);
        Assert.Equal("secret", config.Password);
    }

    [Fact]
    public void Parse_SecuritySettings()
    {
        var config = OpcUaDriverConfig.Parse("security:Sign;autoacceptcertificate:false;publishinginterval:2000");
        Assert.Equal("Sign", config.Security);
        Assert.False(config.AutoAcceptCertificate);
        Assert.Equal(2000, config.PublishingInterval);
    }

    [Fact]
    public void Parse_FullConnectionString()
    {
        var config = OpcUaDriverConfig.Parse(
            "endpoint:opc.tcp://10.0.0.1:4840;username:opcuser;password:p@ss;security:SignAndEncrypt;timeout:8000;publishinginterval:500;sessiontimeout:30000;autoacceptcertificate:false");
        Assert.Equal("opc.tcp://10.0.0.1:4840", config.Endpoint);
        Assert.Equal("opcuser", config.UserName);
        Assert.Equal("p@ss", config.Password);
        Assert.Equal("SignAndEncrypt", config.Security);
        Assert.Equal(8000, config.Timeout);
        Assert.Equal(500, config.PublishingInterval);
        Assert.Equal(30000, config.SessionTimeout);
        Assert.False(config.AutoAcceptCertificate);
    }
}
