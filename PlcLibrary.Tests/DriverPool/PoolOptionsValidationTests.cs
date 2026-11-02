using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General.Configuration;
using PlcLibrary.Modbus;

namespace PlcLibrary.Tests.DriverPool;

/// <summary>
/// PoolOptions 数据注解允许 MaxRetryAttempts = 0，但 Polly 的重试策略要求 ≥ 1：
/// 配置层校验通过、运行期才抛异常，属于「配置错误反馈过晚」的风险点。
/// </summary>
public class PoolOptionsValidationTests
{
    private static DeviceConfiguration Device() => new()
    {
        Id = "modbus-01",
        Protocol = "Modbus_TCP",
        ConnectionString = "host:127.0.0.1;port:502",
        TagPoints = [new TagPointConfiguration { TagId = "t1", Address = "40001" }],
    };

    [Fact]
    public async Task MaxRetryAttemptsZero_PassesOptionsValidation_ButThrowsOnFirstDeviceApply()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPlcLibrary();
                services.AddDriver<ModbusTcpDriver>();
                services.Configure<PoolOptions>(o => o.MaxRetryAttempts = 0);
            })
            .Build();

        // 启动阶段 ValidateOnStart 通过：Range(0,10) 允许 0
        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();

            var error = await Record.ExceptionAsync(() => scheduler.ApplyDevicesAsync([Device()]));

            Assert.NotNull(error);
            Assert.Contains("MaxRetryAttempts", error!.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { await host.StopAsync(); } catch (Exception) { }
        }
    }
}
