using Microsoft.Extensions.Logging;
using PlcLibrary.DriverDomain;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverDomain.Parser;
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
    public sealed class S7Driver(ILogger<S7Driver> logger, DeviceConfiguration device) : IProtocolDriver
    {
        private readonly S7DriverConfig _config = S7DriverConfig.Parse(device.ConnectionString);
        private Plc? _plc;
        private DriverStatus _status = DriverStatus.Disconnected;
        private readonly object _stateLock = new();

        public DriverStatus DriverStatus
        {
            get { lock (_stateLock) return _status; }
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await DisconnectAsync(ct).ConfigureAwait(false);
            SetState(null, DriverStatus.Connecting);

            Plc? plc = null;
            try
            {
                plc = new Plc(_config.CpuType, _config.Host, _config.Port, _config.Rack, _config.Slot)
                {
                    ReadTimeout = _config.Timeout,
                    WriteTimeout = _config.Timeout
                };
                await plc.OpenAsync(ct).ConfigureAwait(false);

                SetState(plc, DriverStatus.Connected);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SetState(null, DriverStatus.Faulted);
                if (plc is not null)
                {
                    try { plc.Close(); } catch { }
                }
                S7Log.LogConnectionFailed(logger, ex, _config.Host, _config.Port);
                throw;
            }
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
                S7Log.LogReconnected(logger, _config.Host);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_stateLock) _status = DriverStatus.Faulted;
                S7Log.LogReconnectFailed(logger, ex, _config.Host);
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
                    batchItems.Add(CreateDataItem(points[i]));
                    batchIndices.Add(i);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    S7Log.LogAddressParseFailed(logger, points[i].Address);
                    fallback[i] = true;
                }
            }

            if (batchItems.Count > 0)
            {
                try { await plc.ReadMultipleVarsAsync(batchItems, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    S7Log.LogBatchReadFallback(logger, ex);
                    MarkFaultedIfTransport(ex);
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
                    object? value;
                    if (TryParseStringAddress(points[i].Address, out var db, out var start, out var len))
                        value = await plc.ReadAsync(DataType.DataBlock, db, start, VarType.S7String, len, 0, ct).ConfigureAwait(false);
                    else
                        value = await plc.ReadAsync(points[i].Address, ct).ConfigureAwait(false);
                    results[i] = DriverResult.Good(points[i].Address, value);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    S7Log.LogReadPointFailed(logger, ex, points[i].Address);
                    MarkFaultedIfTransport(ex);
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

            // string 点位必须走批量写（DataItem 支持 S7String 格式），即使只有一个点位
            if (values.Count > 1 || values.Keys.Any(IsStringPoint))
            {
                try
                {
                    return await WriteBatchAsync(plc, values).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    S7Log.LogBatchWriteFallback(logger, ex);
                }
            }

            return await WritePointsIndividuallyAsync(plc, values, ct).ConfigureAwait(false);
        }

        private static async Task<DriverResult[]> WriteBatchAsync(
            Plc plc, IReadOnlyDictionary<TagPointConfiguration, object> values)
        {
            var count = values.Count;
            var addresses = new string[count];
            var dataItems = new DataItem[count];
            var idx = 0;
            foreach (var kv in values)
            {
                addresses[idx] = kv.Key.Address;
                var item = CreateDataItem(kv.Key);
                item.Value = kv.Value;
                dataItems[idx++] = item;
            }

            await plc.WriteAsync(dataItems).ConfigureAwait(false);

            var dr = new DriverResult[count];
            for (var i = 0; i < count; i++)
                dr[i] = DriverResult.Good(addresses[i], null);
            return dr;
        }

        private async Task<DriverResult[]> WritePointsIndividuallyAsync(
            Plc plc, IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct)
        {
            var results = new DriverResult[values.Count];
            var index = 0;
            foreach (var kvp in values)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (TryParseStringAddress(kvp.Key.Address, out var db, out var start, out var len))
                    {
                        // string 逐点写：S7String.ToByteArray 生成带 2 字节头的字节流，再按字节写入
                        var bytes = S7String.ToByteArray(Convert.ToString(kvp.Value), len);
                        await plc.WriteBytesAsync(DataType.DataBlock, db, start, bytes, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await plc.WriteAsync(kvp.Key.Address, kvp.Value, ct).ConfigureAwait(false);
                    }
                    results[index] = DriverResult.Good(kvp.Key.Address, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    S7Log.LogWritePointFailed(logger, ex, kvp.Key.Address);
                    MarkFaultedIfTransport(ex);
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

        /// <summary>通信级故障时置 Faulted，连接池据此丢弃驱动并重建，使断线重连生效。</summary>
        private void MarkFaultedIfTransport(Exception ex)
        {
            if (TransportFailureDetector.IsTransportFailure(ex))
                lock (_stateLock) _status = DriverStatus.Faulted;
        }

        // ─── S7 string 支持 ───

        /// <summary>
        /// 解析 S7 STRING 地址：<c>DB{db}.DBB{startByte}.{length}</c>（如 <c>DB6000.DBB504.100</c>）。
        /// 注意：s7netplus 的 <see cref="DataItem.FromAddress"/> 不解析长度后缀（官方注释确认），
        /// 因此 string 点位必须显式带长度，驱动据此构造 <see cref="VarType.S7String"/> 的 DataItem。
        /// </summary>
        internal static bool TryParseStringAddress(string address, out int db, out int startByte, out int length)
        {
            db = startByte = length = 0;
            if (string.IsNullOrEmpty(address)) return false;

            var parts = address.Split('.');
            if (parts.Length != 3) return false;
            if (!parts[0].StartsWith("DB", StringComparison.OrdinalIgnoreCase)) return false;
            if (!parts[1].StartsWith("DBB", StringComparison.OrdinalIgnoreCase)) return false;

            return int.TryParse(parts[0].AsSpan(2), out db)
                && int.TryParse(parts[1].AsSpan(3), out startByte)
                && int.TryParse(parts[2], out length) && length > 0;
        }

        /// <summary>是否 S7 字符串点位：地址带长度后缀（<c>DBx.DBBx.len</c>）或显式配置了 string 类型。</summary>
        internal static bool IsStringPoint(TagPointConfiguration point)
        {
            if (TryParseStringAddress(point.Address, out _, out _, out _)) return true;
            return DataTypeMapper.Resolve(point.DataType) == typeof(string);
        }

        /// <summary>
        /// 构造 DataItem：S7 字符串点位 → <see cref="VarType.S7String"/>（读 len+2 字节，s7netplus 自动解析 2 字节头）；
        /// 其余点位沿用 <see cref="DataItem.FromAddress"/>（按地址推断类型）。
        /// </summary>
        internal static DataItem CreateDataItem(TagPointConfiguration point)
        {
            if (TryParseStringAddress(point.Address, out var db, out var start, out var len))
            {
                return new DataItem
                {
                    DataType = DataType.DataBlock,
                    DB = db,
                    VarType = VarType.S7String,
                    StartByteAdr = start,
                    Count = len
                };
            }

            if (DataTypeMapper.Resolve(point.DataType) == typeof(string))
                throw new InvalidAddressException(
                    $"S7 string 地址必须带长度，例如 DB1.DBB100.10（当前: {point.Address}）");

            return DataItem.FromAddress(point.Address);
        }
    }
}
