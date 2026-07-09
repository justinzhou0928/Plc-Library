using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlcLibrary.Controller.Engine;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.Pipeline.Engine;
using PlcLibrary.Pipeline.Interfaces;
using PlcLibrary.Pipeline.Models;
using Polly.Registry;
using System;

namespace PlcLibrary.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddPlcLibrary(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<PoolOptions>()
                .Bind(configuration.GetSection(PoolOptions.SectionName))
                .ValidateOnStart();

            services.AddOptions<PipelineOptions>()
                .Bind(configuration.GetSection(PipelineOptions.SectionName))
                .ValidateOnStart();

            services.AddSingleton<ResiliencePipelineRegistry<string>>();

            services.AddSingleton<IDeviceAccessor, DeviceDriverPool>();
            services.AddSingleton<IDataPipeline, DriverResultPipeline>();
            services.AddHostedService<PipelineHostedService>();

            services.AddSingleton<ITaskScheduler, TaskScheduler>();
            services.AddHostedService<TaskSchedulerHostedService>();

            return services;
        }

        public static IServiceCollection AddDriver<TDriver>(
            this IServiceCollection services,
            string protocol,
            Func<string, string>? connectionKey = null)
            where TDriver : class, IProtocolDriver
        {
            services.AddSingleton<IDriverFactory>(sp =>
                new GenericDriverFactory(
                    protocol,
                    device => ActivatorUtilities.CreateInstance<TDriver>(sp, device),
                    connectionKey ?? (cs => cs)));
            return services;
        }
    }
}
