using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NModbus;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.Modbus
{
    /// <summary>Modbus UDP 驱动</summary>
    [ProtocolDriverName("Modbus_UDP")]
    public sealed class ModbusUdpDriver : ModbusDriverBase
    {
        /// <summary>创建 Modbus UDP 驱动</summary>
        /// <param name="logger">日志</param>
        /// <param name="device">设备配置</param>
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
