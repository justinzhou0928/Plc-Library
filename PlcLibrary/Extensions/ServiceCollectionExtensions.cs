using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcLibrary.Controller.Collectors;
using PlcLibrary.Controller.Engine;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.Pipeline.Engine;
using PlcLibrary.Pipeline.Interfaces;
using PlcLibrary.Pipeline.Models;
using Polly.Registry;
using System;
using System.Reflection;

namespace PlcLibrary.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddPlcLibrary(this IServiceCollection services)
        {
            services.AddOptions<PoolOptions>().ValidateOnStart();
            services.AddOptions<PipelineOptions>().ValidateOnStart();
            return services.AddPlcLibraryCore();
        }

        public static IServiceCollection AddPlcLibrary(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<PoolOptions>()
                .Bind(configuration.GetSection(PoolOptions.SectionName))
                .ValidateOnStart();
            services.AddOptions<PipelineOptions>()
                .Bind(configuration.GetSection(PipelineOptions.SectionName))
                .ValidateOnStart();
            return services.AddPlcLibraryCore();
        }

        private static IServiceCollection AddPlcLibraryCore(this IServiceCollection services)
        {
            services.AddSingleton<ResiliencePipelineRegistry<string>>();

            services.AddSingleton<IDeviceAccessor, DeviceDriverPool>();

            services.AddSingleton<TaskScheduler>();
            services.AddSingleton<IDeviceScheduler>(sp => sp.GetRequiredService<TaskScheduler>());
            services.AddHostedService<TaskSchedulerHost>();

            services.AddSingleton<DriverResultPipeline>();
            services.AddSingleton<IDataPipeline>(sp => sp.GetRequiredService<DriverResultPipeline>());
            services.AddHostedService<PipelineHost>();

            services.AddTransient<PollingCollector>();
            services.AddTransient<TaskActuator>();

            services.AddHealthChecks()
                .AddCheck<General.PlcLibraryHealthCheck>("plc-library");

            return services;
        }

        public static IServiceCollection AddDriver<TDriver>(
            this IServiceCollection services,
            Func<string, string>? connectionKey = null)
            where TDriver : class, IProtocolDriver
        {
            var name = typeof(TDriver).GetCustomAttribute<ProtocolDriverNameAttribute>()?.Name
                ?? throw new InvalidOperationException(
                    $"Driver type '{typeof(TDriver).FullName}' is missing [ProtocolDriverName] attribute.");

            var supportsPush = typeof(IPushProtocolDriver).IsAssignableFrom(typeof(TDriver));

            services.AddSingleton<IDriverFactory>(sp =>
                new GenericDriverFactory(
                    name,
                    device => ActivatorUtilities.CreateInstance<TDriver>(sp, device),
                    connectionKey ?? (cs => cs),
                    supportsPush,
                    sp.GetRequiredService<ResiliencePipelineRegistry<string>>(),
                    sp.GetRequiredService<IOptions<PoolOptions>>(),
                    sp.GetRequiredService<ILoggerFactory>()));

            return services;
        }
    }
}
