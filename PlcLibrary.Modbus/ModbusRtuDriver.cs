using Microsoft.Extensions.Logging;
using NModbus;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.General.Configuration;
using System;

namespace PlcLibrary.Modbus
{
    [ProtocolDriverName("Modbus_RTU")]
    public sealed class ModbusRtuDriver(ILogger<ModbusRtuDriver> logger, DeviceConfiguration device)
        : ModbusDriverBase(logger, ModbusDriverConfig.Parse(device.ConnectionString), CreateMaster(device))
    {
        private static IModbusMaster CreateMaster(DeviceConfiguration device)
        {
            throw new NotImplementedException(
                "Modbus RTU requires a custom IModbusSerialTransport implementation. " +
                "NModbus 3.0.83 does not expose a public serial transport. " +
                "Expected in a future version.");
        }
    }
}
