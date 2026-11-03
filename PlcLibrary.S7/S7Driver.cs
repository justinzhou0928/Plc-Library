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
    public sealed class S7Driver(ILogger<S7Driver> logger, DeviceConfiguration device)
        : ProtocolDriverBase<Plc>
    {
        private readonly S7DriverConfig _config = S7DriverConfig.Parse(device.ConnectionString);

        public override async Task ConnectAsync(CancellationToken ct = default)
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

        public override Task DisconnectAsync(CancellationToken ct = default)
        {
            var old = DetachClient();
            try { old?.Close(); }
            catch { }
            return Task.CompletedTask;
        }

        protected override void OnReconnected() => S7Log.LogReconnected(logger, _config.Host);

        protected override void OnReconnectFailed(Exception ex) => S7Log.LogReconnectFailed(logger, ex, _config.Host);

        public override async Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
        {
            var plc = GetClientOrThrow("S7");
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
                catch (Exception ex)
                {
                    // 地址解析失败是配置错误，不应伪装成通信故障，也不应产生线上请求。
                    S7Log.LogAddressParseFailed(logger, points[i].Address);
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadConfigError, ex.Message);
                }
            }

            if (batchItems.Count > 0)
            {
                var batchSucceeded = true;
                try { await plc.ReadMultipleVarsAsync(batchItems, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    batchSucceeded = false;
                    S7Log.LogBatchReadFallback(logger, ex);
                    MarkFaultedIfTransport(ex);

                    if (TransportFailureDetector.IsTransportFailure(ex))
                    {
                        // 传输已断：逐点回退只会在坏连接上重复等待 timeout。与写路径一致，
                        // 整批标 Bad 并交给池丢弃/重连；解析失败的点位已单独标 BadConfigError。
                        foreach (var idx in batchIndices)
                            results[idx] = DriverResult.Bad(points[idx].Address,
                                QualityCode.BadCommFailure, ex.Message);
                    }
                    else
                    {
                        // 协议/数据级失败：逐点回退以隔离坏点位。
                        foreach (var idx in batchIndices)
                            fallback[idx] = true;
                    }
                }

                if (batchSucceeded)
                {
                    for (var j = 0; j < batchItems.Count; j++)
                    {
                        var index = batchIndices[j];
                        results[index] = batchItems[j].Value is not null
                            ? DriverResult.Good(points[index].Address, batchItems[j].Value)
                            : DriverResult.Bad(points[index].Address, QualityCode.BadCommFailure, "Read returned null");
                    }
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

        public override async Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            var plc = GetClientOrThrow("S7");
            if (values.Count == 0) return [];

            var entries = values.ToList();
            var results = new DriverResult[entries.Count];
            var items = new List<(int Index, string Address, DataItem Item)>(entries.Count);

            // 先逐点构造 DataItem：非法地址只影响自身，且不进入批量写（避免整批失败）
            for (var i = 0; i < entries.Count; i++)
            {
                try
                {
                    var item = CreateDataItem(entries[i].Key);
                    item.Value = entries[i].Value;
                    items.Add((i, entries[i].Key.Address, item));
                }
                catch (Exception ex)
                {
                    results[i] = DriverResult.Bad(entries[i].Key.Address, QualityCode.BadConfigError, ex.Message);
                }
            }

            if (items.Count == 0) return results;

            // 单点且非 string：直接逐点写（可取消），无需承担批量失败回退的双写风险
            if (items.Count == 1 && !IsStringPoint(entries[items[0].Index].Key))
            {
                await WriteOneAsync(plc, items[0].Index, items[0].Address, entries[items[0].Index].Value, results, ct)
                    .ConfigureAwait(false);
                return results;
            }

            try
            {
                await plc.WriteAsync(items.Select(x => x.Item).ToArray()).ConfigureAwait(false);
                foreach (var (index, address, _) in items)
                    results[index] = DriverResult.Good(address, null);
                return results;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                S7Log.LogBatchWriteFallback(logger, ex);
                MarkFaultedIfTransport(ex);

                // 传输级故障：不再逐点重写（连接已不可用，重试只会放大延迟且可能重复写）
                if (TransportFailureDetector.IsTransportFailure(ex))
                {
                    foreach (var (index, address, _) in items)
                        results[index] = DriverResult.Bad(address, QualityCode.BadCommFailure, ex.Message);
                    return results;
                }
            }

            // 协议/数据级失败：逐点回退以隔离坏点位。S7 为绝对值写入，重复写同值幂等。
            foreach (var (index, address, _) in items)
                await WriteOneAsync(plc, index, address, entries[index].Value, results, ct).ConfigureAwait(false);

            return results;
        }

        private async Task WriteOneAsync(
            Plc plc, int index, string address, object value, DriverResult[] results, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (TryParseStringAddress(address, out var db, out var start, out var len))
                {
                    // string 逐点写：S7String.ToByteArray 生成带 2 字节头的字节流，再按字节写入
                    var bytes = S7String.ToByteArray(Convert.ToString(value), len);
                    await plc.WriteBytesAsync(DataType.DataBlock, db, start, bytes, ct).ConfigureAwait(false);
                }
                else
                {
                    await plc.WriteAsync(address, value, ct).ConfigureAwait(false);
                }

                results[index] = DriverResult.Good(address, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                S7Log.LogWritePointFailed(logger, ex, address);
                MarkFaultedIfTransport(ex);
                results[index] = DriverResult.Bad(address, QualityCode.BadCommFailure, ex.Message);
            }
        }

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
        /// 配置了 <c>DataType</c> 的标量点位按配置覆盖地址推断；未配置时沿用 <see cref="DataItem.FromAddress"/>。
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

            if (string.IsNullOrWhiteSpace(point.DataType))
                return DataItem.FromAddress(point.Address);

            if (!DataTypeMapper.TryResolve(point.DataType, out var dataType))
                throw new InvalidAddressException(
                    $"S7 不支持的 DataType '{point.DataType}'（地址 {point.Address}）");

            if (dataType == typeof(string))
                throw new InvalidAddressException(
                    $"S7 string 地址必须带长度，例如 DB1.DBB100.10（当前: {point.Address}）");

            var item = DataItem.FromAddress(point.Address);

            if (item.VarType == VarType.Bit)
            {
                if (dataType != typeof(bool))
                    throw new InvalidAddressException(
                        $"S7 位地址 '{point.Address}' 只支持 bool DataType");
                return item;
            }

            if (dataType == typeof(bool))
                throw new InvalidAddressException(
                    $"S7 bool 必须使用位地址（如 DB1.DBX10.2），当前: {point.Address}");

            var varType = ResolveVarType(dataType);
            if (varType is null)
                throw new InvalidAddressException(
                    $"S7 不支持 DataType '{point.DataType}' 的标量读取（地址 {point.Address}）");

            item.VarType = varType.Value;
            item.Count = 1;
            return item;
        }

        private static VarType? ResolveVarType(Type type) => Type.GetTypeCode(type) switch
        {
            TypeCode.Byte => VarType.Byte,
            TypeCode.Int16 => VarType.Int,
            TypeCode.UInt16 => VarType.Word,
            TypeCode.Int32 => VarType.DInt,
            TypeCode.UInt32 => VarType.DWord,
            TypeCode.Single => VarType.Real,
            TypeCode.Double => VarType.LReal,
            _ => null,
        };
    }
}
