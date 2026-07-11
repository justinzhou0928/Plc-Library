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

public class DeviceSharedPoolStressTests
{
    private readonly ILogger<DeviceSharedPool> _logger = NullLogger<DeviceSharedPool>.Instance;
    private readonly ResiliencePipelineRegistry<string> _registry = new();
    private readonly PoolOptions _options = new()
    {
        MaxConnectionsPerDevice = 4,
        OperationTimeout = TimeSpan.FromSeconds(30),
        MaxRetryAttempts = 1,
    };
    private readonly DeviceConfiguration _device = new()
    {
        Id = "stress-test",
        Protocol = "Test",
        ConnectionString = "host:127.0.0.1;port:502"
    };

    [Fact]
    public async Task ConcurrentAcquireReturn_NoDeadlockOrLeak()
    {
        var createCount = 0;
        var factory = new Mock<IDriverFactory>();
        factory.Setup(f => f.CreateAsync(_device, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref createCount);
                var d = new Mock<IProtocolDriver>();
                d.Setup(x => x.DriverStatus).Returns(DriverStatus.Connected);
                return d.Object;
            });

        var pool = new DeviceSharedPool(_logger, _device, factory.Object, _options, _registry);

        var tasks = new List<Task>();
        for (var i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var driver = await pool.AcquireAsync(CancellationToken.None);
                await Task.Delay(10);
                pool.Return(driver);
            }));
        }

        await Task.WhenAll(tasks);

        Assert.True(createCount <= _options.MaxConnectionsPerDevice,
            $"Created {createCount} drivers, expected <= {_options.MaxConnectionsPerDevice}");

        await pool.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_BeyondLimit_WaitsForReturn()
    {
        var factory = new Mock<IDriverFactory>();
        factory.Setup(f => f.CreateAsync(_device, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var d = new Mock<IProtocolDriver>();
                d.Setup(x => x.DriverStatus).Returns(DriverStatus.Connected);
                return d.Object;
            });

        var pool = new DeviceSharedPool(_logger, _device, factory.Object,
            new PoolOptions { MaxConnectionsPerDevice = 2, OperationTimeout = TimeSpan.FromSeconds(30) },
            _registry);

        var drivers = new IProtocolDriver[3];

        drivers[0] = await pool.AcquireAsync(CancellationToken.None);
        drivers[1] = await pool.AcquireAsync(CancellationToken.None);

        var acquireTask = pool.AcquireAsync(CancellationToken.None).AsTask();
        await Task.Delay(100);
        Assert.False(acquireTask.IsCompleted, "Third acquire should be waiting");

        pool.Return(drivers[0]);
        drivers[2] = await acquireTask;

        Assert.NotNull(drivers[2]);

        pool.Return(drivers[1]);
        pool.Return(drivers[2]);
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task FaultedDrivers_AreNotReused()
    {
        var createCount = 0;
        var factory = new Mock<IDriverFactory>();
        factory.Setup(f => f.CreateAsync(_device, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref createCount);
                var d = new Mock<IProtocolDriver>();
                d.Setup(x => x.DriverStatus).Returns(DriverStatus.Connected);
                return d.Object;
            });

        var pool = new DeviceSharedPool(_logger, _device, factory.Object, _options, _registry);

        var driver = await pool.AcquireAsync(CancellationToken.None);

        Mock.Get(driver).Setup(x => x.DriverStatus).Returns(DriverStatus.Faulted);
        pool.Return(driver);

        var acquired = await pool.AcquireAsync(CancellationToken.None);

        Assert.NotSame(driver, acquired);
        Assert.Equal(2, createCount);

        await pool.DisposeAsync();
    }
}
