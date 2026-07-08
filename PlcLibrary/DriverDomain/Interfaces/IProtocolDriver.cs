using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverDomain.Interfaces
{
    public interface IProtocolDriver :IAsyncDisposable
    {
        DriverStatus DriverStatus { get; }
        Task ConnectAsync(CancellationToken ct = default);
        Task DisconnectAsync(CancellationToken ct = default);
        Task<bool> TryReconnectAsync(CancellationToken ct = default);
        Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default);
        Task<DriverResult[]> WriteAsync(IReadOnlyDictionary<TagPointConfiguration, object> values,CancellationToken ct = default);
    }
}
