using System.Threading;
using System.Threading.Tasks;
using PlcLibrary.DriverDomain.Models;

namespace PlcLibrary.Pipeline.Interfaces
{
    /// <summary>采集结果处理器，通过 DI 注册后自动接收采集数据。</summary>
    public interface IDataHandler
    {
        ValueTask HandleAsync(DriverResult result, CancellationToken ct);
    }
}
