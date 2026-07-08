using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlcLibrary.Controller.Engine;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.Pipeline.Engine;
using PlcLibrary.Pipeline.Interfaces;
using PlcLibrary.Pipeline.Models;
using Polly;

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

            services.AddSingleton<ResiliencePipeline>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<PoolOptions>>().Value;
                return new ResiliencePipelineBuilder()
                    .AddPoolStrategies(options)
                    .Build();
            });
            services.AddSingleton<DeviceDriverPool>();
            services.AddSingleton<IDataPipeline, DriverResultPipeline>();
            services.AddHostedService<PipelineHostedService>();

            services.AddSingleton<ITaskScheduler, TaskScheduler>();
            services.AddHostedService<TaskSchedulerHostedService>();

            return services;
        }
    }
}
