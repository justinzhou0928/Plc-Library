using System;
using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.DriverPool.Models
{
    public sealed class PoolOptions
    {
        public const string SectionName = "DriverPool";
        /// <summary>每设备最大连接数</summary>
        [Range(1, 100)]
        public int MaxConnectionsPerDevice { get; set; } = 2;

        /// <summary>最大重试次数</summary>
        [Range(0, 10)]
        public int MaxRetryAttempts { get; set; } = 3;

        /// <summary>重试基础延迟</summary>
        [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>断路器连续失败阈值</summary>
        [Range(1, 100)]
        public int CircuitBreakerMinimumThroughput { get; set; } = 5;

        /// <summary>断路器熔断持续时间</summary>
        [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
        public TimeSpan CircuitBreakerDuration { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>单次操作超时</summary>
        [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
        public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(10);
    }
}
