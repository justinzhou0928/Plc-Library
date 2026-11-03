using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using PlcLibrary.Application;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.Controller.Models;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.Tests.Application;

/// <summary>应用门面：只依赖 IPlcGateway，验证对内部能力的委托与单点便捷方法。</summary>
public class PlcGatewayTests
{
    private readonly Mock<IDeviceScheduler> _scheduler = new();
    private readonly Mock<IDeviceAccessor> _accessor = new();
    private readonly Mock<IDeviceBrowser> _browser = new();

    private PlcGateway Create() => new(_scheduler.Object, _accessor.Object, _browser.Object);

    private static DeviceConfiguration Device(params TagPointConfiguration[] points) => new()
    {
        Id = "dev-01",
        Protocol = "Test",
        ConnectionString = "host:127.0.0.1",
        TagPoints = points,
    };

    [Fact]
    public async Task ApplyDevicesAndHealth_DelegateToScheduler()
    {
        var device = Device();
        var health = new DeviceHealthInfo("dev-01", "Test", true, null, DateTime.UtcNow);
        _scheduler.Setup(s => s.GetDeviceHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([health]);

        var gateway = Create();
        await gateway.ApplyDevicesAsync([device]);
        var result = await gateway.GetHealthAsync();

        _scheduler.Verify(s => s.ApplyDevicesAsync(
            It.Is<IReadOnlyList<DeviceConfiguration>>(d => d.Count == 1 && d[0] == device),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(result);
        Assert.True(result[0].IsRunning);
    }

    [Fact]
    public async Task ReadWriteAndBrowse_DelegateToAccessorAndBrowser()
    {
        var point = new TagPointConfiguration { TagId = "t1", Address = "n1" };
        var device = Device(point);
        var result = DriverResult.Good("n1", 42);
        _accessor.Setup(a => a.ReadAsync(device, It.IsAny<TagPointConfiguration[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([result]);
        _accessor.Setup(a => a.WriteAsync(device, It.IsAny<IReadOnlyDictionary<TagPointConfiguration, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([result]);
        _browser.Setup(b => b.BrowseAsync(device, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TagDescriptor { Address = "n1", Name = "t1" }]);

        var gateway = Create();
        var read = await gateway.ReadAsync(device, [point]);
        await gateway.WriteAsync(device, new Dictionary<TagPointConfiguration, object> { [point] = 1 });
        var tags = await gateway.BrowseAsync(device, null);

        Assert.Equal(42, read[0].Value);
        Assert.Single(tags);
        Assert.Equal("n1", tags[0].Address);
    }

    [Fact]
    public async Task ReadTagAsync_FindsPointAndReturnsResult()
    {
        var point = new TagPointConfiguration { TagId = "t1", Address = "n1" };
        var device = Device(point);
        _accessor.Setup(a => a.ReadAsync(device, It.Is<TagPointConfiguration[]>(p => p.Length == 1 && p[0] == point), It.IsAny<CancellationToken>()))
            .ReturnsAsync([DriverResult.Good("n1", 42)]);

        var result = await Create().ReadTagAsync(device, "t1");

        Assert.NotNull(result);
        Assert.Equal(42, result.Value.Value);
    }

    [Fact]
    public async Task ReadTagAsync_MissingTag_Throws()
    {
        var device = Device();

        await Assert.ThrowsAsync<ArgumentException>(() => Create().ReadTagAsync(device, "missing"));
    }

    [Fact]
    public void AddPlcLibrary_RegistersGateway()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddPlcLibrary())
            .Build();

        Assert.NotNull(host.Services.GetRequiredService<IPlcGateway>());
    }
}