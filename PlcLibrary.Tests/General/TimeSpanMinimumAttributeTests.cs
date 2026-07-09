using System.ComponentModel.DataAnnotations;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.Tests.General;

public class TimeSpanMinimumAttributeTests
{
    [Fact]
    public void IsValid_ValueGreaterThanMinimum_ReturnsSuccess()
    {
        var attr = new TimeSpanMinimumAttribute("00:00:01");
        Assert.Null(attr.GetValidationResult(TimeSpan.FromSeconds(2), new ValidationContext(new object())));
    }

    [Fact]
    public void IsValid_ValueEqualsMinimum_ReturnsSuccess()
    {
        var attr = new TimeSpanMinimumAttribute("00:00:01");
        Assert.Null(attr.GetValidationResult(TimeSpan.FromSeconds(1), new ValidationContext(new object())));
    }

    [Fact]
    public void IsValid_ValueLessThanMinimum_ReturnsError()
    {
        var attr = new TimeSpanMinimumAttribute("00:00:01");
        var result = attr.GetValidationResult(TimeSpan.FromMilliseconds(500), new ValidationContext(new object()));
        Assert.NotNull(result);
    }

    [Fact]
    public void IsValid_NonTimeSpanValue_ReturnsSuccess()
    {
        var attr = new TimeSpanMinimumAttribute("00:00:01");
        Assert.Null(attr.GetValidationResult("not a timespan", new ValidationContext(new object())));
    }
}
