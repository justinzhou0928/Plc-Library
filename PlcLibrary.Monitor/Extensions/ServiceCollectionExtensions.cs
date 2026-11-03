using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PlcLibrary.Monitor.Engine;
using PlcLibrary.Monitor.Interfaces;
using PlcLibrary.Monitor.Models;
using PlcLibrary.Pipeline.Interfaces;

namespace PlcLibrary.Monitor.Extensions
{
    /// <summary>Monitor DI 注册扩展</summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>注册 PLC 实时值监控缓存（默认配置）；需先调用 <c>AddPlcLibrary()</c></summary>
        /// <param name="services">服务集合</param>
        public static IServiceCollection AddPlcMonitor(this IServiceCollection services)
        {
            services.AddOptions<MonitorOptions>().ValidateDataAnnotations().ValidateOnStart();
            return services.AddPlcMonitorCore();
        }

        /// <summary>注册 PLC 实时值监控缓存，并从配置节 <c>"Monitor"</c> 绑定 <see cref="MonitorOptions"/></summary>
        /// <param name="services">服务集合</param>
        /// <param name="configuration">应用配置</param>
        public static IServiceCollection AddPlcMonitor(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<MonitorOptions>()
                .Bind(configuration.GetSection(MonitorOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            return services.AddPlcMonitorCore();
        }

        private static IServiceCollection AddPlcMonitorCore(this IServiceCollection services)
        {
            // 幂等：重复调用 AddPlcMonitor 不会产生多个缓存实例或多个 IDataHandler
            services.TryAddSingleton<PlcMonitor>();
            services.TryAddSingleton<IPlcMonitor>(sp => sp.GetRequiredService<PlcMonitor>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IDataHandler, PlcMonitorHandler>());
            services.AddHostedService<PlcMonitorHost>();
            return services;
        }
    }
}
