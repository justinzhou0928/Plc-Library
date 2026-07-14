using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverDomain.Interfaces
{
    public interface IPushProtocolDriver : IProtocolDriver
    {
        Task StartPushingAsync(
            TagPointConfiguration[] points,
            Func<DriverResult, CancellationToken, ValueTask> onData,
            CancellationToken ct);

        Task StopPushingAsync(CancellationToken ct);
    }
}
