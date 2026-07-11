using Microsoft.Extensions.Logging;
using NModbus;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.General.Configuration;
using System.Net.Sockets;

namespace PlcLibrary.Modbus
{
    [ProtocolDriverName("Modbus_TCP")]
    public sealed class ModbusTcpDriver : ModbusDriverBase
    {
        public ModbusTcpDriver(ILogger<ModbusTcpDriver> logger, DeviceConfiguration device)
            : base(logger, ModbusDriverConfig.Parse(device.ConnectionString), CreateMaster(device)) { }

        private static IModbusMaster CreateMaster(DeviceConfiguration device)
        {
            var config = ModbusDriverConfig.Parse(device.ConnectionString);
            return new ModbusFactory().CreateMaster(new TcpClient(config.Host, config.Port));
        }
    }
}
