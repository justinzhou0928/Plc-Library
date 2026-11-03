using System;
using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.DriverPool.Models
{
    /// <summary>连接池与弹性策略配置</summary>
    public sealed class PoolOptions
    {
        /// <summary>配置节名</summary>
        public const string SectionName = "DriverPool";

        /// <summary>每设备最大连接数</summary>
        [Range(1, 100)]
        public int MaxConnectionsPerDevice { get; set; } = 2;

        /// <summary>最大重试次数</summary>
        [Range(1, 10)]
        public int MaxRetryAttempts { get; set; } = 3;

        /// <summary>重试间隔</summary>
        [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>断路器最小吞吐量</summary>
        [Range(1, 100)]
        public int CircuitBreakerMinimumThroughput { get; set; } = 5;

        /// <summary>断路器冷却时长</summary>
        [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
        public TimeSpan CircuitBreakerDuration { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>单次操作超时</summary>
        [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
        public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>断路器失败率阈值</summary>
        [Range(0.0, 1.0)]
        public double CircuitBreakerFailureRatio { get; set; } = 0.5;

        /// <summary>空闲连接池回收阈值：池空置超过该时长且无在途借用时，销毁池并释放连接
        /// <c>TimeSpan.Zero</c> 表示禁用自动回收（默认启用，10 分钟）</summary>
        [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
        public TimeSpan PoolIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);
    }
}
