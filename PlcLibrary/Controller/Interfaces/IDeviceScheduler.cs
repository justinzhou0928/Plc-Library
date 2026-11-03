using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlcLibrary.Controller.Models;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.Controller.Interfaces
{
    internal interface IDeviceScheduler
    {
        Task ApplyDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken ct = default);

        Task<IReadOnlyList<DeviceHealthInfo>> GetDeviceHealthAsync(CancellationToken ct = default);
    }
}
