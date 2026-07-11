using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General.Configuration;
using Polly.Registry;

namespace PlcLibrary.Tests.DriverPool;

public class DeviceDriverPoolTests
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly ResiliencePipelineRegistry<string> _pipelineRegistry = new();
    private readonly Mock<IOptions<PoolOptions>> _options = new();
    private readonly Mock<IDriverFactory> _factory = new();
    private readonly DeviceConfiguration _device = new()
    {
        Id = "plc-01",
        Protocol = "Test",
        ConnectionString = "host:127.0.0.1;port:502",
        TagPoints = [new TagPointConfiguration { TagId = "t1", Address = "40001" }]
    };

    public DeviceDriverPoolTests()
    {
        _options.Setup(o => o.Value).Returns(new PoolOptions { MaxConnectionsPerDevice = 1 });
        _factory.Setup(f => f.ProtocolDriverName).Returns("Test");
        _factory.Setup(f => f.GetConnectionKey(It.IsAny<string>())).Returns((string cs) => cs);
    }

    [Fact]
    public async Task ReadAsync_AcquiresAndReturnsDriver()
    {
        var driver = new Mock<IProtocolDriver>();
        driver.Setup(d => d.DriverStatus).Returns(DriverStatus.Connected);
        driver.Setup(d => d.ReadAsync(It.IsAny<TagPointConfiguration[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([DriverResult.Good("40001", 42)]);

        _factory.Setup(f => f.CreateAsync(It.IsAny<DeviceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(driver.Object);

        var pool = CreatePool();
        var results = await pool.ReadAsync(_device, _device.TagPoints, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(QualityCode.Good, results[0].Status);
        driver.Verify(d => d.ReadAsync(_device.TagPoints, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WriteAsync_AcquiresAndReturnsDriver()
    {
        var driver = new Mock<IProtocolDriver>();
        driver.Setup(d => d.DriverStatus).Returns(DriverStatus.Connected);
        driver.Setup(d => d.WriteAsync(It.IsAny<IReadOnlyDictionary<TagPointConfiguration, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([DriverResult.Good("40001", null)]);

        _factory.Setup(f => f.CreateAsync(It.IsAny<DeviceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(driver.Object);

        var pool = CreatePool();
        var values = new Dictionary<TagPointConfiguration, object> { [_device.TagPoints[0]] = 42 };
        var results = await pool.WriteAsync(_device, values, CancellationToken.None);

        Assert.Single(results);
        driver.Verify(d => d.WriteAsync(values, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SameConnectionKey_ReusesPool()
    {
        var driverCount = 0;
        _factory.Setup(f => f.CreateAsync(It.IsAny<DeviceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var d = new Mock<IProtocolDriver>();
                d.Setup(x => x.DriverStatus).Returns(DriverStatus.Connected);
                Interlocked.Increment(ref driverCount);
                return d.Object;
            });

        var pool = CreatePool();
        await pool.ReadAsync(_device, _device.TagPoints, CancellationToken.None);
        await pool.ReadAsync(_device, _device.TagPoints, CancellationToken.None);

        Assert.Equal(1, driverCount);
    }

    [Fact]
    public async Task DifferentConnectionKey_CreatesNewPool()
    {
        var driverCount = 0;
        _factory.Setup(f => f.CreateAsync(It.IsAny<DeviceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var d = new Mock<IProtocolDriver>();
                d.Setup(x => x.DriverStatus).Returns(DriverStatus.Connected);
                Interlocked.Increment(ref driverCount);
                return d.Object;
            });

        var pool = CreatePool();
        var device2 = _device with { ConnectionString = "host:10.0.0.1;port:502" };
        _factory.Setup(f => f.GetConnectionKey(device2.ConnectionString)).Returns(device2.ConnectionString);

        await pool.ReadAsync(_device, _device.TagPoints, CancellationToken.None);
        await pool.ReadAsync(device2, device2.TagPoints, CancellationToken.None);

        Assert.Equal(2, driverCount);
    }

    private DeviceDriverPool CreatePool()
        => new(_options.Object, [_factory.Object], _loggerFactory, _pipelineRegistry);
}
