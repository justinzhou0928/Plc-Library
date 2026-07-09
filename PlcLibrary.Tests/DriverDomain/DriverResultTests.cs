using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Models;

namespace PlcLibrary.Tests.DriverDomain;

public class DriverResultTests
{
    [Fact]
    public void Good_SetsAddressValueAndStatus()
    {
        var r = DriverResult.Good("DB1.DBD0", 42);
        Assert.Equal("DB1.DBD0", r.Address);
        Assert.Equal(42, r.Value);
        Assert.Equal(QualityCode.Good, r.Status);
        Assert.NotEqual(default, r.Timestamp);
        Assert.Null(r.ErrorMessage);
    }

    [Fact]
    public void Bad_SetsAddressStatusAndError()
    {
        var r = DriverResult.Bad("DB1.DBD0", QualityCode.BadCommFailure, "timeout");
        Assert.Equal("DB1.DBD0", r.Address);
        Assert.Equal(QualityCode.BadCommFailure, r.Status);
        Assert.Equal("timeout", r.ErrorMessage);
        Assert.Null(r.Value);
        Assert.NotEqual(default, r.Timestamp);
    }

    [Fact]
    public void Good_WithNullValue_StillSucceeds()
    {
        var r = DriverResult.Good("DB1.DBD0", null);
        Assert.Equal(QualityCode.Good, r.Status);
        Assert.Null(r.Value);
    }
}
