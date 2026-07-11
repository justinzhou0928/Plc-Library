using Microsoft.Extensions.Logging;
using NModbus;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.General.Configuration;
using System.Net.Sockets;

namespace PlcLibrary.Modbus
{
    [ProtocolDriverName("Modbus_UDP")]
    public sealed class ModbusUdpDriver : ModbusDriverBase
    {
        public ModbusUdpDriver(ILogger<ModbusUdpDriver> logger, DeviceConfiguration device)
            : base(logger, ModbusDriverConfig.Parse(device.ConnectionString), CreateMaster(device)) { }

        private static IModbusMaster CreateMaster(DeviceConfiguration device)
        {
            var config = ModbusDriverConfig.Parse(device.ConnectionString);
            return new ModbusFactory().CreateMaster(new UdpClient(config.Host, config.Port));
        }
    }
}
