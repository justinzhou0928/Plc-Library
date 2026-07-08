using PlcLibrary.General.Configuration;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Controller.Interfaces
{
    public interface ITaskScheduler
    {
        Task ApplyDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken ct = default);

        Task StartAsync(CancellationToken ct);

        Task StopAsync(CancellationToken ct);
    }
}
