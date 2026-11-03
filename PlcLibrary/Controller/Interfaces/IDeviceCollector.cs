using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Controller.Engine
{
    /// <summary>采集器内部契约：由调度器创建/停止，不对外注册或扩展。</summary>
    internal interface IDeviceCollector : IAsyncDisposable
    {
        Task ExecuteAsync(CancellationToken ct);
    }
}
