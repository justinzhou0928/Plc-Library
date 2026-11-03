using System;
using PlcLibrary.DriverDomain.Enums;

namespace PlcLibrary.DriverDomain.Models
{
    public readonly record struct DriverResult
    {
        public string DeviceId { get; init; } = string.Empty;
        public string TagId { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public object? Value { get; init; }
        public QualityCode Status { get; init; }
        public string? ErrorMessage { get; init; }
        /// <summary>采集时间（UTC，库接收/生成结果的时刻）。</summary>
        public DateTime Timestamp { get; init; }
        /// <summary>设备侧源时间戳（UTC 或设备时区，由协议返回；未知为 null）。</summary>
        public DateTime? SourceTimestamp { get; init; }
        /// <summary>服务器侧时间戳（UTC 或服务器时区，由协议返回；未知为 null）。</summary>
        public DateTime? ServerTimestamp { get; init; }

        public DriverResult() { }

        public static DriverResult Good(string address, object? value)
            => new() { Address = address, Value = value, Status = QualityCode.Good, Timestamp = DateTime.UtcNow };

        public static DriverResult Bad(string address, QualityCode status, string error)
            => new() { Address = address, Status = status, ErrorMessage = error, Timestamp = DateTime.UtcNow };
    }
}
