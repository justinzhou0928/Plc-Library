using System;
using Microsoft.Extensions.Logging;

namespace PlcLibrary.General
{
    internal static partial class PipelineLog
    {
        [LoggerMessage(Level = LogLevel.Information,
        Message = "已注册 {Count} 个采集结果处理器")]
        internal static partial void LogHandlersRegistered(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Error,
        Message = "Handler {HandlerType} 处理失败")]
        internal static partial void LogHandlerFailed(ILogger logger, Exception ex, string handlerType);

        [LoggerMessage(Level = LogLevel.Information,
        Message = "数据通道已停止")]
        internal static partial void LogPipelineStopped(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error,
        Message = "分发循环异常退出")]
        internal static partial void LogFanoutFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning,
        Message = "管道写入超时（{Timeout}），数据点已丢弃。Address={Address}")]
        internal static partial void LogEnqueueTimedOut(ILogger logger, string address, TimeSpan timeout);
    }
}
