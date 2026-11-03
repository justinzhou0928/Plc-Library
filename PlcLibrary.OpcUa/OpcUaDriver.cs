using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using PlcLibrary.DriverDomain;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.OpcUa
{
    /// <summary>OPC UA 客户端驱动，支持读/写、订阅推送和标签浏览</summary>
    [ProtocolDriverName("OPC_UA")]
    public sealed class OpcUaDriver : ProtocolDriverBase<Session>, IPushProtocolDriver, ITagBrowser
    {
        private const int KeepAliveIntervalSeconds = 30;
        private const uint SubscriptionKeepAliveCount = 10;
        private const uint SubscriptionLifetimeCount = 20;
        private const uint MaxNotificationsPerPublish = 1000;
        private const int PushQueueCapacity = 1024;

        private readonly ILogger<OpcUaDriver> _logger;
        private readonly OpcUaDriverConfig _config;
        private IList<Subscription>? _subscriptions;
        private KeepAliveEventHandler? _keepAliveHandler;
        private int _disposed;

        /// <summary>创建 OPC UA 驱动</summary>
        /// <param name="logger">日志</param>
        /// <param name="device">设备配置</param>
        public OpcUaDriver(ILogger<OpcUaDriver> logger, DeviceConfiguration device)
        {
            _logger = logger;
            _config = OpcUaDriverConfig.Parse(device.ConnectionString);
        }

        /// <inheritdoc/>
        public override async Task ConnectAsync(CancellationToken ct = default)
        {
            await DisconnectAsync(ct).ConfigureAwait(false);
            SetStatus(DriverStatus.Connecting);

            try
            {
                var appConfig = CreateApplicationConfig(_config);
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
                typedSession.KeepAliveInterval = (int)TimeSpan.FromSeconds(KeepAliveIntervalSeconds).TotalMilliseconds;
                var keepAliveHandler = new KeepAliveEventHandler((_, e) =>
                {
                    if (ServiceResult.IsBad(e.Status))
                        SetStatus(DriverStatus.Faulted);
                });
                typedSession.KeepAlive += keepAliveHandler;

                lock (StateLock)
                {
                    _keepAliveHandler = keepAliveHandler;
                    SetState(typedSession, DriverStatus.Connected);
                }
                OpcUaLog.LogConnected(_logger, _config.Endpoint);
            }
            catch
            {
                // ConnectAsync 失败必须置 Faulted（与其它驱动/DEVELOPMENT 契约一致），
                // 否则池/监督循环看到的是长期 Connecting。
                SetStatus(DriverStatus.Faulted);
                throw;
            }
        }

        /// <inheritdoc/>
        public override async Task DisconnectAsync(CancellationToken ct = default)
        {
            Session? session;
            KeepAliveEventHandler? keepAliveHandler;
            lock (StateLock)
            {
                _subscriptions = null;
                keepAliveHandler = _keepAliveHandler;
                _keepAliveHandler = null;
                session = DetachClient();
            }

            if (session is not null)
            {
                if (keepAliveHandler is not null)
                {
                    try
                    { session.KeepAlive -= keepAliveHandler; }
                    catch { }
                }

                try
                { await session.CloseAsync(ct).ConfigureAwait(false); }
                catch { }
                session.Dispose();
            }
        }

        /// <inheritdoc/>
        protected override void OnReconnected() => OpcUaLog.LogReconnected(_logger);

        /// <inheritdoc/>
        protected override void OnReconnectFailed(Exception ex) => OpcUaLog.LogReconnectFailed(_logger, ex);

        /// <inheritdoc/>
        public override async Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
        {
            var session = GetClientOrThrow("OPC UA");
            if (points.Length == 0)
                return [];

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
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadConfigError, "Invalid OPC UA address");
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
                        var result = StatusCode.IsGood(dv.StatusCode)
                            ? DriverResult.Good(points[idx].Address, dv.GetValue(null))
                            : DriverResult.Bad(points[idx].Address, QualityCode.BadCommFailure, dv.StatusCode.ToString());
                        results[idx] = WithTimestamps(result, dv);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    MarkFaultedIfTransport(ex);
                    foreach (var kv in validIndices)
                        results[kv.Value] = DriverResult.Bad(points[kv.Value].Address, QualityCode.BadCommFailure, ex.Message);
                    OpcUaLog.LogReadPointFailed(_logger, ex, "batch");
                }
            }

            return results;
        }

        /// <inheritdoc/>
        public override async Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            var session = GetClientOrThrow("OPC UA");
            if (values.Count == 0)
                return [];

            var entryList = values.ToList();
            var results = new DriverResult[entryList.Count];
            Dictionary<int, int> validIndices = [];
            var collection = new WriteValueCollection();

            for (var i = 0; i < entryList.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (TryCreateWriteValue(entryList[i].Key, entryList[i].Value, out var writeValue, out var error))
                {
                    validIndices[collection.Count] = i;
                    collection.Add(writeValue!);
                }
                else
                {
                    // 单点值无法编码为 Variant 时只影响该点，不能让整批写抛异常。
                    results[i] = DriverResult.Bad(
                        entryList[i].Key.Address, QualityCode.BadConfigError, error ?? "Invalid OPC UA write value");
                }
            }

            if (collection.Count > 0)
            {
                try
                {
                    var response = await session.WriteAsync(null, collection, ct).ConfigureAwait(false);
                    for (var j = 0; j < collection.Count; j++)
                    {
                        var idx = validIndices[j];
                        if (j >= response.Results.Count)
                        {
                            results[idx] = DriverResult.Bad(
                                entryList[idx].Key.Address, QualityCode.BadCommFailure, "No write result returned by server");
                            continue;
                        }

                        results[idx] = StatusCode.IsGood(response.Results[j])
                            ? DriverResult.Good(entryList[idx].Key.Address, null)
                            : DriverResult.Bad(entryList[idx].Key.Address, QualityCode.BadCommFailure, response.Results[j].ToString());
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    MarkFaultedIfTransport(ex);
                    foreach (var kv in validIndices)
                        results[kv.Value] = DriverResult.Bad(entryList[kv.Value].Key.Address, QualityCode.BadCommFailure, ex.Message);
                    OpcUaLog.LogWritePointFailed(_logger, ex, "batch");
                }
            }

            return results;
        }

        /// <summary>
        /// 浏览指定节点（null/空 = Objects 文件夹）的直接子节点。返回的 <see cref="TagDescriptor.Address"/>
        /// 可直接用于点位，也可继续传入本方法递归浏览。
        /// </summary>
        public async Task<IReadOnlyList<TagDescriptor>> BrowseAsync(
            string? address = null, CancellationToken ct = default)
        {
            var session = GetClientOrThrow("OPC UA");

            NodeId nodeId;
            try
            {
                nodeId = string.IsNullOrWhiteSpace(address)
                    ? ObjectIds.ObjectsFolder
                    : NodeId.Parse(address);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Invalid OPC UA browse address '{address}'.", ex);
            }

            var options = new BrowserOptions
            {
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (int)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (uint)BrowseResultMask.All,
            };

            var browser = new Browser(session, options);
            var references = (await browser.BrowseAsync(nodeId, ct).ConfigureAwait(false)).ToArray();

            var descriptors = new List<TagDescriptor>(references.Length);
            var variableIndices = new List<int>();
            var readValues = new ReadValueIdCollection();

            foreach (var reference in references)
            {
                var descriptor = new TagDescriptor
                {
                    Address = reference.NodeId.ToString(),
                    Name = reference.DisplayName?.Text
                        ?? reference.BrowseName?.Name
                        ?? reference.NodeId.ToString(),
                    IsFolder = reference.NodeClass is NodeClass.Object or NodeClass.View,
                };

                if (reference.NodeClass == NodeClass.Variable)
                {
                    variableIndices.Add(descriptors.Count);
                    var referenceNodeId = NodeId.Parse(reference.NodeId.ToString());
                    readValues.Add(new ReadValueId { NodeId = referenceNodeId, AttributeId = Attributes.DataType });
                    readValues.Add(new ReadValueId { NodeId = referenceNodeId, AttributeId = Attributes.AccessLevel });
                    readValues.Add(new ReadValueId { NodeId = referenceNodeId, AttributeId = Attributes.Description });
                }

                descriptors.Add(descriptor);
            }

            if (readValues.Count > 0)
            {
                try
                {
                    var response = await session.ReadAsync(
                        null, 0, TimestampsToReturn.Neither, readValues, ct).ConfigureAwait(false);

                    for (var i = 0; i < variableIndices.Count; i++)
                    {
                        var descriptor = descriptors[variableIndices[i]];
                        var dataTypeIndex = i * 3;
                        var accessIndex = dataTypeIndex + 1;
                        var descriptionIndex = dataTypeIndex + 2;
                        if (dataTypeIndex >= response.Results.Count)
                            continue;

                        if (response.Results[dataTypeIndex].Value is NodeId dataType)
                            descriptor = descriptor with { DataType = DataTypes.GetBuiltInType(dataType).ToString() };

                        if (accessIndex < response.Results.Count
                            && response.Results[accessIndex].Value is byte accessLevel)
                        {
                            descriptor = descriptor with
                            {
                                IsWritable = (accessLevel & AccessLevels.CurrentWrite) != 0,
                            };
                        }

                        if (descriptionIndex < response.Results.Count
                            && response.Results[descriptionIndex].Value is LocalizedText description
                            && !string.IsNullOrEmpty(description.Text))
                        {
                            descriptor = descriptor with { Description = description.Text };
                        }

                        descriptors[variableIndices[i]] = descriptor;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 子节点已浏览成功，属性读取失败只降级 DataType/IsWritable，不丢掉整棵目录。
                    OpcUaLog.LogReadPointFailed(_logger, ex, "browse-attributes");
                }
            }

            return descriptors;
        }

        /// <inheritdoc/>
        public async Task StartPushingAsync(
            TagPointConfiguration[] points,
            Func<DriverResult, CancellationToken, ValueTask> onData,
            CancellationToken ct)
        {
            var session = GetClientOrThrow("OPC UA");

            var sub = CreateSubscription(_config);

            var validPoints = new (int OriginalIndex, string Address, NodeId NodeId)[points.Length];
            var invalidResults = new List<DriverResult>();
            var vpCount = 0;
            for (var i = 0; i < points.Length; i++)
            {
                if (TryParseNodeId(points[i].Address, out var nodeId))
                    validPoints[vpCount++] = (i, points[i].Address, nodeId);
                else
                {
                    OpcUaLog.LogInvalidAddress(_logger, points[i].Address);
                    // 读路径对非法 NodeId 返回 BadConfigError；推送路径也必须让配置错误进入数据流。
                    // 先缓存，等订阅建立后再回调，避免宿主在回调里取消令牌导致订阅创建被中断。
                    invalidResults.Add(DriverResult.Bad(
                        points[i].Address, QualityCode.BadConfigError, "Invalid OPC UA address"));
                }
            }

            var addresses = new string[vpCount];
            for (var i = 0; i < vpCount; i++)
                addresses[i] = validPoints[i].Address;

            // SDK 的 FastDataChangeCallback 是同步回调；异步写管道不能直接 fire-and-forget，
            // 否则同一订阅的连续通知可能乱序，且停机时仍在写。这里用单消费者队列串行化。
            var pending = Channel.CreateBounded<DriverResult>(new BoundedChannelOptions(PushQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite,
            });

            Task? consumer = null;
            session.AddSubscription(sub);
            try
            {
                await sub.CreateAsync(ct).ConfigureAwait(false);

                for (var i = 0; i < vpCount; i++)
                {
                    var (origIdx, _, nodeId) = validPoints[i];
                    var item = CreateMonitoredItem(points[origIdx], nodeId, _config.PublishingInterval);
                    sub.AddItem(item);
                }

                await sub.ApplyChangesAsync(ct).ConfigureAwait(false);

                foreach (var invalid in invalidResults)
                    await onData(invalid, ct).ConfigureAwait(false);

                var handleToIndex = new Dictionary<uint, int>();
                var monitoredItems = sub.MonitoredItems.ToArray();
                for (var i = 0; i < vpCount; i++)
                    handleToIndex[monitoredItems[i].ClientHandle] = i;

                sub.FastDataChangeCallback = (_, notification, __) =>
                {
                    foreach (var item in notification.MonitoredItems)
                    {
                        if (!handleToIndex.TryGetValue(item.ClientHandle, out var idx))
                            continue;

                        try
                        {
                            var result = CreatePushResult(addresses[idx], item.Value);
                            if (!pending.Writer.TryWrite(result))
                                OpcUaLog.LogPushQueueFull(_logger, addresses[idx]);
                        }
                        catch (Exception ex)
                        {
                            OpcUaLog.LogReadPointFailed(_logger, ex, addresses[idx]);
                        }
                    }
                };

                consumer = ConsumePushQueueAsync(pending.Reader, onData, ct);
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

            lock (StateLock)
            { _subscriptions = new[] { sub }; }
            OpcUaLog.LogSubscriptionStarted(_logger, vpCount, _config.PublishingInterval);

            try
            { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally
            {
                pending.Writer.TryComplete();
                if (consumer is not null)
                {
                    try
                    { await consumer.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
        }

        /// <summary>串行消费订阅通知，保证同一订阅的 onData 不并发、不乱序。</summary>
        private async Task ConsumePushQueueAsync(
            ChannelReader<DriverResult> reader,
            Func<DriverResult, CancellationToken, ValueTask> onData,
            CancellationToken ct)
        {
            try
            {
                await foreach (var result in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try
                    { await onData(result, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        OpcUaLog.LogNotificationFailed(_logger, ex, result.Address);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <inheritdoc/>
        public Task StopPushingAsync(CancellationToken ct)
        {
            IList<Subscription>? subs;
            Session? session;
            lock (StateLock)
            {
                subs = _subscriptions;
                _subscriptions = null;
                session = Client;
            }

            if (subs is not null)
            {
                foreach (var s in subs)
                {
                    if (session is not null)
                    {
                        try
                        {
#pragma warning disable CS0618
                            session.RemoveSubscription(s);
#pragma warning restore CS0618
                        }
                        catch { }
                    }

                    try
                    { s.Dispose(); }
                    catch { }
                }
            }
            OpcUaLog.LogSubscriptionStopped(_logger);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await StopPushingAsync(default).ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
            OpcUaLog.LogDisposed(_logger);
        }

        internal static Subscription CreateSubscription(OpcUaDriverConfig config)
            => new(null!, new SubscriptionOptions
            {
                PublishingInterval = config.PublishingInterval,
                KeepAliveCount = SubscriptionKeepAliveCount,
                LifetimeCount = SubscriptionLifetimeCount,
                MaxNotificationsPerPublish = MaxNotificationsPerPublish,
                Priority = 0,
                PublishingEnabled = true
            });

        internal static MonitoredItem CreateMonitoredItem(
            TagPointConfiguration point, NodeId nodeId, int defaultSamplingInterval)
            => new(null!, new MonitoredItemOptions
            {
                DisplayName = point.TagId,
                StartNodeId = nodeId,
                AttributeId = Attributes.Value,
                SamplingInterval = point.SamplingInterval > 0
                    ? point.SamplingInterval
                    : defaultSamplingInterval,
                QueueSize = (uint)Math.Max(1, point.QueueSize),
                DiscardOldest = true
            });

        internal static ApplicationConfiguration CreateApplicationConfig(OpcUaDriverConfig driverConfig)
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
                        StorePath = driverConfig.PkiOwnPath,
                        SubjectName = "CN=PlcLibrary OPC UA Client"
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = driverConfig.PkiTrustedPath
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = driverConfig.PkiIssuerPath
                    },
                    RejectedCertificateStore = new CertificateStoreIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = driverConfig.PkiRejectedPath
                    },
                    AutoAcceptUntrustedCertificates = driverConfig.AutoAcceptCertificate,
                    RejectSHA1SignedCertificates = false
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = driverConfig.Timeout },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = driverConfig.SessionTimeout },
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

        /// <summary>
        /// OPC UA 通知的 DataValue 可能携带 Bad StatusCode（值仍非 null）。订阅路径不能只看 Value，
        /// 否则传感器故障/通信错误会被当成 Good 数据进入缓存。
        /// </summary>
        internal static DriverResult CreatePushResult(string address, DataValue? value)
        {
            if (value?.Value is null)
                return DriverResult.Bad(address, QualityCode.BadCommFailure, "Null value");

            var result = StatusCode.IsGood(value.StatusCode)
                ? DriverResult.Good(address, value.Value)
                : DriverResult.Bad(address, QualityCode.BadCommFailure, value.StatusCode.ToString());

            return WithTimestamps(result, value);
        }

        private static DriverResult WithTimestamps(DriverResult result, DataValue value)
            => result with
            {
                SourceTimestamp = NormalizeTimestamp(value.SourceTimestamp),
                ServerTimestamp = NormalizeTimestamp(value.ServerTimestamp),
            };

        private static DateTime? NormalizeTimestamp(DateTime value)
            => value == DateTime.MinValue ? null : value;

        /// <summary>OPC UA 的服务级/传输级异常置 Faulted，使池按需重连。</summary>
        protected override void MarkFaultedIfTransport(Exception ex)
        {
            base.MarkFaultedIfTransport(ex);
            if (ex is ServiceResultException { StatusCode: var statusCode } && StatusCode.IsBad(statusCode))
            {
                SetStatus(DriverStatus.Faulted);
            }
        }

        /// <summary>连接串 security 字段 → MessageSecurityMode；非法值直接抛异常，不静默降级。</summary>
        private static MessageSecurityMode ParseSecurityMode(string? security)
            => OpcUaDriverConfig.TryParseSecurityMode(security, out var mode)
                ? mode
                : throw new InvalidOperationException(
                    $"Invalid OPC UA security '{security}'. Allowed values: None, Sign, SignAndEncrypt.");

        /// <summary>
        /// 将单个点位编码为 OPC UA WriteValue；地址或值不合法时返回 false，由调用方标 BadConfigError。
        /// 这里必须逐点隔离：Variant 对不支持的类型会抛异常，不能让整批写失败。
        /// </summary>
        internal static bool TryCreateWriteValue(
            TagPointConfiguration point, object? value, out WriteValue? writeValue, out string? error)
        {
            writeValue = null;
            error = null;
            if (!TryParseNodeId(point.Address, out var nodeId))
            {
                error = "Invalid OPC UA address";
                return false;
            }

            try
            {
                writeValue = new WriteValue
                {
                    NodeId = nodeId,
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(value))
                };
                return true;
            }
            catch (Exception ex)
            {
                error = $"OPC UA value of type '{value?.GetType().Name ?? "null"}' cannot be written: {ex.Message}";
                return false;
            }
        }

        private static bool TryParseNodeId(string address, out NodeId nodeId)
        {
            nodeId = null!;
            if (string.IsNullOrWhiteSpace(address))
                return false;
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
