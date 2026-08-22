using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.OpcUa
{
    [ProtocolDriverName("OPC_UA")]
    public sealed class OpcUaDriver : IPushProtocolDriver
    {
        private readonly ILogger<OpcUaDriver> _logger;
        private readonly OpcUaDriverConfig _config;
        private readonly object _stateLock = new();
        private Session? _session;
        private IList<Subscription>? _subscriptions;
        private DriverStatus _status = DriverStatus.Disconnected;
        private int _disposed;

        public OpcUaDriver(ILogger<OpcUaDriver> logger, DeviceConfiguration device)
        {
            _logger = logger;
            _config = OpcUaDriverConfig.Parse(device.ConnectionString);
        }

        public DriverStatus DriverStatus
        {
            get { lock (_stateLock) return _status; }
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await DisconnectAsync(ct).ConfigureAwait(false);
            SetStatus(DriverStatus.Connecting);

            var appConfig = CreateApplicationConfig();
            await appConfig.ValidateAsync(ApplicationType.Client, ct).ConfigureAwait(false);

            var securityMode = ParseSecurityMode(_config.Security);
            var useSecurity = securityMode != MessageSecurityMode.None;

            var endpointDesc = await CoreClientUtils.SelectEndpointAsync(
                appConfig,
                _config.Endpoint,
                useSecurity,
                telemetry: null!,
                ct).ConfigureAwait(false);

            // 安全模式校验：配置了 Sign/SignAndEncrypt 时，端点实际模式不得低于要求，宁可直接失败也不静默降级
            if (endpointDesc is null)
                throw new InvalidOperationException($"OPC UA endpoint '{_config.Endpoint}' could not be resolved.");

            if (endpointDesc.SecurityMode < securityMode)
                throw new InvalidOperationException(
                    $"OPC UA endpoint '{_config.Endpoint}' security mode '{endpointDesc.SecurityMode}' " +
                    $"does not meet the configured requirement '{securityMode}'. " +
                    $"Set security:None to allow unencrypted connections.");

            var endpointConfiguration = EndpointConfiguration.Create(appConfig);
            var configuredEndpoint = new ConfiguredEndpoint(null, endpointDesc, endpointConfiguration);

            var userIdentity = CreateUserIdentity();
            var sessionFactory = new DefaultSessionFactory(telemetry: null!);

            ISession session = await sessionFactory.CreateAsync(
                appConfig,
                (ITransportWaitingConnection)null!,
                configuredEndpoint,
                true,
                false,
                appConfig.ApplicationName!,
                (uint)_config.SessionTimeout,
                userIdentity,
                null,
                ct).ConfigureAwait(false);

            var typedSession = (Session)session;
            typedSession.KeepAliveInterval = (int)TimeSpan.FromSeconds(30).TotalMilliseconds;
            typedSession.KeepAlive += (_, e) =>
            {
                if (ServiceResult.IsBad(e.Status))
                    SetStatus(DriverStatus.Faulted);
            };

            lock (_stateLock)
            {
                _session = typedSession;
                _status = DriverStatus.Connected;
            }
            OpcUaLog.LogConnected(_logger, _config.Endpoint);
        }

        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            Session? session;
            lock (_stateLock)
            {
                _subscriptions = null;
                session = _session;
                _session = null;
                _status = DriverStatus.Disconnected;
            }

            if (session is not null)
            {
                try { await session.CloseAsync(ct).ConfigureAwait(false); }
                catch { }
                session.Dispose();
            }
        }

        public async Task<bool> TryReconnectAsync(CancellationToken ct = default)
        {
            try
            {
                await DisconnectAsync(ct).ConfigureAwait(false);
                await ConnectAsync(ct).ConfigureAwait(false);
                OpcUaLog.LogReconnected(_logger);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetStatus(DriverStatus.Faulted);
                OpcUaLog.LogReconnectFailed(_logger, ex);
                return false;
            }
        }

        public async Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
        {
            var session = GetSessionOrThrow();
            if (points.Length == 0) return [];

            var results = new DriverResult[points.Length];
            Dictionary<int, int> validIndices = [];
            var collection = new ReadValueIdCollection();

            for (var i = 0; i < points.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (TryParseNodeId(points[i].Address, out var nodeId))
                {
                    validIndices[collection.Count] = i;
                    collection.Add(new ReadValueId { NodeId = nodeId, AttributeId = Attributes.Value });
                }
                else
                {
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure, "Invalid OPC UA address");
                    OpcUaLog.LogInvalidAddress(_logger, points[i].Address);
                }
            }

            if (collection.Count > 0)
            {
                try
                {
                    var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both, collection, ct).ConfigureAwait(false);
                    for (var j = 0; j < response.Results.Count; j++)
                    {
                        var idx = validIndices[j];
                        var dv = response.Results[j];
                        results[idx] = StatusCode.IsGood(dv.StatusCode)
                            ? DriverResult.Good(points[idx].Address, dv.GetValue(null))
                            : DriverResult.Bad(points[idx].Address, QualityCode.BadCommFailure, dv.StatusCode.ToString());
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    foreach (var kv in validIndices)
                        results[kv.Value] = DriverResult.Bad(points[kv.Value].Address, QualityCode.BadCommFailure, ex.Message);
                    OpcUaLog.LogReadPointFailed(_logger, ex, "batch");
                }
            }

            return results;
        }

        public async Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            var session = GetSessionOrThrow();
            if (values.Count == 0) return [];

            var entryList = values.ToList();
            var results = new DriverResult[entryList.Count];
            Dictionary<int, int> validIndices = [];
            var collection = new WriteValueCollection();

            for (var i = 0; i < entryList.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (TryParseNodeId(entryList[i].Key.Address, out var nodeId))
                {
                    validIndices[collection.Count] = i;
                    collection.Add(new WriteValue
                    {
                        NodeId = nodeId,
                        AttributeId = Attributes.Value,
                        Value = new DataValue(new Variant(entryList[i].Value))
                    });
                }
                else
                {
                    results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadCommFailure, "Invalid OPC UA address");
                }
            }

            if (collection.Count > 0)
            {
                try
                {
                    var response = await session.WriteAsync(null, collection, ct).ConfigureAwait(false);
                    for (var j = 0; j < response.Results.Count; j++)
                    {
                        var idx = validIndices[j];
                        results[idx] = StatusCode.IsGood(response.Results[j])
                            ? DriverResult.Good(entryList[idx].Key.Address, null)
                            : DriverResult.Bad(entryList[idx].Key.Address, QualityCode.BadCommFailure, response.Results[j].ToString());
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    foreach (var kv in validIndices)
                        results[kv.Value] = DriverResult.Bad(entryList[kv.Value].Key.Address, QualityCode.BadCommFailure, ex.Message);
                    OpcUaLog.LogWritePointFailed(_logger, ex, "batch");
                }
            }

            return results;
        }

        public async Task StartPushingAsync(
            TagPointConfiguration[] points,
            Func<DriverResult, CancellationToken, ValueTask> onData,
            CancellationToken ct)
        {
            var session = GetSessionOrThrow();

            var sub = new Subscription(null!, false)
            {
                PublishingInterval = _config.PublishingInterval,
                KeepAliveCount = 10,
                LifetimeCount = 20,
                MaxNotificationsPerPublish = 1000,
                Priority = 0,
                PublishingEnabled = true
            };

            var validPoints = new (int OriginalIndex, string Address, NodeId NodeId)[points.Length];
            var vpCount = 0;
            for (var i = 0; i < points.Length; i++)
            {
                if (TryParseNodeId(points[i].Address, out var nodeId))
                    validPoints[vpCount++] = (i, points[i].Address, nodeId);
                else
                    OpcUaLog.LogInvalidAddress(_logger, points[i].Address);
            }

            var addresses = new string[vpCount];
            for (var i = 0; i < vpCount; i++)
                addresses[i] = validPoints[i].Address;

            session.AddSubscription(sub);
            try
            {
                await sub.CreateAsync(ct).ConfigureAwait(false);

                for (var i = 0; i < vpCount; i++)
                {
                    var (origIdx, _, nodeId) = validPoints[i];
                    var item = new MonitoredItem(null!, false, false)
                    {
                        DisplayName = points[origIdx].TagId,
                        StartNodeId = nodeId,
                        AttributeId = Attributes.Value,
                        SamplingInterval = points[origIdx].SamplingInterval > 0
                            ? points[origIdx].SamplingInterval : _config.PublishingInterval,
                        QueueSize = (uint)Math.Max(1, points[origIdx].QueueSize),
                        DiscardOldest = true
                    };
                    sub.AddItem(item);
                }

                await sub.ApplyChangesAsync(ct).ConfigureAwait(false);

                var handleToIndex = new Dictionary<uint, int>();
                var monitoredItems = sub.MonitoredItems.ToArray();
                for (var i = 0; i < vpCount; i++)
                    handleToIndex[monitoredItems[i].ClientHandle] = i;

                sub.FastDataChangeCallback = (_, notification, __) =>
                {
                    foreach (var item in notification.MonitoredItems)
                    {
                        if (!handleToIndex.TryGetValue(item.ClientHandle, out var idx)) continue;

                        try
                        {
                            var result = item.Value.Value is not null
                                ? DriverResult.Good(addresses[idx], item.Value.Value)
                                : DriverResult.Bad(addresses[idx], QualityCode.BadCommFailure, "Null value");
                            onData(result, ct).AsTask().ContinueWith(t =>
                            {
                                if (t.IsFaulted && t.Exception is not null)
                                    OpcUaLog.LogReadPointFailed(_logger, t.Exception.InnerException!, addresses[idx]);
                            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        }
                        catch (Exception ex)
                        {
                            OpcUaLog.LogReadPointFailed(_logger, ex, addresses[idx]);
                        }
                    }
                };
            }
            catch
            {
                try
                {
#pragma warning disable CS0618
                    session.RemoveSubscription(sub);
#pragma warning restore CS0618
                }
                catch { }
                sub.Dispose();
                throw;
            }

            lock (_stateLock) { _subscriptions = new[] { sub }; }
            OpcUaLog.LogSubscriptionStarted(_logger, vpCount, _config.PublishingInterval);

            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        public Task StopPushingAsync(CancellationToken ct)
        {
            IList<Subscription>? subs;
            lock (_stateLock)
            {
                subs = _subscriptions;
                _subscriptions = null;
            }

            if (subs is not null)
            {
                foreach (var s in subs)
                {
                    try { s.Dispose(); }
                    catch { }
                }
            }
            OpcUaLog.LogSubscriptionStopped(_logger);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await StopPushingAsync(default).ConfigureAwait(false);
            await DisconnectAsync().ConfigureAwait(false);
            OpcUaLog.LogDisposed(_logger);
        }

        private ApplicationConfiguration CreateApplicationConfig()
        {
            var config = new ApplicationConfiguration
            {
                ApplicationName = "PlcLibrary.OpcUa",
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = _config.PkiOwnPath,
                        SubjectName = "CN=PlcLibrary OPC UA Client"
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = _config.PkiTrustedPath
                    },
                    RejectedCertificateStore = new CertificateStoreIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = _config.PkiRejectedPath
                    },
                    AutoAcceptUntrustedCertificates = _config.AutoAcceptCertificate,
                    RejectSHA1SignedCertificates = false
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = _config.Timeout },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = _config.SessionTimeout },
                TraceConfiguration = new TraceConfiguration()
            };

            return config;
        }

        private IUserIdentity CreateUserIdentity()
        {
            if (!string.IsNullOrEmpty(_config.UserName))
                return new UserIdentity(_config.UserName, Encoding.UTF8.GetBytes(_config.Password ?? ""));
            return new UserIdentity(new AnonymousIdentityToken());
        }

        /// <summary>连接串 security 字段 → MessageSecurityMode 映射（大小写不敏感，默认 None）。</summary>
        private static MessageSecurityMode ParseSecurityMode(string? security)
            => security?.Trim().ToUpperInvariant() switch
            {
                "SIGN" => MessageSecurityMode.Sign,
                "SIGNANDENCRYPT" => MessageSecurityMode.SignAndEncrypt,
                _ => MessageSecurityMode.None
            };

        private Session GetSessionOrThrow()
        {
            lock (_stateLock)
            {
                if (_session is null || _status != DriverStatus.Connected)
                    throw new InvalidOperationException("OPC UA driver is not connected");
                return _session;
            }
        }

        private void SetStatus(DriverStatus status)
        {
            lock (_stateLock) { _status = status; }
        }

        private static bool TryParseNodeId(string address, out NodeId nodeId)
        {
            nodeId = null!;
            if (string.IsNullOrWhiteSpace(address)) return false;
            try
            {
                nodeId = NodeId.Parse(address);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
