using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.General.Configuration;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverPool.Engine
{
    internal sealed class GenericDriverFactory(
        string protocolDriverName,
        Func<DeviceConfiguration, IProtocolDriver> create,
        bool supportsPush) : IDriverFactory
    {
        public string ProtocolDriverName { get; } = protocolDriverName;
        public bool SupportsPush => supportsPush;

        public Task<IProtocolDriver> CreateAsync(DeviceConfiguration device, CancellationToken ct = default)
            => Task.FromResult(create(device));
    }
}
