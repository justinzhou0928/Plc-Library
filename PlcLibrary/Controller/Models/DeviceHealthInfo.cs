using System;

namespace PlcLibrary.Controller.Models
{
    public readonly record struct DeviceHealthInfo(
        string DeviceId,
        string Protocol,
        bool IsRunning,
        string? Error,
        DateTime UpdatedAt)
    {
        public static DeviceHealthInfo Healthy(string deviceId, string protocol)
            => new(deviceId, protocol, true, null, DateTime.UtcNow);

        public static DeviceHealthInfo Faulted(string deviceId, string protocol, string error)
            => new(deviceId, protocol, false, error, DateTime.UtcNow);
    }
}
