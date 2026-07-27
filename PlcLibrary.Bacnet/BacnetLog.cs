using Microsoft.Extensions.Logging;
using System;

namespace PlcLibrary.Bacnet
{
    internal static partial class BacnetLog
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "BACnet 已连接到 {Host}:{Port}")]
        internal static partial void LogConnected(ILogger logger, string host, int port);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "BACnet 连接失败 {Host}:{Port}")]
        internal static partial void LogConnectionFailed(ILogger logger, Exception ex, string host, int port);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "BACnet 读取失败")]
        internal static partial void LogReadFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "BACnet 写入失败")]
        internal static partial void LogWriteFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "BACnet 点读取失败 Address={Address}")]
        internal static partial void LogReadPointFailed(ILogger logger, Exception ex, string address);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "BACnet 点写入失败 Address={Address}")]
        internal static partial void LogWritePointFailed(ILogger logger, Exception ex, string address);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "BACnet 重连成功")]
        internal static partial void LogReconnected(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "BACnet 重连失败")]
        internal static partial void LogReconnectFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "BACnet 驱动已释放")]
        internal static partial void LogDisposed(ILogger logger);
    }
}
