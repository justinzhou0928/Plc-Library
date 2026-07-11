using Microsoft.Extensions.DependencyInjection;
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

            services.AddSingleton<ResiliencePipelineRegistry<string>>();

            services.AddSingleton<IDeviceAccessor, DeviceDriverPool>();
            services.AddSingleton<IDeviceScheduler, TaskScheduler>();
            services.AddHostedService<TaskSchedulerHost>();

            services.AddSingleton<IDataPipeline, DriverResultPipeline>();
            services.AddHostedService<PipelineHost>();

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

            services.AddSingleton<IDriverFactory>(sp =>
                new GenericDriverFactory(
                    name,
                    device => ActivatorUtilities.CreateInstance<TDriver>(sp, device),
                    connectionKey ?? (cs => cs)));
            return services;
        }
    }
}
