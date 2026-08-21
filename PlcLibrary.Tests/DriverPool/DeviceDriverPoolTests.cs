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
using Polly.Timeout;

namespace PlcLibrary.Tests.DriverPool;

public class DeviceDriverPoolTests
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly ManagedResiliencePipelineRegistry _pipelineRegistry = new();
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

        await pool.ReadAsync(_device, _device.TagPoints, CancellationToken.None);
        await pool.ReadAsync(device2, device2.TagPoints, CancellationToken.None);

        Assert.Equal(2, driverCount);
    }

    [Fact]
    public async Task ReadAsync_FaultedDriver_IsDiscardedAndRecreated()
    {
        var createCount = 0;
        var drivers = new List<Mock<IProtocolDriver>>();
        _factory.Setup(f => f.CreateAsync(It.IsAny<DeviceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // 每个驱动实例独立的可变状态：模拟驱动在传输失败后置 Faulted
                var st = DriverStatus.Connected;
                var d = new Mock<IProtocolDriver>();
                d.Setup(x => x.DriverStatus).Returns(() => st);
                d.Setup(x => x.ReadAsync(It.IsAny<TagPointConfiguration[]>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() =>
                    {
                        st = DriverStatus.Faulted;
                        return [DriverResult.Bad("40001", QualityCode.BadCommFailure, "comm lost")];
                    });
                drivers.Add(d);
                Interlocked.Increment(ref createCount);
                return d.Object;
            });

        var pool = CreatePool();

        var first = await pool.ReadAsync(_device, _device.TagPoints, CancellationToken.None);
        Assert.Equal(QualityCode.BadCommFailure, first[0].Status);

        // Faulted 驱动已被池丢弃并释放，第二次读应创建新驱动
        var second = await pool.ReadAsync(_device, _device.TagPoints, CancellationToken.None);
        Assert.Equal(QualityCode.BadCommFailure, second[0].Status);

        Assert.Equal(2, createCount);
        drivers[0].Verify(d => d.DisposeAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ReadAsync_IoTimeout_ForcesDriverDisconnect()
    {
        var driver = new Mock<IProtocolDriver>();
        driver.Setup(d => d.DriverStatus).Returns(DriverStatus.Connected);
        driver.Setup(d => d.ReadAsync(It.IsAny<TagPointConfiguration[]>(), It.IsAny<CancellationToken>()))
            .Returns(async (TagPointConfiguration[] _, CancellationToken token) =>
            {
                await Task.Delay(Timeout.Infinite, token); // 挂起直至超时取消
                return Array.Empty<DriverResult>();
            });

        _factory.Setup(f => f.CreateAsync(It.IsAny<DeviceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(driver.Object);

        var options = new Mock<IOptions<PoolOptions>>();
        options.Setup(o => o.Value).Returns(new PoolOptions
        {
            MaxConnectionsPerDevice = 1,
            OperationTimeout = TimeSpan.FromMilliseconds(100),
        });
        var pool = new DeviceDriverPool(options.Object, [_factory.Object], _loggerFactory, _pipelineRegistry);

        // IO 超时（TimeoutRejectedException）应触发驱动断开，避免复用状态未知的连接
        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => pool.ReadAsync(_device, _device.TagPoints, CancellationToken.None));

        driver.Verify(d => d.DisconnectAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CleanupIdlePoolsAsync_RecyclesIdlePool()
    {
        var createCount = 0;
        var drivers = new List<Mock<IProtocolDriver>>();
        _factory.Setup(f => f.CreateAsync(It.IsAny<DeviceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var d = new Mock<IProtocolDriver>();
                d.Setup(x => x.DriverStatus).Returns(DriverStatus.Connected);
                d.Setup(x => x.ReadAsync(It.IsAny<TagPointConfiguration[]>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync([DriverResult.Good("40001", 42)]);
                drivers.Add(d);
                Interlocked.Increment(ref createCount);
                return d.Object;
            });

        var options = new Mock<IOptions<PoolOptions>>();
        options.Setup(o => o.Value).Returns(new PoolOptions
        {
            MaxConnectionsPerDevice = 1,
            PoolIdleTimeout = TimeSpan.FromSeconds(1),
        });
        var pool = new DeviceDriverPool(options.Object, [_factory.Object], _loggerFactory, _pipelineRegistry);

        await pool.ReadAsync(_device, _device.TagPoints, CancellationToken.None);
        Assert.Equal(1, createCount);

        // 等空置超时后回收：驱动释放、池销毁、弹性管线移除
        await Task.Delay(1100);
        await pool.CleanupIdlePoolsAsync();
        drivers[0].Verify(d => d.DisposeAsync(), Times.AtLeastOnce);

        // 再次读取：重建池与新驱动
        await pool.ReadAsync(_device, _device.TagPoints, CancellationToken.None);
        Assert.Equal(2, createCount);

        await pool.DisposeAsync();
    }

    private DeviceDriverPool CreatePool()
        => new(_options.Object, [_factory.Object], _loggerFactory, _pipelineRegistry);
}
