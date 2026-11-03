using System.Threading;
using System.Threading.Tasks;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.Pipeline.Interfaces;

namespace PlcLibrary.Monitor.Engine
{
    /// <summary>
    /// 把 <see cref="PlcMonitor"/> 适配为管道处理器；独立包装类型使 DI 的 TryAddEnumerable 能按实现类型去重。
    /// </summary>
    internal sealed class PlcMonitorHandler(PlcMonitor monitor) : IDataHandler
    {
        /// <inheritdoc/>
        public ValueTask HandleAsync(DriverResult result, CancellationToken ct)
            => monitor.HandleAsync(result, ct);
    }
}
