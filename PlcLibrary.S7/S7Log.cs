using Microsoft.Extensions.Logging;
using System;

namespace PlcLibrary.S7
{
    internal static partial class S7Log
    {
        [LoggerMessage(Level = LogLevel.Warning,
        Message = "S7 批量写入失败，降级逐个写入")]
        internal static partial void LogBatchWriteFallback(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning,
        Message = "S7 批量读取失败，降级逐个读取")]
        internal static partial void LogBatchReadFallback(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning,
        Message = "S7 地址解析失败: {Address}")]
        internal static partial void LogAddressParseFailed(ILogger logger, string address);

        [LoggerMessage(Level = LogLevel.Debug,
        Message = "S7 点读取失败 Address={Address}")]
        internal static partial void LogReadPointFailed(ILogger logger, Exception ex, string address);

        [LoggerMessage(Level = LogLevel.Debug,
        Message = "S7 点写入失败 Address={Address}")]
        internal static partial void LogWritePointFailed(ILogger logger, Exception ex, string address);

        [LoggerMessage(Level = LogLevel.Error,
        Message = "S7 连接失败 Host={Host} Port={Port}")]
        internal static partial void LogConnectionFailed(ILogger logger, Exception ex, string host, int port);

        [LoggerMessage(Level = LogLevel.Information,
        Message = "S7 重连成功 Host={Host}")]
        internal static partial void LogReconnected(ILogger logger, string host);

        [LoggerMessage(Level = LogLevel.Error,
        Message = "S7 重连失败 Host={Host}")]
        internal static partial void LogReconnectFailed(ILogger logger, Exception ex, string host);
    }
}
