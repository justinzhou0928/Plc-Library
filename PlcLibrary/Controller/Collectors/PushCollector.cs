using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlcLibrary.Controller.Engine;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;
using Polly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Controller.Collectors
{
    internal sealed class PushCollector : IDeviceCollector
    {
        private readonly IPushProtocolDriver _driver;
        private readonly DeviceConfiguration _device;
        private readonly ResiliencePipeline _resilience;
        private readonly IDataPipeline _pipeline;
        private readonly ILogger<PushCollector> _logger;
        private readonly ManagedResiliencePipelineRegistry _pipelineRegistry;
        private readonly IReadOnlyDictionary<string, string> _addressToTag;

        public PushCollector(
            IPushProtocolDriver driver,
            DeviceConfiguration device,
            IDataPipeline pipeline,
            ILogger<PushCollector> logger,
            ManagedResiliencePipelineRegistry pipelineRegistry,
            IOptions<PoolOptions> poolOptions,
            ILoggerFactory loggerFactory)
        {
            _driver = driver;
            _device = device;
            _pipeline = pipeline;
            _logger = logger;
            _resilience = pipelineRegistry.GetOrAddPipeline(
                ResiliencePipelineKeys.Push(device.Id),
                builder => builder.AddPoolStrategies(poolOptions.Value, loggerFactory.CreateLogger("PushCollector"), device.Id));
            _pipelineRegistry = pipelineRegistry;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var point in device.TagPoints.Where(p => !string.IsNullOrEmpty(p.Address)))
            {
                if (seen.Add(point.Address))
                    dict[point.Address] = point.TagId;
                else
                    ControllerLog.LogDeviceValidationFailed(_logger, device.Id,
                        $"Duplicate address '{point.Address}' in PushCollector, first tag '{dict[point.Address]}' wins");
            }
            _addressToTag = dict;
        }

        public async Task ExecuteAsync(CancellationToken ct)
        {
            await _resilience.ExecuteAsync(async token =>
            {
                if (_driver.DriverStatus is DriverStatus.Disconnected or DriverStatus.Faulted)
                {
                    if (_driver.DriverStatus == DriverStatus.Faulted)
                        await _driver.TryReconnectAsync(token).ConfigureAwait(false);
                    else
                        await _driver.ConnectAsync(token).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);

            await _driver.StartPushingAsync(
                _device.TagPoints,
                async (result, token) =>
                {
                    try
                    {
                        var enriched = result with
                        {
                            DeviceId = _device.Id,
                            TagId = _addressToTag.TryGetValue(result.Address, out var tag)
                                ? tag
                                : result.TagId
                        };
                        await _pipeline.HandleAsync(enriched, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        ControllerLog.LogCollectionFailed(_logger, ex, _device.Id);
                    }
                },
                ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            try { await _driver.StopPushingAsync(default).ConfigureAwait(false); }
            catch { }
            try { await _driver.DisposeAsync().ConfigureAwait(false); }
            catch { }
            // 随采集器销毁移除设备级弹性管线，避免热更新累积
            _pipelineRegistry.TryRemove(ResiliencePipelineKeys.Push(_device.Id));
        }
    }
}
