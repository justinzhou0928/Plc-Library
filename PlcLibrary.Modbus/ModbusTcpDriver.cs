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
            : base(logger, ModbusDriverConfig.Parse(device.ConnectionString), () => CreateMaster(device.ConnectionString)) { }

        private static IModbusMaster CreateMaster(string connectionString)
        {
            var config = ModbusDriverConfig.Parse(connectionString);
            var tcpClient = new TcpClient(config.Host, config.Port)
            {
                SendTimeout = config.Timeout,
                ReceiveTimeout = config.Timeout
            };
            return new ModbusFactory().CreateMaster(tcpClient);
        }
    }
}
