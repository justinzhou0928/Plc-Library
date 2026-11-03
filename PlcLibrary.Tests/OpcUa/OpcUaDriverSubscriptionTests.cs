using Opc.Ua;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.General.Configuration;
using PlcLibrary.OpcUa;

namespace PlcLibrary.Tests.OpcUa;

/// <summary>
/// 覆盖 OPC UA 1.5.x 的 Subscription/MonitoredItem 构造重载：
/// 传 null! 时不能再命中“从 template 复制”的重载，否则运行时抛 ArgumentNullException。
/// </summary>
public class OpcUaDriverSubscriptionTests
{
    [Fact]
    public void CreateSubscription_UsesConfiguredOptions()
    {
        var config = new OpcUaDriverConfig { PublishingInterval = 2500 };

        using var subscription = OpcUaDriver.CreateSubscription(config);

        Assert.Equal(2500, subscription.PublishingInterval);
        Assert.Equal(10u, subscription.KeepAliveCount);
        Assert.Equal(20u, subscription.LifetimeCount);
        Assert.Equal(1000u, subscription.MaxNotificationsPerPublish);
        Assert.Equal((byte)0, subscription.Priority);
        Assert.True(subscription.PublishingEnabled);
    }

    [Fact]
    public void CreateMonitoredItem_UsesPointOptions()
    {
        var point = new TagPointConfiguration
        {
            TagId = "t1",
            Address = "ns=2;s=Tag",
            SamplingInterval = 250,
            QueueSize = 3,
        };
        var nodeId = NodeId.Parse(point.Address);

        var item = OpcUaDriver.CreateMonitoredItem(point, nodeId, 1000);

        Assert.Equal("t1", item.DisplayName);
        Assert.Equal(nodeId, item.StartNodeId);
        Assert.Equal(Attributes.Value, item.AttributeId);
        Assert.Equal(250, item.SamplingInterval);
        Assert.Equal(3u, item.QueueSize);
        Assert.True(item.DiscardOldest);
    }

    [Fact]
    public void CreatePushResult_BadStatusCode_ReturnsBadQuality()
    {
        // 订阅通知可能带 Bad StatusCode 但 Value 非 null；不能当成 Good。
        var value = new DataValue(42, StatusCodes.BadSensorFailure, DateTime.UtcNow);

        var result = OpcUaDriver.CreatePushResult("ns=2;s=Tag", value);

        Assert.Equal(QualityCode.BadCommFailure, result.Status);
        Assert.Null(result.Value);
        Assert.Contains("BadSensorFailure", result.ErrorMessage);
    }

    [Fact]
    public void CreatePushResult_GoodStatusCode_ReturnsValue()
    {
        var value = new DataValue(42, StatusCodes.Good, DateTime.UtcNow);

        var result = OpcUaDriver.CreatePushResult("ns=2;s=Tag", value);

        Assert.Equal(QualityCode.Good, result.Status);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void CreatePushResult_CopiesSourceAndServerTimestamps()
    {
        var source = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var server = new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        var value = new DataValue(42, StatusCodes.Good, source)
        {
            ServerTimestamp = server,
        };

        var result = OpcUaDriver.CreatePushResult("ns=2;s=Tag", value);

        Assert.Equal(source, result.SourceTimestamp);
        Assert.Equal(server, result.ServerTimestamp);
    }

    [Fact]
    public void CreateMonitoredItem_WithoutSamplingInterval_UsesPublishingInterval()
    {
        var point = new TagPointConfiguration { TagId = "t1", Address = "ns=2;s=Tag" };

        var item = OpcUaDriver.CreateMonitoredItem(point, NodeId.Parse(point.Address), 1500);

        Assert.Equal(1500, item.SamplingInterval);
        Assert.Equal(1u, item.QueueSize);
    }

    [Fact]
    public void TryCreateWriteValue_SupportedValue_ReturnsWriteValue()
    {
        var point = new TagPointConfiguration { TagId = "t1", Address = "ns=2;s=Tag" };

        Assert.True(OpcUaDriver.TryCreateWriteValue(point, 42, out var writeValue, out var error));

        Assert.Null(error);
        Assert.NotNull(writeValue);
        Assert.Equal(NodeId.Parse(point.Address), writeValue.NodeId);
        Assert.Equal(Attributes.Value, writeValue.AttributeId);
        Assert.NotNull(writeValue.Value);
    }

    [Fact]
    public void TryCreateWriteValue_UnsupportedValue_ReturnsFalseWithoutThrowing()
    {
        var point = new TagPointConfiguration { TagId = "t1", Address = "ns=2;s=Tag" };

        Assert.False(OpcUaDriver.TryCreateWriteValue(point, new object(), out var writeValue, out var error));

        Assert.Null(writeValue);
        Assert.Contains("cannot be written", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreateWriteValue_InvalidAddress_ReturnsFalse()
    {
        var point = new TagPointConfiguration { TagId = "t1", Address = "not a node id" };

        Assert.False(OpcUaDriver.TryCreateWriteValue(point, 42, out _, out var error));
        Assert.Contains("Invalid OPC UA address", error, StringComparison.Ordinal);
    }
}
