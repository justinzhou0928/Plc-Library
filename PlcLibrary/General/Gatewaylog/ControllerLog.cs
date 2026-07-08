using Microsoft.Extensions.Logging;
using System;

namespace PlcLibrary.General
{
    internal static partial class ControllerLog
    {
        [LoggerMessage(Level = LogLevel.Information,
        Message = "采集调度器已停止")]
        internal static partial void LogSchedulerStopped(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information,
        Message = "设备 {DeviceId} 采集任务已启动，协议 {Protocol}，间隔 {Interval}")]
        internal static partial void LogTaskStarted(ILogger logger, string deviceId, string protocol, TimeSpan interval);

        [LoggerMessage(Level = LogLevel.Information,
        Message = "设备 {DeviceId} 采集任务已停止")]
        internal static partial void LogTaskStopped(ILogger logger, string deviceId);

        [LoggerMessage(Level = LogLevel.Error,
        Message = "设备 {DeviceId} 协议 {Protocol} 未注册，跳过")]
        internal static partial void LogProtocolUnregistered(ILogger logger, string deviceId, string protocol);

        [LoggerMessage(Level = LogLevel.Error,
        Message = "采集失败 Device={DeviceId}")]
        internal static partial void LogCollectionFailed(ILogger logger, Exception ex, string deviceId);

        [LoggerMessage(Level = LogLevel.Warning,
        Message = "设备 {DeviceId} 读取点数与期望不一致：期望 {Expected}，实际 {Actual}")]
        internal static partial void LogPointCountMismatch(ILogger logger, string deviceId, int expected, int actual);

        [LoggerMessage(Level = LogLevel.Error,
        Message = "设备 {DeviceId} 校验失败: {Error}")]
        internal static partial void LogDeviceValidationFailed(ILogger logger, string deviceId, string error);
    }
}
