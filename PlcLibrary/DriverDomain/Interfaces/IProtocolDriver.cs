using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverDomain.Interfaces
{
    /// <summary>协议驱动抽象。实现类需同时实现 <see cref="IDisposable"/>。</summary>
    public interface IProtocolDriver : IAsyncDisposable
    {
        DriverStatus DriverStatus { get; }
        Task ConnectAsync(CancellationToken ct = default);
        Task DisconnectAsync(CancellationToken ct = default);
        Task<bool> TryReconnectAsync(CancellationToken ct = default);
        /// <summary>批量读取点位，结果顺序须与输入 points 一致。</summary>
        Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default);
        Task<DriverResult[]> WriteAsync(IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default);
    }
}
