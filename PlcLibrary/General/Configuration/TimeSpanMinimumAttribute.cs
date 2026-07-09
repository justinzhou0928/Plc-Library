using System;
using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.General.Configuration
{
    [AttributeUsage(AttributeTargets.Property)]
    internal sealed class TimeSpanMinimumAttribute(string minimum) : ValidationAttribute
    {
        private readonly TimeSpan _minimum = TimeSpan.Parse(minimum);

        protected override ValidationResult? IsValid(object? value, ValidationContext ctx)
        {
            if (value is TimeSpan ts && ts < _minimum)
                return new ValidationResult($"{ctx.DisplayName} 不能小于 {_minimum}");
            return ValidationResult.Success;
        }
    }
}
