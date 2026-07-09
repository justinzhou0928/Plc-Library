using Microsoft.Extensions.Logging;
using Moq;
using PlcLibrary.General.Configuration;
using System.ComponentModel.DataAnnotations;

namespace PlcLibrary.Tests.General;

public class DeviceConfigurationValidationTests
{
    private readonly Mock<ILogger> _logger = new();

    private static DeviceConfiguration ValidDevice() => new()
    {
        Id = "plc-01",
        Protocol = "S7",
        ConnectionString = "host:192.168.1.1;port:102",
        TagPoints = [new TagPointConfiguration { TagId = "tag1", Address = "DB1.DBD0" }]
    };

    [Fact]
    public void Validate_AllFieldsValid_ReturnsTrue()
    {
        Assert.True(ValidDevice().Validate(_logger.Object));
    }

    [Fact]
    public void Validate_EmptyId_ReturnsFalse()
    {
        var d = ValidDevice() with { Id = "" };
        Assert.False(d.Validate(_logger.Object));
    }

    [Fact]
    public void Validate_WhitespaceId_ReturnsFalse()
    {
        var d = ValidDevice() with { Id = "   " };
        Assert.False(d.Validate(_logger.Object));
    }

    [Fact]
    public void Validate_EmptyProtocol_ReturnsFalse()
    {
        var d = ValidDevice() with { Protocol = "" };
        Assert.False(d.Validate(_logger.Object));
    }

    [Fact]
    public void Validate_EmptyConnectionString_ReturnsFalse()
    {
        var d = ValidDevice() with { ConnectionString = "" };
        Assert.False(d.Validate(_logger.Object));
    }

    [Fact]
    public void Validate_ZeroCollectionInterval_ReturnsFalse()
    {
        var d = ValidDevice() with { CollectionInterval = TimeSpan.Zero };
        Assert.False(d.Validate(_logger.Object));
    }

    [Fact]
    public void Validate_NegativeCollectionInterval_ReturnsFalse()
    {
        var d = ValidDevice() with { CollectionInterval = TimeSpan.FromSeconds(-1) };
        Assert.False(d.Validate(_logger.Object));
    }

    [Fact]
    public void Validate_EmptyTagPoints_ReturnsFalse()
    {
        var d = ValidDevice() with { TagPoints = [] };
        Assert.False(d.Validate(_logger.Object));
    }

    [Fact]
    public void Validate_TagPointWithEmptyTagId_ReturnsFalse()
    {
        var d = ValidDevice() with
        {
            TagPoints = [new TagPointConfiguration { TagId = "", Address = "DB1.DBD0" }]
        };
        Assert.False(d.Validate(_logger.Object));
    }

    [Fact]
    public void Validate_TagPointWithEmptyAddress_ReturnsFalse()
    {
        var d = ValidDevice() with
        {
            TagPoints = [new TagPointConfiguration { TagId = "tag1", Address = "" }]
        };
        Assert.False(d.Validate(_logger.Object));
    }
}
