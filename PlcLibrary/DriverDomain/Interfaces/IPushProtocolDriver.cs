using System;
using System.Threading;
using System.Threading.Tasks;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.DriverDomain.Interfaces
{
    /// <summary>推送型驱动：订阅设备变化并通过回调输出</summary>
    public interface IPushProtocolDriver : IProtocolDriver
    {
        /// <summary>开始推送订阅</summary>
        /// <param name="points">订阅点位</param>
        /// <param name="onData">数据回调</param>
        /// <param name="ct">取消令牌</param>
        Task StartPushingAsync(
            TagPointConfiguration[] points,
            Func<DriverResult, CancellationToken, ValueTask> onData,
            CancellationToken ct);

        /// <summary>停止推送订阅</summary>
        /// <param name="ct">取消令牌</param>
        Task StopPushingAsync(CancellationToken ct);
    }
}
