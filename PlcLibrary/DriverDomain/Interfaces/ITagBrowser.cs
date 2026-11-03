using PlcLibrary.DriverDomain.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverDomain.Interfaces
{
    /// <summary>
    /// 可选的标签/节点浏览能力。只浏览直接子节点，调用方可用返回的 <see cref="TagDescriptor.Address"/>
    /// 继续递归；不支持浏览的驱动不实现此接口。
    /// </summary>
    public interface ITagBrowser
    {
        /// <summary>
        /// 浏览指定节点的直接子节点。<paramref name="address"/> 为 null/空时使用协议根（OPC UA 为 Objects 文件夹）。
        /// </summary>
        Task<IReadOnlyList<TagDescriptor>> BrowseAsync(string? address = null, CancellationToken ct = default);
    }
}