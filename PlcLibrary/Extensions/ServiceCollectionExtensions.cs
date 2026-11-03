using System;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlcLibrary.Application;
using PlcLibrary.Controller.Engine;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Engine;
using PlcLibrary.Pipeline.Interfaces;
using PlcLibrary.Pipeline.Models;

namespace PlcLibrary.Extensions
{
    /// <summary>PLC 库 DI 注册扩展</summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>注册 PLC 库（不绑定配置）</summary>
        /// <param name="services">服务集合</param>
        public static IServiceCollection AddPlcLibrary(this IServiceCollection services)
        {
            services.AddOptions<PoolOptions>().ValidateDataAnnotations().ValidateOnStart();
            services.AddOptions<PipelineOptions>().ValidateDataAnnotations().ValidateOnStart();
            return services.AddPlcLibraryCore();
        }

        /// <summary>注册 PLC 库并绑定配置节</summary>
        /// <param name="services">服务集合</param>
        /// <param name="configuration">应用配置</param>
        public static IServiceCollection AddPlcLibrary(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<PoolOptions>()
                .Bind(configuration.GetSection(PoolOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.AddOptions<PipelineOptions>()
                .Bind(configuration.GetSection(PipelineOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            return services.AddPlcLibraryCore();
        }

        private static IServiceCollection AddPlcLibraryCore(this IServiceCollection services)
        {
            // 单一实例实现多个接口：池既提供读写，也提供标签浏览。
            services.AddSingleton<DeviceDriverPool>();
            services.AddSingleton<IDeviceAccessor>(sp => sp.GetRequiredService<DeviceDriverPool>());
            services.AddSingleton<IDeviceBrowser>(sp => sp.GetRequiredService<DeviceDriverPool>());

            services.AddSingleton<DriverResultPipeline>();
            services.AddSingleton<IDataPipeline>(sp => sp.GetRequiredService<DriverResultPipeline>());
            services.AddHostedService<PipelineHost>();

            services.AddSingleton<TaskScheduler>();
            services.AddSingleton<IDeviceScheduler>(sp => sp.GetRequiredService<TaskScheduler>());
            services.AddHostedService<TaskSchedulerHost>();

            // 应用只依赖 IPlcGateway；scheduler/accessor/browser 留在内部。
            services.AddSingleton<IPlcGateway>(sp => new PlcGateway(
                sp.GetRequiredService<IDeviceScheduler>(),
                sp.GetRequiredService<IDeviceAccessor>(),
                sp.GetRequiredService<IDeviceBrowser>()));

            return services;
        }

        /// <summary>注册协议驱动</summary>
        /// <typeparam name="TDriver">驱动类型</typeparam>
        /// <param name="services">服务集合</param>
        public static IServiceCollection AddDriver<TDriver>(this IServiceCollection services)
            where TDriver : class, IProtocolDriver
        {
            var name = typeof(TDriver).GetCustomAttribute<ProtocolDriverNameAttribute>()?.Name
                ?? throw new InvalidOperationException(
                    $"Driver type '{typeof(TDriver).FullName}' is missing [ProtocolDriverName] attribute.");

            var supportsPush = typeof(IPushProtocolDriver).IsAssignableFrom(typeof(TDriver));

            services.AddSingleton<IDriverFactory>(sp =>
            {
                Func<DeviceConfiguration, IProtocolDriver> create = device =>
                    ActivatorUtilities.CreateInstance<TDriver>(sp, device);
                return ActivatorUtilities.CreateInstance<GenericDriverFactory>(sp, name, create, supportsPush);
            });

            return services;
        }
    }
}
