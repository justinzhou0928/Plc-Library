using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverDomain.Interfaces
{
    /// <summary>主动浏览设备节点/标签（当前由支持 <see cref="ITagBrowser"/> 的驱动实现）。</summary>
    public interface IDeviceBrowser
    {
        /// <summary>
        /// 浏览设备的直接子节点。<paramref name="address"/> 为 null/空时使用协议根。
        /// 不支持浏览的驱动抛出 <see cref="System.NotSupportedException"/>。
        /// </summary>
        Task<IReadOnlyList<TagDescriptor>> BrowseAsync(
            DeviceConfiguration device,
            string? address = null,
            CancellationToken ct = default);
    }
}