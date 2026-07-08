using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.General.Configuration
{
    public sealed record DeviceConfiguration
    {
        public bool Enabled { get; set; } = true;

        [Required]
        [MinLength(1)]
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        [Required]
        [MinLength(1)]
        public string Protocol { get; set; } = string.Empty;

        [Required]
        [MinLength(1)]
        public string ConnectionString { get; set; } = string.Empty;

        public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(5);

        [TimeSpanMinimum("00:00:00.001")]
        public TimeSpan CollectionInterval { get; set; } = TimeSpan.FromSeconds(1);

        [MinLength(1)]
        public TagPointConfiguration[] TagPoints { get; set; } = [];

        public bool Validate(ILogger logger)
        {
            var results = new List<ValidationResult>();
            if (Validator.TryValidateObject(this, new ValidationContext(this), results, true))
                return true;

            foreach (var r in results)
                ControllerLog.LogDeviceValidationFailed(logger, Id, r.ErrorMessage ?? "");
            return false;
        }
    }
}
