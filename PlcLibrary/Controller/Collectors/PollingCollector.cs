using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PlcLibrary.Controller.Engine;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;

namespace PlcLibrary.Controller.Collectors
{
    internal sealed class PollingCollector(
        ILogger<PollingCollector> logger,
        DeviceConfiguration device,
        IDeviceAccessor accessor,
        IDataPipeline pipeline) : IDeviceCollector, ICollectorHeartbeat
    {
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;

        public DateTime LastActivityUtc => new(Volatile.Read(ref _lastActivityTicks), DateTimeKind.Utc);

        // 一个轮询周期最多允许“4 个采集间隔 + 30s”没有进展，避免把慢设备误判为卡死。
        public TimeSpan StallTimeout { get; } = TimeSpan.FromTicks(
            Math.Max(device.CollectionInterval.Ticks * 4, TimeSpan.FromSeconds(30).Ticks));

        public async Task ExecuteAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(device.CollectionInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                { await CollectOnceAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { ControllerLog.LogCollectionFailed(logger, ex, device.Id); }
                finally
                {
                    // 只要一个采集周期结束（成功或失败）就刷新心跳；卡在 ReadAsync/HandleAsync 时不会执行到这里。
                    Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                }
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
