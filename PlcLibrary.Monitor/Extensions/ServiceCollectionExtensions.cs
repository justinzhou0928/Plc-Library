using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlcLibrary.Monitor.Engine;
using PlcLibrary.Monitor.Interfaces;
using PlcLibrary.Monitor.Models;
using PlcLibrary.Pipeline.Interfaces;

namespace PlcLibrary.Monitor.Extensions
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>注册 PLC 实时值监控缓存（默认配置）。需先调用 <c>AddPlcLibrary()</c>。</summary>
        public static IServiceCollection AddPlcMonitor(this IServiceCollection services)
        {
            services.AddOptions<MonitorOptions>().ValidateOnStart();
            return services.AddPlcMonitorCore();
        }

        /// <summary>注册 PLC 实时值监控缓存，并从配置节 <c>"Monitor"</c> 绑定 <see cref="MonitorOptions"/>。</summary>
        public static IServiceCollection AddPlcMonitor(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<MonitorOptions>()
                .Bind(configuration.GetSection(MonitorOptions.SectionName))
                .ValidateOnStart();
            return services.AddPlcMonitorCore();
        }

        private static IServiceCollection AddPlcMonitorCore(this IServiceCollection services)
        {
            services.AddSingleton<PlcMonitor>();
            services.AddSingleton<IPlcMonitor>(sp => sp.GetRequiredService<PlcMonitor>());
            services.AddSingleton<IDataHandler>(sp => sp.GetRequiredService<PlcMonitor>());
            services.AddHostedService<PlcMonitorHost>();
            return services;
        }
    }
}
