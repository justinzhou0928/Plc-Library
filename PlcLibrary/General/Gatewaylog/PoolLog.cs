using Microsoft.Extensions.Logging;
using System;

namespace PlcLibrary.General
{
    internal static partial class PoolLog
    {
        [LoggerMessage(Level = LogLevel.Debug,
        Message = "正在为设备 {DeviceId} 创建驱动，协议 {Protocol}")]
        internal static partial void LogCreatingDriver(ILogger logger, string deviceId, string protocol);

        [LoggerMessage(Level = LogLevel.Debug,
        Message = "复用空闲驱动，设备 {DeviceId}，空闲数 {IdleCount}")]
        internal static partial void LogReusingDriver(ILogger logger, string deviceId, int idleCount);

        [LoggerMessage(Level = LogLevel.Information,
        Message = "驱动状态 {State}，尝试连接，设备 {DeviceId}")]
        internal static partial void LogConnectingDriver(ILogger logger, string deviceId, byte state);

        [LoggerMessage(Level = LogLevel.Debug,
        Message = "驱动已归还连接池，设备 {DeviceId}，空闲 {Idle}")]
        internal static partial void LogDriverReturned(ILogger logger, string deviceId, int idle);

        [LoggerMessage(Level = LogLevel.Error,
        Message = "获取驱动失败，设备 {DeviceId}")]
        internal static partial void LogAcquireFailed(ILogger logger, Exception ex, string deviceId);
    }
}
