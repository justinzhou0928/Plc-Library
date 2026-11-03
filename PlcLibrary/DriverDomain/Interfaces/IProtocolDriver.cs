using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.DriverDomain.Interfaces
{
    /// <summary>协议驱动抽象，通过 <see cref="IAsyncDisposable"/> 释放资源</summary>
    public interface IProtocolDriver : IAsyncDisposable
    {
        /// <summary>当前连接状态</summary>
        DriverStatus DriverStatus { get; }

        /// <summary>建立连接</summary>
        /// <param name="ct">取消令牌</param>
        Task ConnectAsync(CancellationToken ct = default);

        /// <summary>断开连接</summary>
        /// <param name="ct">取消令牌</param>
        Task DisconnectAsync(CancellationToken ct = default);

        /// <summary>尝试重连</summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>重连成功返回 true</returns>
        Task<bool> TryReconnectAsync(CancellationToken ct = default);

        /// <summary>批量读取点位，结果顺序须与输入 points 一致</summary>
        /// <param name="points">点位列表</param>
        /// <param name="ct">取消令牌</param>
        Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default);

        /// <summary>批量写入点位</summary>
        /// <param name="values">点位与值</param>
        /// <param name="ct">取消令牌</param>
        Task<DriverResult[]> WriteAsync(IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default);
    }
}
