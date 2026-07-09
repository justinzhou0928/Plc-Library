using Microsoft.Extensions.Logging;
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
    public sealed class S7Driver : IProtocolDriver, IDisposable, IAsyncDisposable
    {
        private readonly ILogger<S7Driver>? _logger;
        private readonly S7DriverConfig _config;
        private readonly object _gate = new();
        private Plc? _plc;
        private DriverStatus _status = DriverStatus.Disconnected;

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
            get { lock (_gate) return _status; }
            private set { lock (_gate) _status = value; }
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await DisconnectAsync(ct).ConfigureAwait(false);
            DriverStatus = DriverStatus.Connecting;

            var plc = new Plc(_config.CpuType, _config.Host, _config.Port, _config.Rack, _config.Slot)
            {
                ReadTimeout = _config.Timeout,
                WriteTimeout = _config.Timeout
            };
            await plc.OpenAsync(ct).ConfigureAwait(false);

            lock (_gate) _plc = plc;
            DriverStatus = DriverStatus.Connected;
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            Plc? plc;
            lock (_gate) { plc = _plc; _plc = null; }
            try { plc?.Close(); }
            catch { }
            DriverStatus = DriverStatus.Disconnected;
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
            catch (Exception ex) when (ExceptionIsNotCancellation(ex))
            {
                DriverStatus = DriverStatus.Faulted;
                return false;
            }
        }

        private static bool ExceptionIsNotCancellation(Exception ex) => ex is not OperationCanceledException;
        public async Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
        {
            var plc = GetPlcOrThrow();
            if (points.Length == 0) return [];

            var batchItems = new List<(int Index, DataItem Item)>(points.Length);
            var fallbackIndices = new List<int>();
            var results = new DriverResult[points.Length];

            for (var i = 0; i < points.Length; i++)
            {
                try { batchItems.Add((i, DataItem.FromAddress(points[i].Address))); }
                catch (OperationCanceledException) { throw; }
                catch { fallbackIndices.Add(i); }
            }

            if (batchItems.Count > 0)
            {
                var items = batchItems.Select(b => b.Item).ToList();
                try { await plc.ReadMultipleVarsAsync(items, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    foreach (var (index, _) in batchItems)
                        fallbackIndices.Add(index);
                }

                for (var j = 0; j < batchItems.Count; j++)
                {
                    var (index, _) = batchItems[j];
                    if (fallbackIndices.Contains(index)) continue;
                    results[index] = items[j].Value is not null
                        ? DriverResult.Good(points[index].Address, items[j].Value)
                        : DriverResult.Bad(points[index].Address, QualityCode.BadCommFailure, "读取返回 null");
                }
            }

            foreach (var i in fallbackIndices)
            {
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
                    var pairs = values.ToList();
                    var dataItems = pairs.Select(kv =>
                    {
                        var item = DataItem.FromAddress(kv.Key.Address);
                        item.Value = kv.Value;
                        return item;
                    }).ToArray();

                    await plc.WriteAsync(dataItems).ConfigureAwait(false);
                    return pairs.Select(kv => DriverResult.Good(kv.Key.Address, null)).ToArray();
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

        public void Dispose()
        {
            Plc? plc;
            lock (_gate) { plc = _plc; _plc = null; }
            try { plc?.Close(); }
            catch { }
        }

        public ValueTask DisposeAsync() => new(DisconnectAsync());

        private Plc GetPlcOrThrow()
        {
            Plc? plc;
            lock (_gate) plc = _plc;
            if (plc is null || DriverStatus != DriverStatus.Connected)
                throw new InvalidOperationException("S7 驱动未连接");
            return plc;
        }
    }
}
