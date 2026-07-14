using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General.Configuration;
using Polly.Registry;

namespace PlcLibrary.Tests.Controller;

public class GenericDriverFactoryTests
{
    private static readonly ResiliencePipelineRegistry<string> s_registry = new();
    private static readonly IOptions<PoolOptions> s_options = Options.Create(new PoolOptions());
    private static readonly ILoggerFactory s_loggerFactory = NullLoggerFactory.Instance;

    [Fact]
    public void ProtocolDriverName_ReturnsConstructorValue()
    {
        var factory = new GenericDriverFactory("S7", _ => Mock.Of<IProtocolDriver>(), cs => cs, false, s_registry, s_options, s_loggerFactory);
        Assert.Equal("S7", factory.ProtocolDriverName);
    }

    [Fact]
    public async Task CreateAsync_CallsFactoryDelegate()
    {
        var driver = Mock.Of<IProtocolDriver>();
        var factory = new GenericDriverFactory("S7", _ => driver, cs => cs, false, s_registry, s_options, s_loggerFactory);

        var result = await factory.CreateAsync(new DeviceConfiguration { Id = "d1" });

        Assert.Same(driver, result);
    }

    [Fact]
    public void GetConnectionKey_Default_UsesConnectionString()
    {
        var factory = new GenericDriverFactory("S7", _ => Mock.Of<IProtocolDriver>(), cs => cs, false, s_registry, s_options, s_loggerFactory);
        Assert.Equal("host:192.168.1.1;port:102", factory.GetConnectionKey("host:192.168.1.1;port:102"));
    }

    [Fact]
    public void GetConnectionKey_CustomDelegate_ReturnsComputedKey()
    {
        var factory = new GenericDriverFactory("S7", _ => Mock.Of<IProtocolDriver>(), cs => "key:" + cs, false, s_registry, s_options, s_loggerFactory);
        Assert.Equal("key:test", factory.GetConnectionKey("test"));
    }
}
