using PlcLibrary.DriverDomain.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Pipeline.Interfaces
{
    public interface IDataPipeline
    {
        Task StartAsync(CancellationToken ct);
        Task StopAsync(CancellationToken ct);

        /// <summary>将采集结果写入管道。</summary>
        ValueTask HandleAsync(DriverResult result, CancellationToken ct);

        /// <summary>
        /// 订阅采集结果流。每个订阅者独立收到全部消息（fan-out broadcast）。
        /// </summary>
        IAsyncEnumerable<DriverResult> ReadAsync(CancellationToken ct = default);

    }
}
