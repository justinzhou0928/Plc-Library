using Microsoft.Extensions.Logging;
using PlcLibrary.DriverDomain;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Bacnet
{
    [ProtocolDriverName("BACnet")]
    public sealed class BacnetDriver : IProtocolDriver
    {
        private readonly ILogger<BacnetDriver> _logger;
        private readonly BacnetDriverConfig _config;
        private readonly object _stateLock = new();
        private BacnetClient? _client;
        private BacnetAddress? _deviceAddress;
        private DriverStatus _status = DriverStatus.Disconnected;
        private int _disposed;

        public BacnetDriver(ILogger<BacnetDriver> logger, DeviceConfiguration device)
        {
            _logger = logger;
            _config = BacnetDriverConfig.Parse(device.ConnectionString);
        }

        public DriverStatus DriverStatus
        {
            get { lock (_stateLock) return _status; }
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            DisconnectInternal();
            SetState(null, DriverStatus.Connecting);

            BacnetClient? client = null;
            try
            {
                var transport = string.IsNullOrEmpty(_config.LocalEndpointIp)
                    ? new BacnetIpUdpProtocolTransport(_config.Port)
                    : new BacnetIpUdpProtocolTransport(_config.Port, localEndpointIp: _config.LocalEndpointIp);

                client = new BacnetClient(transport);
                client.Start();

                var deviceAddress = await ResolveDeviceAddressAsync(client, ct).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _client = client;
                    _deviceAddress = deviceAddress;
                    _status = DriverStatus.Connected;
                }
                BacnetLog.LogConnected(_logger, _config.Host, _config.Port);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetState(null, DriverStatus.Faulted);
                if (client is not null)
                {
                    try { client.Dispose(); } catch { }
                }
                BacnetLog.LogConnectionFailed(_logger, ex, _config.Host, _config.Port);
                throw;
            }
        }

        /// <summary>
        /// 解析目标设备网络地址：DeviceInstance == 0 时直接用 host + port；
        /// 否则通过 WhoIs/IAm 广播发现实例号对应的地址（未找到抛 TimeoutException）。
        /// </summary>
        private async Task<BacnetAddress> ResolveDeviceAddressAsync(BacnetClient client, CancellationToken ct)
        {
            if (_config.DeviceInstance == 0)
            {
                // BacnetAddress(IP, address, network) 的第三个参数是 BACnet 网络号，不是端口；
                // 端口必须写在地址字符串里（ip:port），否则远端永远是 47808。
                var addressText = _config.Host.Contains(':', StringComparison.Ordinal)
                    ? _config.Host
                    : $"{_config.Host}:{_config.Port}";
                return new BacnetAddress(BacnetAddressTypes.IP, addressText, 0);
            }

            var tcs = new TaskCompletionSource<BacnetAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
            BacnetClient.IamHandler handler = null!;
            handler = (_, adr, deviceId, _, _, _) =>
            {
                if (deviceId == _config.DeviceInstance)
                    tcs.TrySetResult(adr);
            };
            client.OnIam += handler;
            try
            {
                client.WhoIs((int)_config.DeviceInstance, (int)_config.DeviceInstance, null!, null!);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_config.Timeout));
                return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"BACnet device instance {_config.DeviceInstance} not found within {_config.Timeout}ms");
            }
            finally
            {
                client.OnIam -= handler;
            }
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            DisconnectInternal();
            return Task.CompletedTask;
        }

        private void DisconnectInternal()
        {
            BacnetClient? oldClient;
            lock (_stateLock)
            {
                oldClient = _client;
                _client = null;
                _deviceAddress = null;
                _status = DriverStatus.Disconnected;
            }

            if (oldClient is not null)
            {
                oldClient.Dispose();
            }
        }

        public async Task<bool> TryReconnectAsync(CancellationToken ct = default)
        {
            try
            {
                await DisconnectAsync(ct).ConfigureAwait(false);
                await ConnectAsync(ct).ConfigureAwait(false);
                BacnetLog.LogReconnected(_logger);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_stateLock) _status = DriverStatus.Faulted;
                BacnetLog.LogReconnectFailed(_logger, ex);
                return false;
            }
        }

        public async Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
        {
            var (client, deviceAddress) = GetClientOrThrow();
            if (points.Length == 0) return [];

            // 默认填充 Bad：厂商设备可能返回不完整的 RMP-ACK（省略某些对象），
            // 不能把数组默认值（Status=Good）当成有效数据返回。
            var results = new DriverResult[points.Length];
            for (var i = 0; i < points.Length; i++)
                results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure,
                    "No response for object");

            // 按对象分组：同 objectId 的点位一次 ReadPropertyMultiple 批量读取
            var groups = new Dictionary<BacnetObjectId, List<int>>();
            for (var i = 0; i < points.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!TryParseBacnetAddress(points[i].Address, out var objType, out var instance, out var error))
                {
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadConfigError, error);
                    continue;
                }

                var objId = new BacnetObjectId(objType, instance);
                if (!groups.TryGetValue(objId, out var indices))
                    groups[objId] = indices = [];
                indices.Add(i);
            }

            if (groups.Count > 0)
            {
                var specs = groups.Keys
                    .Select(id => new BacnetReadAccessSpecification(id,
                        new List<BacnetPropertyReference>
                        {
                            new(BacnetPropertyIds.PROP_PRESENT_VALUE, ASN1.BACNET_ARRAY_ALL),
                        }))
                    .ToList();
                try
                {
                    var responses = await WithTimeoutAsync(
                        token => client.ReadPropertyMultipleAsync(deviceAddress, specs, 0, token),
                        ct).ConfigureAwait(false);
                    foreach (var resp in responses)
                    {
                        if (!groups.TryGetValue(resp.objectIdentifier, out var indices)) continue;

                        var propVal = resp.values.FirstOrDefault(
                            v => v.property.propertyIdentifier == (uint)BacnetPropertyIds.PROP_PRESENT_VALUE);
                        var value = propVal.value is { Count: > 0 } ? propVal.value[0].Value : null;

                        foreach (var idx in indices)
                        {
                            // 逐对象错误在 RMP-ACK 中以 BacnetError 形式返回，必须降级为 Bad，
                            // 否则错误对象会被当成 Good 值进入缓存/监控（静默坏数据）。
                            results[idx] = value switch
                            {
                                null => DriverResult.Bad(points[idx].Address, QualityCode.BadCommFailure,
                                    "Read returned null or empty"),
                                BacnetError bacnetError => DriverResult.Bad(points[idx].Address,
                                    QualityCode.BadCommFailure, $"BACnet error: {bacnetError}"),
                                _ => DriverResult.Good(points[idx].Address, value),
                            };
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    BacnetLog.LogReadPointFailed(_logger, ex, "batch");
                    MarkFaultedIfTransport(ex);
                    foreach (var (_, indices) in groups)
                        foreach (var idx in indices)
                            results[idx] = DriverResult.Bad(points[idx].Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            var (client, deviceAddress) = GetClientOrThrow();
            if (values.Count == 0) return [];

            var entryList = values.ToList();
            var results = new DriverResult[entryList.Count];

            for (var i = 0; i < entryList.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!TryParseBacnetAddress(entryList[i].Key.Address, out var objType, out var instance, out var error))
                {
                    results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadConfigError, error);
                    continue;
                }

                try
                {
                    var objId = new BacnetObjectId(objType, instance);
                    var bacnetValue = new BacnetValue(entryList[i].Value);
                    // 参数顺序：invokeId, priority, arrayIndex, ct。Present_Value 不是数组属性，
                    // arrayIndex 必须用 BACNET_ARRAY_ALL（0xFFFFFFFF），不能把超时值传进来。
                    await WithTimeoutAsync(
                        token => client.WritePropertyAsync(deviceAddress, objId,
                            BacnetPropertyIds.PROP_PRESENT_VALUE, [bacnetValue], 0, null,
                            ASN1.BACNET_ARRAY_ALL, token),
                        ct).ConfigureAwait(false);

                    results[i] = DriverResult.Good(entryList[i].Key.Address, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    BacnetLog.LogWritePointFailed(_logger, ex, entryList[i].Key.Address);
                    MarkFaultedIfTransport(ex);
                    results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await DisconnectAsync().ConfigureAwait(false);
            BacnetLog.LogDisposed(_logger);
        }

        private (BacnetClient client, BacnetAddress deviceAddress) GetClientOrThrow()
        {
            lock (_stateLock)
            {
                if (_client is null || _deviceAddress is null || _status != DriverStatus.Connected)
                    throw new InvalidOperationException("BACnet driver is not connected");
                return (_client, _deviceAddress);
            }
        }

        private void SetState(BacnetClient? client, DriverStatus status)
        {
            lock (_stateLock)
            {
                _client = client;
                _status = status;
            }
        }

        /// <summary>
        /// 给单次 BACnet 读写套上配置的 <c>timeout</c>：BACnet 栈默认超时较长，
        /// 现场慢设备/不响应时不能拖住整个采集周期。超时转 <see cref="TimeoutException"/>（传输级）。
        /// </summary>
        private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            if (_config.Timeout <= 0) return await action(ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_config.Timeout);
            try
            {
                // 底层栈可能不响应取消令牌；WaitAsync 提供硬上限。
                return await action(timeoutCts.Token)
                    .WaitAsync(TimeSpan.FromMilliseconds(_config.Timeout) + TimeSpan.FromSeconds(1), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"BACnet operation timed out after {_config.Timeout}ms");
            }
            catch (TimeoutException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"BACnet operation timed out after {_config.Timeout}ms");
            }
        }

        private async Task WithTimeoutAsync(Func<CancellationToken, Task> action, CancellationToken ct)
            => await WithTimeoutAsync<bool>(async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);

        /// <summary>通信级故障时置 Faulted，连接池据此丢弃驱动并重建，使断线重连生效。</summary>
        private void MarkFaultedIfTransport(Exception ex)
        {
            if (TransportFailureDetector.IsTransportFailure(ex))
                lock (_stateLock) _status = DriverStatus.Faulted;
        }

        private static bool TryParseBacnetAddress(string address, out BacnetObjectTypes objType,
            out uint instance, out string error)
        {
            objType = BacnetObjectTypes.OBJECT_ANALOG_VALUE;
            instance = 0;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(address))
            {
                error = "Empty BACnet address";
                return false;
            }

            var parts = address.Split(':', 2);
            if (parts.Length != 2 || !uint.TryParse(parts[1], out instance))
            {
                error = $"Invalid BACnet address format: {address}. Expected format: TYPE:INSTANCE (e.g. AV:1, AI:0)";
                return false;
            }

            switch (parts[0].ToUpperInvariant())
            {
                case "AI": objType = BacnetObjectTypes.OBJECT_ANALOG_INPUT; break;
                case "AO": objType = BacnetObjectTypes.OBJECT_ANALOG_OUTPUT; break;
                case "AV": objType = BacnetObjectTypes.OBJECT_ANALOG_VALUE; break;
                case "BI": objType = BacnetObjectTypes.OBJECT_BINARY_INPUT; break;
                case "BO": objType = BacnetObjectTypes.OBJECT_BINARY_OUTPUT; break;
                case "BV": objType = BacnetObjectTypes.OBJECT_BINARY_VALUE; break;
                case "MI": objType = BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT; break;
                case "MO": objType = BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT; break;
                case "MV": objType = BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE; break;
                default:
                    error = $"Unknown BACnet object type '{parts[0]}'. Supported: AI/AO/AV/BI/BO/BV/MI/MO/MV";
                    return false;
            }

            return true;
        }
    }
}
