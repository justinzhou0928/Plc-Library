using System;
using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.Pipeline.Models
{
    /// <summary>数据管道配置</summary>
    public sealed class PipelineOptions
    {
        /// <summary>配置节名</summary>
        public const string SectionName = "Pipeline";

        /// <summary>管道容量</summary>
        [Range(100, 100000)]
        public int Capacity { get; set; } = 10_000;

        /// <summary>handler 最大并行度</summary>
        [Range(1, 128)]
        public int MaxHandlerParallelism { get; set; } = 8;

        /// <summary>单个 handler 超时</summary>
        [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
        public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>写入管道队列的等待上限；消费者长时间不前进时丢弃该结果，避免采集循环被反压永久卡住
        /// <c>TimeSpan.Zero</c> 表示不设上限</summary>
        [Range(typeof(TimeSpan), "00:00:00", "00:05:00")]
        public TimeSpan EnqueueTimeout { get; set; } = TimeSpan.FromSeconds(5);
    }
}
