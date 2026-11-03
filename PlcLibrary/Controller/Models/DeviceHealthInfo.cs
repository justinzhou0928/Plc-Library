using System;

namespace PlcLibrary.Controller.Models
{
    /// <summary>设备运行健康状态快照</summary>
    /// <param name="DeviceId">设备标识</param>
    /// <param name="Protocol">协议名</param>
    /// <param name="IsRunning">采集任务是否在运行</param>
    /// <param name="Error">最近一次错误；正常时为 null</param>
    /// <param name="UpdatedAt">状态更新时间（UTC）</param>
    public readonly record struct DeviceHealthInfo(
        string DeviceId,
        string Protocol,
        bool IsRunning,
        string? Error,
        DateTime UpdatedAt)
    {
        /// <summary>创建健康状态</summary>
        /// <param name="deviceId">设备标识</param>
        /// <param name="protocol">协议名</param>
        public static DeviceHealthInfo Healthy(string deviceId, string protocol)
            => new(deviceId, protocol, true, null, DateTime.UtcNow);

        /// <summary>创建故障状态</summary>
        /// <param name="deviceId">设备标识</param>
        /// <param name="protocol">协议名</param>
        /// <param name="error">错误信息</param>
        public static DeviceHealthInfo Faulted(string deviceId, string protocol, string error)
            => new(deviceId, protocol, false, error, DateTime.UtcNow);
    }
}
