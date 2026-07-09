using PlcLibrary.General.Configuration;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Controller.Interfaces
{
    /// <summary>设备采集调度器，推送配置后自动管理采集任务的增删改。</summary>
    public interface ITaskScheduler
    {
        /// <summary>推送设备配置列表，自动识别新增/移除/变更并差量应用。</summary>
        Task ApplyDevicesAsync(IReadOnlyList<DeviceConfiguration> devices, CancellationToken ct = default);
    }
}
