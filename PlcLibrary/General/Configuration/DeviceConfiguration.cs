using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.General.Configuration
{
    public sealed record DeviceConfiguration
    {
        public bool Enabled { get; init; } = true;

        [Required]
        [MinLength(1)]
        public required string Id { get; init; }

        public string Name { get; init; } = string.Empty;

        [Required]
        [MinLength(1)]
        public required string Protocol { get; init; }

        [Required]
        [MinLength(1)]
        public required string ConnectionString { get; init; }

        public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(5);

        [TimeSpanMinimum("00:00:00.001")]
        public TimeSpan CollectionInterval { get; init; } = TimeSpan.FromSeconds(1);

        [MinLength(1)]
        public TagPointConfiguration[] TagPoints { get; init; } = [];

        public bool Validate(out IReadOnlyList<ValidationResult> errors)
        {
            List<ValidationResult> list = [];
            Validator.TryValidateObject(this, new ValidationContext(this), list, true);

            foreach (var tag in TagPoints)
            {
                List<ValidationResult> tagResults = [];
                if (!Validator.TryValidateObject(tag, new ValidationContext(tag), tagResults, true))
                    list.AddRange(tagResults);
            }

            errors = list;
            return list.Count == 0;
        }
    }
}
