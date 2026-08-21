using Microsoft.Extensions.Logging;
using NModbus;
using NModbus.Serial;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.General.Configuration;
using System;
using System.IO.Ports;

namespace PlcLibrary.Modbus
{
    [ProtocolDriverName("Modbus_RTU")]
    public sealed class ModbusRtuDriver(ILogger<ModbusRtuDriver> logger, DeviceConfiguration device)
        : ModbusDriverBase(logger, ModbusDriverConfig.Parse(device.ConnectionString),
            () => CreateMaster(device.ConnectionString))
    {
        private static IModbusMaster CreateMaster(string connectionString)
        {
            var config = ModbusDriverConfig.Parse(connectionString);
            var serialPort = new SerialPort(config.Host)
            {
                BaudRate = config.BaudRate,
                Parity = SerialPortOptionsMapper.ParseParity(config.Parity),
                DataBits = config.DataBits,
                StopBits = SerialPortOptionsMapper.ParseStopBits(config.StopBits),
                ReadTimeout = config.Timeout,
                WriteTimeout = config.Timeout,
            };
            return new ModbusFactory().CreateRtuMaster(serialPort);
        }
    }
}
