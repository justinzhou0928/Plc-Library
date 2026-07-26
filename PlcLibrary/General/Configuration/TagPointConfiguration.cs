using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.General.Configuration
{
    public sealed record TagPointConfiguration
    {
        [Required]
        [MinLength(1)]
        public required string TagId { get; init; }

        public string Name { get; init; } = string.Empty;

        [Required]
        [MinLength(1)]
        public required string Address { get; init; }

        public string DataType { get; init; } = string.Empty;

        public int SamplingInterval { get; init; }

        public int QueueSize { get; init; } = 1;
    }
}
