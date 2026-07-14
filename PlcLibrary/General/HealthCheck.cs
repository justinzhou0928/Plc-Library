using Microsoft.Extensions.Diagnostics.HealthChecks;
using PlcLibrary.DriverPool.Engine;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TaskScheduler = PlcLibrary.Controller.Engine.TaskScheduler;

namespace PlcLibrary.General
{
    internal sealed class PlcLibraryHealthCheck : IHealthCheck
    {
        private readonly TaskScheduler _scheduler;
        private readonly DeviceDriverPool _pool;

        public PlcLibraryHealthCheck(TaskScheduler scheduler, DeviceDriverPool pool)
        {
            _scheduler = scheduler;
            _pool = pool;
        }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken ct = default)
        {
            var status = _scheduler.GetHealthStatus();
            var data = new Dictionary<string, object>
            {
                ["ActiveDevices"] = status.ActiveDeviceCount,
                ["PoolCount"] = _pool.PoolCount,
            };

            var result = status.IsHealthy
                ? HealthCheckResult.Healthy("所有设备采集正常", data)
                : HealthCheckResult.Degraded($"部分设备异常: {status.FaultedDevices}", data: data);

            return Task.FromResult(result);
        }
    }

    internal readonly struct SchedulerHealthStatus
    {
        public bool IsHealthy { get; init; }
        public int ActiveDeviceCount { get; init; }
        public string? FaultedDevices { get; init; }
    }
}
