using System;
using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.Pipeline.Models
{
    public sealed class PipelineOptions
    {
        public const string SectionName = "Pipeline";

        [Range(100, 100000)]
        public int Capacity { get; set; } = 10_000;

        [Range(1, 128)]
        public int MaxHandlerParallelism { get; set; } = 8;

        [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
        public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>写入管道队列的等待上限；消费者长时间不前进时丢弃该结果，避免采集循环被反压永久卡住。
        /// <c>TimeSpan.Zero</c> 表示不设上限（保持旧行为）。</summary>
        [Range(typeof(TimeSpan), "00:00:00", "00:05:00")]
        public TimeSpan EnqueueTimeout { get; set; } = TimeSpan.FromSeconds(5);
    }
}
