using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Controller.Engine
{
    public interface IDeviceCollector : IAsyncDisposable
    {
        Task ExecuteAsync(CancellationToken ct);
    }
}
