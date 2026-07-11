using System;
using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.Pipeline.Models
{
    public sealed class PipelineOptions
    {
        public const string SectionName = "Pipeline";

        [Range(100, 100000)]
        public int Capacity { get; set; } = 10000;

        [Range(1, 128)]
        public int MaxHandlerParallelism { get; set; } = 8;

        [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
        public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }
}
