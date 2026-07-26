using Microsoft.Extensions.DependencyInjection;
using Moq;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.Tests.Controller;

public class GenericDriverFactoryTests
{
    private GenericDriverFactory CreateFactory(string name, bool supportsPush = false) =>
        new(name, _ => Mock.Of<IProtocolDriver>(), supportsPush);

    [Fact]
    public void ProtocolDriverName_ReturnsConstructorValue()
    {
        var factory = CreateFactory("S7");
        Assert.Equal("S7", factory.ProtocolDriverName);
    }

    [Fact]
    public async Task CreateAsync_CallsFactoryDelegate()
    {
        var driver = Mock.Of<IProtocolDriver>();
        var factory = new GenericDriverFactory("S7", _ => driver, false);

        var result = await factory.CreateAsync(new DeviceConfiguration { Id = "d1", Protocol = "S7", ConnectionString = "host:127.0.0.1" });

        Assert.Same(driver, result);
    }

    [Fact]
    public void SupportsPush_ReturnsConstructorValue()
    {
        var factory = CreateFactory("OPC", supportsPush: true);
        Assert.True(factory.SupportsPush);
    }
}
