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
    }
}
