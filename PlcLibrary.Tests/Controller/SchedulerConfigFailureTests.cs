using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.Extensions;
using PlcLibrary.General.Configuration;
using PlcLibrary.S7;

namespace PlcLibrary.Tests.Controller;

/// <summary>
/// 单个设备的驱动构造/配置解析失败不能拖垮整次 ApplyDevicesAsync；
/// 失败设备应被跳过并记录，其他设备仍可继续。
/// </summary>
public class SchedulerConfigFailureTests
{
    [Fact]
    public async Task ApplyDevicesAsync_DriverFactoryThrows_SkipsDeviceWithoutThrowing()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPlcLibrary();
                services.AddSingleton<IDriverFactory>(new ThrowingFactory());
            })
            .Build();

        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var device = new DeviceConfiguration
            {
                Id = "bad-01",
                Protocol = "BadConfig",
                ConnectionString = "unknown:value",
                TagPoints = [new TagPointConfiguration { TagId = "t1", Address = "x" }],
            };

            var error = await Record.ExceptionAsync(() => scheduler.ApplyDevicesAsync([device]));

            Assert.Null(error);
            Assert.Empty(await scheduler.GetDeviceHealthAsync());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ApplyDevicesAsync_InvalidPollingConnectionString_SkipsDeviceWithoutThrowing()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPlcLibrary();
                services.AddDriver<S7Driver>();
            })
            .Build();

        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var device = new DeviceConfiguration
            {
                Id = "bad-s7",
                Protocol = "S7",
                ConnectionString = "host:127.0.0.1;prot:102", // 拼错 port
                TagPoints = [new TagPointConfiguration { TagId = "t1", Address = "DB1.DBW0" }],
            };

            var error = await Record.ExceptionAsync(() => scheduler.ApplyDevicesAsync([device]));

            Assert.Null(error);
            Assert.Empty(await scheduler.GetDeviceHealthAsync());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed class ThrowingFactory : IDriverFactory
    {
        public string ProtocolDriverName => "BadConfig";
        public bool SupportsPush => true;

        public Task<IProtocolDriver> CreateAsync(DeviceConfiguration device, CancellationToken ct = default)
            => throw new InvalidOperationException("unknown connection string key: unknown");
    }
}
