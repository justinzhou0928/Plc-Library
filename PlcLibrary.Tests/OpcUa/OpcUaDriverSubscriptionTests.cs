using Opc.Ua;
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
    public void CreateMonitoredItem_WithoutSamplingInterval_UsesPublishingInterval()
    {
        var point = new TagPointConfiguration { TagId = "t1", Address = "ns=2;s=Tag" };

        var item = OpcUaDriver.CreateMonitoredItem(point, NodeId.Parse(point.Address), 1500);

        Assert.Equal(1500, item.SamplingInterval);
        Assert.Equal(1u, item.QueueSize);
    }
}
