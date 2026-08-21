using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.Tests.DriverPool;

public class DeviceSharedPoolTests
{
    private readonly ILogger<DeviceSharedPool> _logger = NullLogger<DeviceSharedPool>.Instance;
    private readonly ManagedResiliencePipelineRegistry _registry = new();
    private readonly Mock<IDriverFactory> _factory = new();
    private readonly PoolOptions _options = new() { MaxConnectionsPerDevice = 2, OperationTimeout = TimeSpan.FromSeconds(5) };
    private readonly DeviceConfiguration _device = new()
    {
        Id = "plc-01",
        Protocol = "Test",
        ConnectionString = "host:127.0.0.1;port:502"
    };

    public DeviceSharedPoolTests()
    {
        _factory.Setup(f => f.ProtocolDriverName).Returns("Test");
    }

    [Fact]
    public async Task AcquireAsync_CreatesDriver_WhenPoolEmpty()
    {
        var driver = new Mock<IProtocolDriver>();
        driver.Setup(d => d.DriverStatus).Returns(DriverStatus.Connected);
        _factory.Setup(f => f.CreateAsync(_device, It.IsAny<CancellationToken>())).ReturnsAsync(driver.Object);

        var pool = CreatePool();
        var result = await pool.AcquireAsync(CancellationToken.None);

        Assert.Same(driver.Object, result);
        _factory.Verify(f => f.CreateAsync(_device, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AcquireAsync_ReusesIdleDriver()
    {
        var driver = new Mock<IProtocolDriver>();
        driver.Setup(d => d.DriverStatus).Returns(DriverStatus.Connected);
        _factory.Setup(f => f.CreateAsync(_device, It.IsAny<CancellationToken>())).ReturnsAsync(driver.Object);

        var pool = CreatePool();

        var first = await pool.AcquireAsync(CancellationToken.None);
        pool.Return(first);

        var second = await pool.AcquireAsync(CancellationToken.None);

        Assert.Same(first, second);
        _factory.Verify(f => f.CreateAsync(_device, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Return_DisposesFaultedDriver()
    {
        var driver = new Mock<IProtocolDriver>();
        driver.Setup(d => d.DriverStatus).Returns(DriverStatus.Faulted);
        _factory.Setup(f => f.CreateAsync(_device, It.IsAny<CancellationToken>())).ReturnsAsync(driver.Object);

        var pool = CreatePool();
        var acquired = await pool.AcquireAsync(CancellationToken.None);
        pool.Return(acquired);

        driver.Verify(d => d.DisposeAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DisposeAsync_DrainsIdleDrivers()
    {
        var driver = new Mock<IProtocolDriver>();
        driver.Setup(d => d.DriverStatus).Returns(DriverStatus.Connected);
        _factory.Setup(f => f.CreateAsync(_device, It.IsAny<CancellationToken>())).ReturnsAsync(driver.Object);

        var pool = CreatePool();
        var acquired = await pool.AcquireAsync(CancellationToken.None);
        pool.Return(acquired);

        await pool.DisposeAsync();

        driver.Verify(d => d.DisposeAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task AcquireAsync_ConnectsWhenDisconnected()
    {
        var driver = new Mock<IProtocolDriver>();
        driver.Setup(d => d.DriverStatus).Returns(DriverStatus.Disconnected);
        _factory.Setup(f => f.CreateAsync(_device, It.IsAny<CancellationToken>())).ReturnsAsync(driver.Object);

        var pool = CreatePool();
        await pool.AcquireAsync(CancellationToken.None);

        driver.Verify(d => d.ConnectAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Return_AfterDispose_DisposesDriverWithoutThrowing()
    {
        var driver = new Mock<IProtocolDriver>();
        driver.Setup(d => d.DriverStatus).Returns(DriverStatus.Connected);
        _factory.Setup(f => f.CreateAsync(_device, It.IsAny<CancellationToken>())).ReturnsAsync(driver.Object);

        var pool = CreatePool();
        var acquired = await pool.AcquireAsync(CancellationToken.None);
        await pool.DisposeAsync();

        // 池已销毁后晚到的归还：不应抛 ObjectDisposedException，驱动应被释放
        var returned = pool.Return(acquired);

        Assert.False(returned);
        driver.Verify(d => d.DisposeAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task IsIdleBeyond_TrueWhenNoActivityExceedsTtl()
    {
        var pool = CreatePool();
        Assert.False(pool.IsIdleBeyond(TimeSpan.FromSeconds(1))); // 刚创建，活动时间=现在
        await Task.Delay(1100);
        Assert.True(pool.IsIdleBeyond(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task IsIdleBeyond_FalseAfterRecentAcquireReturn()
    {
        var driver = new Mock<IProtocolDriver>();
        driver.Setup(d => d.DriverStatus).Returns(DriverStatus.Connected);
        _factory.Setup(f => f.CreateAsync(_device, It.IsAny<CancellationToken>())).ReturnsAsync(driver.Object);

        var pool = CreatePool();
        var acquired = await pool.AcquireAsync(CancellationToken.None);
        pool.Return(acquired);

        // 刚有过借用活动，即使超过 TTL 也不应判定为空闲可回收
        Assert.False(pool.IsIdleBeyond(TimeSpan.FromSeconds(1)));
    }

    private DeviceSharedPool CreatePool()
        => new(_logger, _device, _factory.Object, _options, _registry);
}
