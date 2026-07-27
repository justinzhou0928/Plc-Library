using Microsoft.Extensions.Logging;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using System;
using System.Collections.Generic;
using System.IO.BACnet;
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

        public Task ConnectAsync(CancellationToken ct = default)
        {
            DisconnectInternal();
            SetState(null, DriverStatus.Connecting);

            try
            {
                var transport = string.IsNullOrEmpty(_config.LocalEndpointIp)
                    ? new BacnetIpUdpProtocolTransport(_config.Port)
                    : new BacnetIpUdpProtocolTransport(_config.Port, localEndpointIp: _config.LocalEndpointIp);

                var client = new BacnetClient(transport);
                client.Start();

                var deviceAddress = new BacnetAddress(BacnetAddressTypes.IP, _config.Host);

                lock (_stateLock)
                {
                    _client = client;
                    _deviceAddress = deviceAddress;
                    _status = DriverStatus.Connected;
                }
                BacnetLog.LogConnected(_logger, _config.Host, _config.Port);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetState(null, DriverStatus.Faulted);
                BacnetLog.LogConnectionFailed(_logger, ex, _config.Host, _config.Port);
                throw;
            }

            return Task.CompletedTask;
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

            var results = new DriverResult[points.Length];

            for (var i = 0; i < points.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!TryParseBacnetAddress(points[i].Address, out var objType, out var instance, out var error))
                {
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadConfigError, error);
                    continue;
                }

                try
                {
                    var objId = new BacnetObjectId(objType, instance);
                    var values = await client.ReadPropertyAsync(deviceAddress, objId,
                        BacnetPropertyIds.PROP_PRESENT_VALUE).ConfigureAwait(false);

                    if (values is { Count: > 0 } && values[0].Value is not null)
                        results[i] = DriverResult.Good(points[i].Address, values[0].Value);
                    else
                        results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure,
                            "Read returned null or empty");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    BacnetLog.LogReadPointFailed(_logger, ex, points[i].Address);
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure, ex.Message);
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
                    await client.WritePropertyAsync(deviceAddress, objId,
                        BacnetPropertyIds.PROP_PRESENT_VALUE, [bacnetValue]).ConfigureAwait(false);

                    results[i] = DriverResult.Good(entryList[i].Key.Address, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    BacnetLog.LogWritePointFailed(_logger, ex, entryList[i].Key.Address);
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

            objType = parts[0].ToUpperInvariant() switch
            {
                "AI" => BacnetObjectTypes.OBJECT_ANALOG_INPUT,
                "AO" => BacnetObjectTypes.OBJECT_ANALOG_OUTPUT,
                "AV" => BacnetObjectTypes.OBJECT_ANALOG_VALUE,
                "BI" => BacnetObjectTypes.OBJECT_BINARY_INPUT,
                "BO" => BacnetObjectTypes.OBJECT_BINARY_OUTPUT,
                "BV" => BacnetObjectTypes.OBJECT_BINARY_VALUE,
                "MI" => BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT,
                "MO" => BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT,
                "MV" => BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE,
                _ => BacnetObjectTypes.OBJECT_ANALOG_VALUE
            };

            return true;
        }
    }
}
