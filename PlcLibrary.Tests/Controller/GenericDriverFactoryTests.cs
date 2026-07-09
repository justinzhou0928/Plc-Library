using Moq;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.Tests.Controller;

public class GenericDriverFactoryTests
{
    [Fact]
    public void ProtocolDriverName_ReturnsConstructorValue()
    {
        var factory = new GenericDriverFactory("S7", _ => Mock.Of<IProtocolDriver>(), cs => cs);
        Assert.Equal("S7", factory.ProtocolDriverName);
    }

    [Fact]
    public async Task CreateAsync_CallsFactoryDelegate()
    {
        var driver = Mock.Of<IProtocolDriver>();
        var factory = new GenericDriverFactory("S7", _ => driver, cs => cs);

        var result = await factory.CreateAsync(new DeviceConfiguration { Id = "d1" });

        Assert.Same(driver, result);
    }

    [Fact]
    public void GetConnectionKey_Default_UsesConnectionString()
    {
        var factory = new GenericDriverFactory("S7", _ => Mock.Of<IProtocolDriver>(), cs => cs);
        Assert.Equal("host:192.168.1.1;port:102", factory.GetConnectionKey("host:192.168.1.1;port:102"));
    }

    [Fact]
    public void GetConnectionKey_CustomDelegate_ReturnsComputedKey()
    {
        var factory = new GenericDriverFactory("S7", _ => Mock.Of<IProtocolDriver>(), cs => "key:" + cs);
        Assert.Equal("key:test", factory.GetConnectionKey("test"));
    }
}
