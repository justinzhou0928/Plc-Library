using Microsoft.Extensions.Logging;
using System;

namespace PlcLibrary.Modbus
{
    internal static partial class ModbusLog
    {
        [LoggerMessage(Level = LogLevel.Error,
        Message = "Modbus 读取失败 Address={Address}")]
        internal static partial void LogReadFailed(ILogger logger, Exception ex, string address);

        [LoggerMessage(Level = LogLevel.Error,
        Message = "Modbus 写入失败 Address={Address}")]
        internal static partial void LogWriteFailed(ILogger logger, Exception ex, string address);

        [LoggerMessage(Level = LogLevel.Error,
        Message = "Modbus 地址解析失败: {Address}")]
        internal static partial void LogAddressParseFailed(ILogger logger, string address);

        [LoggerMessage(Level = LogLevel.Error,
        Message = "Modbus 连接失败")]
        internal static partial void LogConnectFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug,
        Message = "Modbus 驱动已释放")]
        internal static partial void LogDriverDisposed(ILogger logger);
    }
}
