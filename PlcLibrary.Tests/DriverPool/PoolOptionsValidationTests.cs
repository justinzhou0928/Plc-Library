using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.Extensions;
using PlcLibrary.Modbus;
using PlcLibrary.Pipeline.Models;

namespace PlcLibrary.Tests.DriverPool;

/// <summary>
/// PoolOptions 数据注解与 Polly 约束必须一致：MaxRetryAttempts 为 0 时
/// Polly 会在首次下发设备时抛校验异常；现在改为启动期 Options 校验直接失败，
/// 让配置错误在宿主启动阶段暴露，而不是运行期才崩溃。
/// </summary>
public class PoolOptionsValidationTests
{
    [Fact]
    public async Task MaxRetryAttemptsZero_FailsOptionsValidationOnStart()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPlcLibrary();
                services.AddDriver<ModbusTcpDriver>();
                services.Configure<PoolOptions>(o => o.MaxRetryAttempts = 0);
            })
            .Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("MaxRetryAttempts", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PipelineCapacityZero_FailsOptionsValidationOnStart()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPlcLibrary();
                services.Configure<PipelineOptions>(o => o.Capacity = 0);
            })
            .Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Capacity", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
