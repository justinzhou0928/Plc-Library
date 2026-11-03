using System.Threading;
using System.Threading.Tasks;
using PlcLibrary.DriverDomain.Models;

namespace PlcLibrary.Pipeline.Interfaces
{
    internal interface IDataPipeline
    {
        ValueTask HandleAsync(DriverResult result, CancellationToken ct);
    }
}
