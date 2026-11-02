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

            // 立即打开串口：NModbus 的串口适配器是惰性打开的，若不在此处打开，
            // 端口不存在/被占用只会在首次读写的 "The port is closed." 上暴露，
            // 而该异常不属于传输级异常，驱动会一直停在 Connected（连接池不会重建）。
            try
            {
                serialPort.Open();
                return new ModbusFactory().CreateRtuMaster(serialPort);
            }
            catch
            {
                serialPort.Dispose();
                throw;
            }
        }
    }
}
