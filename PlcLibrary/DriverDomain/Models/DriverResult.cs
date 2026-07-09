using PlcLibrary.DriverDomain.Enums;
using System;

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
        public DateTime Timestamp { get; init; }

        public DriverResult() { }

        public static DriverResult Good(string address, object? value)
            => new() { Address = address, Value = value, Status = QualityCode.Good, Timestamp = DateTime.UtcNow };

        public static DriverResult Bad(string address, QualityCode status, string error)
            => new() { Address = address, Status = status, ErrorMessage = error, Timestamp = DateTime.UtcNow };
    }
}
