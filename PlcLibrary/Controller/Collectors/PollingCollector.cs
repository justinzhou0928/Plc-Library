using Microsoft.Extensions.Logging;
using PlcLibrary.Controller.Engine;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Controller.Collectors
{
    internal sealed class PollingCollector(
        ILogger<PollingCollector> logger,
        DeviceConfiguration device,
        IDeviceAccessor accessor,
        IDataPipeline pipeline) : IDeviceCollector
    {
        public async Task ExecuteAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(device.CollectionInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await CollectOnceAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { ControllerLog.LogCollectionFailed(logger, ex, device.Id); }
            }
        }

        public ValueTask DisposeAsync() => default;

        private async Task CollectOnceAsync(CancellationToken ct)
        {
            var points = device.TagPoints;
            var values = await accessor.ReadAsync(device, points, ct).ConfigureAwait(false);
            if (values.Length != points.Length)
                ControllerLog.LogPointCountMismatch(logger, device.Id, points.Length, values.Length);

            for (var i = 0; i < values.Length; i++)
            {
                var v = values[i];
                var point = i < points.Length ? points[i] : null;
                try
                {
                    await pipeline.HandleAsync(v with
                    {
                        DeviceId = device.Id,
                        TagId = point?.TagId ?? v.Address
                    }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ControllerLog.LogCollectionFailed(logger, ex, device.Id); }
            }
        }
    }
}
