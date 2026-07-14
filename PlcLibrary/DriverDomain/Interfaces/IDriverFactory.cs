using PlcLibrary.General.Configuration;
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
    }
}
