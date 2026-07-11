using Microsoft.Extensions.Logging;
using NModbus;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.General.Configuration;
using System;

namespace PlcLibrary.Modbus
{
    [ProtocolDriverName("Modbus_ASCII")]
    public sealed class ModbusAsciiDriver(ILogger<ModbusAsciiDriver> logger, DeviceConfiguration device)
        : ModbusDriverBase(logger, ModbusDriverConfig.Parse(device.ConnectionString), CreateMaster(device))
    {
        private static IModbusMaster CreateMaster(DeviceConfiguration device)
        {
            throw new NotImplementedException(
                "Modbus ASCII requires a custom IModbusSerialTransport implementation. " +
                "NModbus 3.0.83 does not expose a public serial transport. " +
                "Expected in a future version.");
        }
    }
}
