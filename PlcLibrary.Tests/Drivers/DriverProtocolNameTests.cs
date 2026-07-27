using Microsoft.Extensions.DependencyInjection;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.Extensions;
using PlcLibrary.AllenBradley;
using PlcLibrary.Bacnet;
using PlcLibrary.Mitsubishi;
using PlcLibrary.Modbus;
using PlcLibrary.Omron;
using PlcLibrary.OpcUa;
using PlcLibrary.S7;
using System.Reflection;

namespace PlcLibrary.Tests.Drivers;

public class DriverProtocolNameTests
{
    [Theory]
    [InlineData(typeof(S7Driver), "S7")]
    [InlineData(typeof(ModbusTcpDriver), "Modbus_TCP")]
    [InlineData(typeof(ModbusUdpDriver), "Modbus_UDP")]
    [InlineData(typeof(ModbusRtuDriver), "Modbus_RTU")]
    [InlineData(typeof(ModbusAsciiDriver), "Modbus_ASCII")]
    [InlineData(typeof(OpcUaDriver), "OPC_UA")]
    [InlineData(typeof(MitsubishiDriver), "Mitsubishi")]
    [InlineData(typeof(OmronDriver), "Omron_FINS")]
    [InlineData(typeof(AllenBradleyDriver), "AllenBradley")]
    [InlineData(typeof(BacnetDriver), "BACnet")]
    public void ProtocolDriverNameAttribute_MatchesExpected(Type driverType, string expectedName)
    {
        var attr = driverType.GetCustomAttribute<ProtocolDriverNameAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(expectedName, attr.Name);
    }

    [Theory]
    [InlineData(typeof(S7Driver), typeof(IProtocolDriver))]
    [InlineData(typeof(ModbusTcpDriver), typeof(IProtocolDriver))]
    [InlineData(typeof(ModbusUdpDriver), typeof(IProtocolDriver))]
    [InlineData(typeof(MitsubishiDriver), typeof(IProtocolDriver))]
    [InlineData(typeof(OmronDriver), typeof(IProtocolDriver))]
    [InlineData(typeof(AllenBradleyDriver), typeof(IProtocolDriver))]
    [InlineData(typeof(BacnetDriver), typeof(IProtocolDriver))]
    public void Driver_ImplementsIProtocolDriver(Type driverType, Type interfaceType)
    {
        Assert.True(interfaceType.IsAssignableFrom(driverType),
            $"{driverType.Name} should implement {interfaceType.Name}");
    }

    [Fact]
    public void OpcUaDriver_ImplementsIPushProtocolDriver()
    {
        Assert.True(typeof(IPushProtocolDriver).IsAssignableFrom(typeof(OpcUaDriver)));
    }

    [Fact]
    public void AllDrivers_HaveDistinctProtocolNames()
    {
        var driverTypes = new[]
        {
            typeof(S7Driver), typeof(ModbusTcpDriver), typeof(ModbusUdpDriver),
            typeof(OpcUaDriver), typeof(MitsubishiDriver), typeof(OmronDriver),
            typeof(AllenBradleyDriver), typeof(BacnetDriver)
        };

        var names = driverTypes
            .Select(t => t.GetCustomAttribute<ProtocolDriverNameAttribute>()!.Name)
            .ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
