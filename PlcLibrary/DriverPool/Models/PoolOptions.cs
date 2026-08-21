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

        /// <summary>空闲连接池回收阈值：池空置超过该时长且无在途借用时，销毁池并释放连接。
        /// <c>TimeSpan.Zero</c> 表示禁用自动回收（默认启用，10 分钟）。</summary>
        [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
        public TimeSpan PoolIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);
    }
}
