using System;

namespace PlcLibrary.Monitor.Models
{
    /// <summary>
    /// 监控缓存条目的复合标识。
    /// <para>
    /// 点位唯一性由 <see cref="DeviceId"/> + <see cref="TagId"/> 共同决定：
    /// 不同设备（不同 IP/连接串）即使配置了相同的 <see cref="TagId"/> 或相同的协议地址
    /// （例如同型号 PLC 的 <c>DB21.DBX10.2</c>），也互不干扰、各自独立缓存与通知。
    /// 协议地址（<c>Address</c>）保留在缓存的 <c>DriverResult.Address</c> 上，不参与键比较。
    /// </para>
    /// </summary>
    public readonly record struct PlcPointId
    {
        /// <summary>设备标识</summary>
        public string DeviceId { get; init; }

        /// <summary>点位业务标识</summary>
        public string TagId { get; init; }

        /// <summary>创建点位标识</summary>
        /// <param name="deviceId">设备标识</param>
        /// <param name="tagId">点位标识</param>
        public PlcPointId(string deviceId, string tagId)
        {
            DeviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
            TagId = tagId ?? throw new ArgumentNullException(nameof(tagId));
        }

        /// <inheritdoc/>
        public override string ToString() => $"{DeviceId}/{TagId}";
    }
}
