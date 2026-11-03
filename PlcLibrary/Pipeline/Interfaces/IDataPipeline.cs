using PlcLibrary.DriverDomain.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Pipeline.Interfaces
{
    internal interface IDataPipeline
    {
        ValueTask HandleAsync(DriverResult result, CancellationToken ct);
        IAsyncEnumerable<DriverResult> ReadAsync(CancellationToken ct = default);
    }
}
