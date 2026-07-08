using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.General.Configuration
{
    public sealed record TagPointConfiguration
    {
        public bool Enabled { get; set; } = true;

        [Required]
        [MinLength(1)]
        public string TagId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        [Required]
        [MinLength(1)]
        public string Address { get; set; } = string.Empty;

        public string DataType { get; set; } = string.Empty;
    }
}
