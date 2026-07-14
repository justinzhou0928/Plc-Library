using PlcLibrary.Controller.Engine;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverDomain.Interfaces
{
    public interface IDriverFactory
    {
        string ProtocolDriverName { get; }
        bool SupportsPush { get; }
        Task<IProtocolDriver> CreateAsync(DeviceConfiguration device, CancellationToken ct = default);
        string GetConnectionKey(string connectionString);
        Task<IDeviceCollector?> TryCreateCollectorAsync(
            DeviceConfiguration device,
            IDataPipeline pipeline,
            IServiceProvider sp,
            CancellationToken ct);
    }
}
