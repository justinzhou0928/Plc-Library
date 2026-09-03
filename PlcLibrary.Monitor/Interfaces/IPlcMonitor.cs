using PlcLibrary.DriverDomain.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Monitor.Interfaces
{
    /// <summary>
    /// PLC 实时值监控缓存：接收采集结果，按 <c>(DeviceId, TagId)</c> 缓存最新值，
    /// 并仅向订阅方推送「发生变化」的点位，屏蔽轮询产生的重复（干扰）消息。
    /// </summary>
    public interface IPlcMonitor
    {
        /// <summary>读取某点位的最新缓存值；尚未收到数据时返回 <c>null</c>。</summary>
        DriverResult? Get(string deviceId, string tagId);

        /// <summary>读取某设备全部点位的最新缓存快照。</summary>
        IReadOnlyList<DriverResult> GetDevice(string deviceId);

        /// <summary>
        /// 订阅单点变化：先立即返回当前缓存值（若存在），随后仅在该点位
        /// 的值或质量状态发生变化时产出新值（订阅通道容量 1、丢弃最旧，latest-wins）。
        /// </summary>
        IAsyncEnumerable<DriverResult> SubscribeAsync(string deviceId, string tagId, CancellationToken ct = default);

        /// <summary>订阅设备级变化：产出该设备下所有发生变化的点位。</summary>
        IAsyncEnumerable<DriverResult> SubscribeDeviceAsync(string deviceId, CancellationToken ct = default);
    }
}
