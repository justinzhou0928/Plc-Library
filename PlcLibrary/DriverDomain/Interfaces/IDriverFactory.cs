using PlcLibrary.General.Configuration;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverDomain.Interfaces
{
    /// <summary>驱动工厂，负责创建驱动并计算连接池分组键。</summary>
    public interface IDriverFactory
    {
        string ProtocolDriverName { get; }
        Task<IProtocolDriver> CreateAsync(DeviceConfiguration device, CancellationToken ct = default);
        /// <summary>相同键的设备共享连接池。</summary>
        string GetConnectionKey(string connectionString);
    }
}
