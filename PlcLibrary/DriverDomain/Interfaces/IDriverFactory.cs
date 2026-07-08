using PlcLibrary.General.Configuration;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverDomain.Interfaces
{
    internal interface IDriverFactory
    {
        string ProtocolDriver { get; }
        Task<IProtocolDriver> CreateAsync(DeviceConfiguration device, CancellationToken ct = default);
        string GetConnectionKey(string connectionString);
    }
}
