using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General.Configuration;
using Polly.Registry;

namespace PlcLibrary.Tests.DriverPool;

public class DeviceSharedPoolTests
{
    private readonly ILogger<DeviceSharedPool> _logger = NullLogger<DeviceSharedPool>.Instance;
    private readonly ResiliencePipelineRegistry<string> _registry = new();
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
        _factory.Setup(f => f.GetConnectionKey(It.IsAny<string>())).Returns((string cs) => cs);
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

    private DeviceSharedPool CreatePool()
        => new(_logger, _device, _factory.Object, _options, _registry);
}
