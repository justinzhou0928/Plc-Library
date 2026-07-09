using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.General.Configuration;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverPool.Engine
{
    internal sealed class GenericDriverFactory : IDriverFactory
    {
        private readonly Func<DeviceConfiguration, IProtocolDriver> _create;
        private readonly Func<string, string> _connectionKey;

        public GenericDriverFactory(
            string protocolDriverName,
            Func<DeviceConfiguration, IProtocolDriver> create,
            Func<string, string> connectionKey)
        {
            ProtocolDriverName = protocolDriverName;
            _create = create;
            _connectionKey = connectionKey;
        }

        public string ProtocolDriverName { get; }

        public Task<IProtocolDriver> CreateAsync(DeviceConfiguration device, CancellationToken ct = default)
            => Task.FromResult(_create(device));

        public string GetConnectionKey(string connectionString)
            => _connectionKey(connectionString);
    }
}
