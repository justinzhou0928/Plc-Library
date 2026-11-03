using System;
using PlcLibrary.DriverDomain.Enums;

namespace PlcLibrary.DriverDomain.Models
{
    /// <summary>单个点位的采集/写入结果</summary>
    public readonly record struct DriverResult
    {
        /// <summary>设备标识</summary>
        public string DeviceId { get; init; } = string.Empty;

        /// <summary>点位业务标识</summary>
        public string TagId { get; init; } = string.Empty;

        /// <summary>协议地址</summary>
        public string Address { get; init; } = string.Empty;

        /// <summary>点位值；读取失败时为 null</summary>
        public object? Value { get; init; }

        /// <summary>质量码</summary>
        public QualityCode Status { get; init; }

        /// <summary>错误信息；正常时为 null</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>采集时间（UTC，库接收/生成结果的时刻）</summary>
        public DateTime Timestamp { get; init; }

        /// <summary>设备侧源时间戳（UTC 或设备时区，由协议返回；未知为 null）</summary>
        public DateTime? SourceTimestamp { get; init; }

        /// <summary>服务器侧时间戳（UTC 或服务器时区，由协议返回；未知为 null）</summary>
        public DateTime? ServerTimestamp { get; init; }

        /// <summary>创建空结果</summary>
        public DriverResult() { }

        /// <summary>创建 Good 结果</summary>
        /// <param name="address">协议地址</param>
        /// <param name="value">点位值</param>
        public static DriverResult Good(string address, object? value)
            => new() { Address = address, Value = value, Status = QualityCode.Good, Timestamp = DateTime.UtcNow };

        /// <summary>创建 Bad 结果</summary>
        /// <param name="address">协议地址</param>
        /// <param name="status">质量码</param>
        /// <param name="error">错误信息</param>
        public static DriverResult Bad(string address, QualityCode status, string error)
            => new() { Address = address, Status = status, ErrorMessage = error, Timestamp = DateTime.UtcNow };
    }
}
