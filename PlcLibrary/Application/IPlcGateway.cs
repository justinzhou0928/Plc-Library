using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlcLibrary.Controller.Models;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.Application
{
    /// <summary>
    /// 应用层统一门面：配置/健康、主动读写、可选标签浏览
    /// 低层 `IDeviceScheduler` / `IDeviceAccessor` / `IDeviceBrowser` 不对外暴露；驱动契约仍独立
    /// </summary>
    public interface IPlcGateway
    {
        /// <summary>下发设备配置并开始/停止采集（差量 reconcile）</summary>
        Task ApplyDevicesAsync(
            IReadOnlyList<DeviceConfiguration> devices,
            CancellationToken ct = default);

        /// <summary>查询当前设备健康状态</summary>
        Task<IReadOnlyList<DeviceHealthInfo>> GetHealthAsync(CancellationToken ct = default);

        /// <summary>批量读取，结果与输入 points 顺序一致</summary>
        Task<DriverResult[]> ReadAsync(
            DeviceConfiguration device,
            TagPointConfiguration[] points,
            CancellationToken ct = default);

        /// <summary>批量写入</summary>
        Task<DriverResult[]> WriteAsync(
            DeviceConfiguration device,
            IReadOnlyDictionary<TagPointConfiguration, object> values,
            CancellationToken ct = default);

        /// <summary>按 TagId 读取单点；设备配置中不存在该 TagId 时抛 <see cref="System.ArgumentException"/></summary>
        Task<DriverResult?> ReadTagAsync(
            DeviceConfiguration device,
            string tagId,
            CancellationToken ct = default);

        /// <summary>
        /// 浏览设备直接子节点（null/空为协议根）不支持浏览的协议抛 <see cref="System.NotSupportedException"/>
        /// </summary>
        Task<IReadOnlyList<TagDescriptor>> BrowseAsync(
            DeviceConfiguration device,
            string? address = null,
            CancellationToken ct = default);
    }
}