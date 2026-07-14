using Microsoft.Extensions.Logging;
using System;

namespace PlcLibrary.OpcUa
{
    internal static partial class OpcUaLog
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "OPC UA 已连接到 {Endpoint}")]
        internal static partial void LogConnected(ILogger logger, string endpoint);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "OPC UA 连接失败 {Endpoint}")]
        internal static partial void LogConnectionFailed(ILogger logger, Exception ex, string endpoint);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "OPC UA 订阅已启动，{Count} 个点位，推送间隔 {Interval}ms")]
        internal static partial void LogSubscriptionStarted(ILogger logger, int count, int interval);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "OPC UA 订阅已停止")]
        internal static partial void LogSubscriptionStopped(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "OPC UA Session 重连成功")]
        internal static partial void LogReconnected(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "OPC UA Session 重连失败")]
        internal static partial void LogReconnectFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "OPC UA 数据通知处理失败 Address={Address}")]
        internal static partial void LogNotificationFailed(ILogger logger, Exception ex, string address);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "OPC UA 单点读取失败 Address={Address}")]
        internal static partial void LogReadPointFailed(ILogger logger, Exception ex, string address);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "OPC UA 单点写入失败 Address={Address}")]
        internal static partial void LogWritePointFailed(ILogger logger, Exception ex, string address);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "OPC UA 地址格式无效，跳过: {Address}")]
        internal static partial void LogInvalidAddress(ILogger logger, string address);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "OPC UA 驱动已释放")]
        internal static partial void LogDisposed(ILogger logger);
    }
}
