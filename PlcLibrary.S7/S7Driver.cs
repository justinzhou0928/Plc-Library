using Microsoft.Extensions.Logging;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using S7.Net;
using S7.Net.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.S7
{
    [ProtocolDriverName("S7")]
    public sealed class S7Driver : IProtocolDriver
    {
        private readonly ILogger<S7Driver>? _logger;
        private readonly S7DriverConfig _config;
        private Plc? _plc;
        private DriverStatus _status = DriverStatus.Disconnected;
        private readonly object _stateLock = new();

        public S7Driver(DeviceConfiguration device)
        {
            _config = S7DriverConfig.Parse(device.ConnectionString);
        }

        public S7Driver(ILogger<S7Driver> logger, DeviceConfiguration device) : this(device)
        {
            _logger = logger;
        }

        public DriverStatus DriverStatus
        {
            get { lock (_stateLock) return _status; }
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await DisconnectAsync(ct).ConfigureAwait(false);
            SetState(null, DriverStatus.Connecting);

            var plc = new Plc(_config.CpuType, _config.Host, _config.Port, _config.Rack, _config.Slot)
            {
                ReadTimeout = _config.Timeout,
                WriteTimeout = _config.Timeout
            };
            await plc.OpenAsync(ct).ConfigureAwait(false);

            SetState(plc, DriverStatus.Connected);
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            Plc? old;
            lock (_stateLock)
            {
                old = _plc;
                _plc = null;
                _status = DriverStatus.Disconnected;
            }
            try { old?.Close(); }
            catch { }
            return Task.CompletedTask;
        }

        public async Task<bool> TryReconnectAsync(CancellationToken ct = default)
        {
            try
            {
                await DisconnectAsync(ct).ConfigureAwait(false);
                await ConnectAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_stateLock) _status = DriverStatus.Faulted;
                return false;
            }
        }

        public async Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
        {
            var plc = GetPlcOrThrow();
            if (points.Length == 0) return [];

            var batchItems = new List<DataItem>(points.Length);
            var batchIndices = new List<int>(points.Length);
            var fallback = new bool[points.Length];
            var results = new DriverResult[points.Length];

            for (var i = 0; i < points.Length; i++)
            {
                try
                {
                    batchItems.Add(DataItem.FromAddress(points[i].Address));
                    batchIndices.Add(i);
                }
                catch (OperationCanceledException) { throw; }
                catch { fallback[i] = true; }
            }

            if (batchItems.Count > 0)
            {
                try { await plc.ReadMultipleVarsAsync(batchItems, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    foreach (var idx in batchIndices)
                        fallback[idx] = true;
                }

                for (var j = 0; j < batchItems.Count; j++)
                {
                    var index = batchIndices[j];
                    if (fallback[index]) continue;
                    results[index] = batchItems[j].Value is not null
                        ? DriverResult.Good(points[index].Address, batchItems[j].Value)
                        : DriverResult.Bad(points[index].Address, QualityCode.BadCommFailure, "Read returned null");
                }
            }

            for (var i = 0; i < fallback.Length; i++)
            {
                if (!fallback[i]) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    var value = await plc.ReadAsync(points[i].Address, ct).ConfigureAwait(false);
                    results[i] = DriverResult.Good(points[i].Address, value);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            var plc = GetPlcOrThrow();
            if (values.Count == 0) return [];

            if (values.Count > 1)
            {
                try
                {
                    var count = values.Count;
                    var addresses = new string[count];
                    var dataItems = new DataItem[count];
                    var idx = 0;
                    foreach (var kv in values)
                    {
                        addresses[idx] = kv.Key.Address;
                        var item = DataItem.FromAddress(kv.Key.Address);
                        item.Value = kv.Value;
                        dataItems[idx++] = item;
                    }

                    await plc.WriteAsync(dataItems).ConfigureAwait(false);

                    var dr = new DriverResult[count];
                    for (var i = 0; i < count; i++)
                        dr[i] = DriverResult.Good(addresses[i], null);
                    return dr;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (_logger is not null) S7Log.LogBatchWriteFallback(_logger, ex); }
            }

            var results = new DriverResult[values.Count];
            var index = 0;
            foreach (var kvp in values)
            {
                try
                {
                    await plc.WriteAsync(kvp.Key.Address, kvp.Value, ct).ConfigureAwait(false);
                    results[index] = DriverResult.Good(kvp.Key.Address, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    results[index] = DriverResult.Bad(kvp.Key.Address, QualityCode.BadCommFailure, ex.Message);
                }
                index++;
            }
            return results;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
        }

        private void SetState(Plc? plc, DriverStatus status)
        {
            lock (_stateLock)
            {
                _plc = plc;
                _status = status;
            }
        }

        private Plc GetPlcOrThrow()
        {
            lock (_stateLock)
            {
                if (_plc is null || _status != DriverStatus.Connected)
                    throw new InvalidOperationException("S7 driver is not connected");
                return _plc;
            }
        }
    }
}
