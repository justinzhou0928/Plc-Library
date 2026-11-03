using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.DriverDomain
{
    /// <summary>
    /// 协议驱动状态机基类：统一 <see cref="DriverStatus"/>、状态锁、客户端持有/取用、
    /// 传输故障置 <c>Faulted</c>、重连语义和释放模板；协议差异留在 Connect/Disconnect/Read/Write
    /// </summary>
    /// <typeparam name="TClient">协议客户端类型（S7 Plc、FinsClient、TagClient 等）</typeparam>
    public abstract class ProtocolDriverBase<TClient> : IProtocolDriver
        where TClient : class
    {
        private readonly object _stateLock = new();
        private TClient? _client;
        private DriverStatus _status = DriverStatus.Disconnected;

        /// <summary>当前驱动状态；与客户端字段在同一锁下读写</summary>
        public DriverStatus DriverStatus
        {
            get { lock (_stateLock) return _status; }
        }

        /// <summary>供派生驱动在需要同时保护额外状态（订阅/地址等）时复用同一把锁</summary>
        protected object StateLock => _stateLock;

        /// <summary>当前客户端（可能为 null/Faulted）；需要原子读取额外状态时配合 <see cref="StateLock"/></summary>
        protected TClient? Client
        {
            get { lock (_stateLock) return _client; }
        }

        /// <summary>原子地取出客户端并置为 Disconnected；Disconnect 实现用它避免与并发读竞争</summary>
        protected TClient? DetachClient()
        {
            lock (_stateLock)
            {
                var client = _client;
                _client = null;
                _status = DriverStatus.Disconnected;
                return client;
            }
        }

        /// <summary>在状态锁内更新客户端与状态</summary>
        protected void SetState(TClient? client, DriverStatus status)
        {
            lock (_stateLock)
            {
                _client = client;
                _status = status;
            }
        }

        /// <summary>只更新状态（例如 KeepAlive/监视器把状态置 Faulted）</summary>
        protected void SetStatus(DriverStatus status)
        {
            lock (_stateLock) _status = status;
        }

        /// <summary>尝试取当前已连接客户端；供需要额外状态（如设备地址）的驱动在锁内做一致性检查</summary>
        protected bool TryGetClient([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TClient? client)
        {
            lock (_stateLock)
            {
                client = _client;
                return _client is not null && _status == DriverStatus.Connected;
            }
        }

        /// <summary>取已连接客户端；未连接时抛 <see cref="InvalidOperationException"/></summary>
        protected TClient GetClientOrThrow(string protocolName)
        {
            lock (_stateLock)
            {
                if (_client is null || _status != DriverStatus.Connected)
                    throw new InvalidOperationException($"{protocolName} driver is not connected");
                return _client;
            }
        }

        /// <summary>通信级故障时置 Faulted，连接池据此丢弃驱动并重建</summary>
        protected virtual void MarkFaultedIfTransport(Exception ex)
        {
            if (TransportFailureDetector.IsTransportFailure(ex))
                SetStatus(DriverStatus.Faulted);
        }

        /// <summary>重连成功后的扩展点（日志/指标）</summary>
        protected virtual void OnReconnected()
        {
        }

        /// <summary>重连失败后的扩展点（日志）状态已置为 Faulted</summary>
        protected virtual void OnReconnectFailed(Exception ex)
        {
        }

        /// <summary>断开再连接；失败（取消除外）返回 false 并置 Faulted，不向调用方抛</summary>
        public virtual async Task<bool> TryReconnectAsync(CancellationToken ct = default)
        {
            try
            {
                await DisconnectAsync(ct).ConfigureAwait(false);
                await ConnectAsync(ct).ConfigureAwait(false);
                OnReconnected();
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetStatus(DriverStatus.Faulted);
                OnReconnectFailed(ex);
                return false;
            }
        }

        /// <summary>默认释放模板：断开连接；驱动可覆写以追加日志或释放额外资源</summary>
        public virtual async ValueTask DisposeAsync()
            => await DisconnectAsync().ConfigureAwait(false);

        public abstract Task ConnectAsync(CancellationToken ct = default);

        public abstract Task DisconnectAsync(CancellationToken ct = default);

        public abstract Task<DriverResult[]> ReadAsync(
            TagPointConfiguration[] points, CancellationToken ct = default);

        public abstract Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default);
    }
}