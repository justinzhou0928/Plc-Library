using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.DriverDomain.Interfaces
{
    /// <summary>设备读写访问器。返回结果数组与输入 points 顺序一致。</summary>
    public interface IDeviceAccessor
    {
        /// <summary>批量读取点位，结果与输入 points 一一对应。</summary>
        Task<DriverResult[]> ReadAsync(DeviceConfiguration device, TagPointConfiguration[] points, CancellationToken ct = default);
        /// <summary>批量写入点位。</summary>
        Task<DriverResult[]> WriteAsync(DeviceConfiguration device, IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default);
    }
}
