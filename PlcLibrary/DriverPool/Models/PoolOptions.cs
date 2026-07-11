using System;
using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.DriverPool.Models
{
    public sealed class PoolOptions
    {
        public const string SectionName = "DriverPool";

        [Range(1, 100)]
        public int MaxConnectionsPerDevice { get; set; } = 2;

        [Range(0, 10)]
        public int MaxRetryAttempts { get; set; } = 3;

        [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

        [Range(1, 100)]
        public int CircuitBreakerMinimumThroughput { get; set; } = 5;

        [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
        public TimeSpan CircuitBreakerDuration { get; set; } = TimeSpan.FromSeconds(30);

        [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
        public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(10);

        [Range(0.0, 1.0)]
        public double CircuitBreakerFailureRatio { get; set; } = 0.5;
    }
}
