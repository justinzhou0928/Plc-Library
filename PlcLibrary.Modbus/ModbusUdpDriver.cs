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
            : base(logger, ModbusDriverConfig.Parse(device.ConnectionString), () => CreateMaster(device.ConnectionString)) { }

        private static IModbusMaster CreateMaster(string connectionString)
        {
            var config = ModbusDriverConfig.Parse(connectionString);
            var master = new ModbusFactory().CreateMaster(new UdpClient(config.Host, config.Port));
            master.Transport.ReadTimeout = config.Timeout;
            master.Transport.WriteTimeout = config.Timeout;
            return master;
        }
    }
}
