using System.Threading;
using System.Threading.Tasks;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.DriverDomain.Interfaces
{
    /// <summary>驱动工厂：按设备配置创建驱动实例</summary>
    public interface IDriverFactory
    {
        /// <summary>协议名</summary>
        string ProtocolDriverName { get; }

        /// <summary>是否支持推送订阅</summary>
        bool SupportsPush { get; }

        /// <summary>创建设备驱动</summary>
        /// <param name="device">设备配置</param>
        /// <param name="ct">取消令牌</param>
        Task<IProtocolDriver> CreateAsync(DeviceConfiguration device, CancellationToken ct = default);
    }
}
