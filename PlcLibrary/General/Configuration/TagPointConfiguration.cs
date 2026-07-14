using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.General.Configuration
{
    public sealed record TagPointConfiguration
    {
        [Required]
        [MinLength(1)]
        public string TagId { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        [Required]
        [MinLength(1)]
        public string Address { get; init; } = string.Empty;

        public string DataType { get; init; } = string.Empty;

        public int SamplingInterval { get; init; }

        public int QueueSize { get; init; } = 1;
    }
}
