using Microsoft.Extensions.DependencyInjection;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.Extensions;
using PlcLibrary.AllenBradley;
using PlcLibrary.Bacnet;
using PlcLibrary.Mitsubishi;
using PlcLibrary.Omron;
using PlcLibrary.S7;
using PlcLibrary.Modbus;
using PlcLibrary.OpcUa;

namespace PlcLibrary.Tests.Drivers;

public class DriverRegistrationTests
{
    private static IServiceCollection CreateServices() =>
        new ServiceCollection().AddPlcLibrary();

    [Fact]
    public void AddDriver_S7Driver_RegistersFactory()
    {
        var services = CreateServices();
        services.AddDriver<S7Driver>();
        var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IDriverFactory>();
        Assert.Contains(factories, f => f.ProtocolDriverName == "S7");
    }

    [Fact]
    public void AddDriver_ModbusTcpDriver_RegistersFactory()
    {
        var services = CreateServices();
        services.AddDriver<ModbusTcpDriver>();
        var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IDriverFactory>();
        Assert.Contains(factories, f => f.ProtocolDriverName == "Modbus_TCP");
    }

    [Fact]
    public void AddDriver_MitsubishiDriver_RegistersFactory()
    {
        var services = CreateServices();
        services.AddDriver<MitsubishiDriver>();
        var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IDriverFactory>();
        var factory = factories.First(f => f.ProtocolDriverName == "Mitsubishi");
        Assert.False(factory.SupportsPush);
    }

    [Fact]
    public void AddDriver_OmronDriver_RegistersFactory()
    {
        var services = CreateServices();
        services.AddDriver<OmronDriver>();
        var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IDriverFactory>();
        var factory = factories.First(f => f.ProtocolDriverName == "Omron_FINS");
        Assert.False(factory.SupportsPush);
    }

    [Fact]
    public void AddDriver_AllenBradleyDriver_RegistersFactory()
    {
        var services = CreateServices();
        services.AddDriver<AllenBradleyDriver>();
        var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IDriverFactory>();
        var factory = factories.First(f => f.ProtocolDriverName == "AllenBradley");
        Assert.False(factory.SupportsPush);
    }

    [Fact]
    public void AddDriver_BacnetDriver_RegistersFactory()
    {
        var services = CreateServices();
        services.AddDriver<BacnetDriver>();
        var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IDriverFactory>();
        var factory = factories.First(f => f.ProtocolDriverName == "BACnet");
        Assert.False(factory.SupportsPush);
    }

    [Fact]
    public void AddDriver_OpcUaDriver_RegistersPushFactory()
    {
        var services = CreateServices();
        services.AddDriver<OpcUaDriver>();
        var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IDriverFactory>();
        var factory = factories.First(f => f.ProtocolDriverName == "OPC_UA");
        Assert.True(factory.SupportsPush);
    }

    [Fact]
    public void AddDriver_MultipleDrivers_AllRegistered()
    {
        var services = CreateServices();
        services.AddDriver<S7Driver>();
        services.AddDriver<ModbusTcpDriver>();
        services.AddDriver<MitsubishiDriver>();
        services.AddDriver<OmronDriver>();
        services.AddDriver<AllenBradleyDriver>();
        services.AddDriver<BacnetDriver>();

        var provider = services.BuildServiceProvider();
        var factories = provider.GetServices<IDriverFactory>().ToList();

        var names = factories.Select(f => f.ProtocolDriverName).ToList();
        Assert.Contains("S7", names);
        Assert.Contains("Modbus_TCP", names);
        Assert.Contains("Mitsubishi", names);
        Assert.Contains("Omron_FINS", names);
        Assert.Contains("AllenBradley", names);
        Assert.Contains("BACnet", names);
    }

    [Fact]
    public void AddDriver_FactoryCreatesDriver()
    {
        var services = CreateServices();
        services.AddDriver<S7Driver>();
        var provider = services.BuildServiceProvider();
        var factory = provider.GetServices<IDriverFactory>().First(f => f.ProtocolDriverName == "S7");

        Assert.NotNull(factory);
    }
}
