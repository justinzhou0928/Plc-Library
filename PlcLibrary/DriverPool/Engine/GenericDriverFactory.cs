using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcLibrary.Controller.Collectors;
using PlcLibrary.Controller.Engine;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;
using Polly.Registry;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverPool.Engine
{
    internal sealed class GenericDriverFactory : IDriverFactory
    {
        private readonly Func<DeviceConfiguration, IProtocolDriver> _create;
        private readonly Func<string, string> _connectionKey;
        private readonly ResiliencePipelineRegistry<string> _pipelineRegistry;
        private readonly PoolOptions _poolOptions;
        private readonly ILoggerFactory _loggerFactory;

        public GenericDriverFactory(
            string protocolDriverName,
            Func<DeviceConfiguration, IProtocolDriver> create,
            Func<string, string> connectionKey,
            bool supportsPush,
            ResiliencePipelineRegistry<string> pipelineRegistry,
            IOptions<PoolOptions> poolOptions,
            ILoggerFactory loggerFactory)
        {
            ProtocolDriverName = protocolDriverName;
            _create = create;
            _connectionKey = connectionKey;
            SupportsPush = supportsPush;
            _pipelineRegistry = pipelineRegistry;
            _poolOptions = poolOptions.Value;
            _loggerFactory = loggerFactory;
        }

        public string ProtocolDriverName { get; }
        public bool SupportsPush { get; }

        public Task<IProtocolDriver> CreateAsync(DeviceConfiguration device, CancellationToken ct = default)
            => Task.FromResult(_create(device));

        public string GetConnectionKey(string connectionString)
            => _connectionKey(connectionString);

        public async Task<IDeviceCollector?> TryCreateCollectorAsync(
            DeviceConfiguration device,
            IDataPipeline pipeline,
            IServiceProvider sp,
            CancellationToken ct)
        {
            if (!SupportsPush) return null;

            var driver = await CreateAsync(device, ct).ConfigureAwait(false);

            var resilience = _pipelineRegistry.GetOrAddPipeline(
                $"{PipelineKey.Pool}:push:{device.Id}",
                builder => builder.AddPoolStrategies(_poolOptions, _loggerFactory.CreateLogger("PushCollector"), device.Id));

            return ActivatorUtilities.CreateInstance<PushCollector>(sp, (IPushProtocolDriver)driver, device, resilience);
        }
    }
}
