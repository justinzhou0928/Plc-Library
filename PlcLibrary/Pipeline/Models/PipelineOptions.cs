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
        public int MaxHandlerParallelism { get; set; } = Math.Max(1, Environment.ProcessorCount);
    }
}
