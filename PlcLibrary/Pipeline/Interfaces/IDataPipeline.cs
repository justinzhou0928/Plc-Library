using PlcLibrary.DriverDomain.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Pipeline.Interfaces
{
    /// <summary>数据管道，将采集结果扇出到所有 <see cref="IDataHandler"/> 和流订阅者。</summary>
    public interface IDataPipeline
    {
        Task StartAsync(CancellationToken ct);
        Task StopAsync(CancellationToken ct);

        /// <summary>将采集结果写入管道。</summary>
        ValueTask HandleAsync(DriverResult result, CancellationToken ct);
        /// <summary>订阅采集结果流。每个订阅者独立收到全部消息。</summary>
        IAsyncEnumerable<DriverResult> ReadAsync(CancellationToken ct = default);
    }
}
