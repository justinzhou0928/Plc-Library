using Microsoft.Extensions.Logging;
using NModbus;
using PlcLibrary.DriverDomain;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Modbus
{
    public abstract class ModbusDriverBase : IProtocolDriver
    {
        private readonly ILogger _logger;
        private readonly byte _slaveId;
        private readonly Func<IModbusMaster> _masterFactory;
        private readonly string _defaultByteOrder;
        private readonly object _stateLock = new();
        private IModbusMaster? _master;
        private DriverStatus _status = DriverStatus.Disconnected;

        protected ModbusDriverBase(ILogger logger, ModbusDriverConfig config, Func<IModbusMaster> masterFactory)
        {
            _logger = logger;
            _slaveId = config.SlaveId;
            _masterFactory = masterFactory;
            _defaultByteOrder = config.ByteOrder;
            if (!ModbusValueCodec.IsValidByteOrderText(_defaultByteOrder))
                throw new ArgumentException($"Invalid Modbus ByteOrder '{_defaultByteOrder}'", nameof(config));
        }

        public DriverStatus DriverStatus
        {
            get { lock (_stateLock) return _status; }
        }

        public Task ConnectAsync(CancellationToken ct = default)
        {
            try
            {
                Disconnect(ct);
                lock (_stateLock) _status = DriverStatus.Connecting;

                var newMaster = _masterFactory();

                IModbusMaster? oldMaster;
                lock (_stateLock)
                {
                    oldMaster = _master;
                    _master = newMaster;
                    _status = DriverStatus.Connected;
                }

                try { oldMaster?.Dispose(); } catch { }
                return Task.CompletedTask;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lock (_stateLock) _status = DriverStatus.Faulted;
                ModbusLog.LogConnectFailed(_logger, ex);
                throw;
            }
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            Disconnect(ct);
            return Task.CompletedTask;
        }

        private void Disconnect(CancellationToken ct)
        {
            IModbusMaster? oldMaster;
            lock (_stateLock)
            {
                oldMaster = _master;
                _master = null;
                _status = DriverStatus.Disconnected;
            }
            try { oldMaster?.Dispose(); } catch { }
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
            ct.ThrowIfCancellationRequested();
            if (points.Length == 0) return [];
            var master = GetMasterOrThrow();
            var results = new DriverResult[points.Length];

            var groups = BuildBatchGroups(points, results);

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await ReadBatchAsync(master, points, group, results).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ModbusLog.LogReadFailed(_logger, ex, group.Start.ToString());
                    MarkFaultedIfTransport(ex);
                    foreach (var point in group.Points)
                        results[point.Index] = DriverResult.Bad(points[point.Index].Address,
                            QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (values.Count == 0) return [];
            var master = GetMasterOrThrow();
            var entryList = values.ToList();
            var results = new DriverResult[entryList.Count];
            var encoded = new ushort[entryList.Count][];
            var plans = new List<PointInfo>();

            for (var i = 0; i < entryList.Count; i++)
            {
                var point = entryList[i].Key;
                if (!TryParseAddress(point.Address, out var type, out var offset))
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Invalid Modbus address: {point.Address}");
                    continue;
                }

                if (type is not ModbusType.Coil and not ModbusType.HoldingRegister)
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Write not supported for {type}");
                    continue;
                }

                if (!TryCreatePointInfo(point, type, offset, i, out var info, out var error))
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        error ?? "Invalid Modbus point configuration");
                    continue;
                }

                var limit = WriteLimit(type);
                if (info.WordCount > limit)
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Point '{point.Address}' span {info.WordCount} exceeds write limit {limit} registers");
                    continue;
                }

                var buffer = new ushort[info.WordCount];
                if (!ModbusValueCodec.TryEncode(entryList[i].Value, info.Kind, info.StringLength,
                        info.ByteOrder, buffer, out error))
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        error ?? "Value cannot be encoded for Modbus");
                    continue;
                }

                encoded[i] = buffer;
                plans.Add(info);
            }

            var groups = BuildWriteGroups(plans, entryList, results);

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await WriteBatchAsync(master, group, encoded).ConfigureAwait(false);
                    foreach (var point in group.Points)
                        results[point.Index] = DriverResult.Good(entryList[point.Index].Key.Address, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ModbusLog.LogWriteFailed(_logger, ex, group.Start.ToString());
                    MarkFaultedIfTransport(ex);
                    foreach (var point in group.Points)
                        results[point.Index] = DriverResult.Bad(entryList[point.Index].Key.Address,
                            QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            ModbusLog.LogDriverDisposed(_logger);
        }

        private const int MaxReadBitsPerRequest = 2000;
        private const int MaxReadRegistersPerRequest = 125;
        private const int MaxWriteCoilsPerRequest = 1968;
        private const int MaxWriteRegistersPerRequest = 123;

        private static int ReadLimit(ModbusType type)
            => type is ModbusType.Coil or ModbusType.DiscreteInput
                ? MaxReadBitsPerRequest : MaxReadRegistersPerRequest;

        private static int WriteLimit(ModbusType type)
            => type == ModbusType.Coil ? MaxWriteCoilsPerRequest : MaxWriteRegistersPerRequest;

        private readonly record struct PointInfo(
            int Index,
            ModbusType Type,
            ushort Offset,
            int WordCount,
            ModbusValueKind Kind,
            int StringLength,
            ByteOrderMap ByteOrder);

        private sealed class BatchGroup
        {
            public ModbusType Type;
            public ushort Start;
            public int End;
            public int Count => End - Start;
            public List<PointInfo> Points = [];
        }

        private List<BatchGroup> BuildBatchGroups(TagPointConfiguration[] points, DriverResult[] results)
        {
            var byType = new Dictionary<ModbusType, List<PointInfo>>();

            for (var i = 0; i < points.Length; i++)
            {
                if (!TryParseAddress(points[i].Address, out var type, out var offset))
                {
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadConfigError,
                        $"Invalid Modbus address: {points[i].Address}");
                    continue;
                }

                if (!TryCreatePointInfo(points[i], type, offset, i, out var info, out var error))
                {
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadConfigError,
                        error ?? "Invalid Modbus point configuration");
                    continue;
                }

                var limit = ReadLimit(type);
                if (info.WordCount > limit)
                {
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadConfigError,
                        $"Point '{points[i].Address}' span {info.WordCount} exceeds read limit {limit} registers");
                    continue;
                }

                if (!byType.TryGetValue(type, out var list))
                    byType[type] = list = [];

                list.Add(info);
            }

            var groups = new List<BatchGroup>();
            foreach (var kv in byType)
                groups.AddRange(MergeGroups(kv.Key, kv.Value, ReadLimit(kv.Key)));

            return groups;
        }

        private bool TryCreatePointInfo(TagPointConfiguration point, ModbusType type, ushort offset, int index,
            out PointInfo info, out string? error)
        {
            info = default;
            error = null;

            if (type is ModbusType.Coil or ModbusType.DiscreteInput)
            {
                if (point.Length != 0)
                {
                    error = $"Length is only supported for register string points (address {point.Address})";
                    return false;
                }

                ModbusValueKind kind;
                if (string.IsNullOrWhiteSpace(point.DataType))
                {
                    kind = ModbusValueKind.Boolean;
                }
                else if (!ModbusValueCodec.TryGetKind(point.DataType, out kind, out error))
                {
                    return false;
                }

                if (kind != ModbusValueKind.Boolean)
                {
                    error = $"Bit address '{point.Address}' supports only bool DataType";
                    return false;
                }

                info = new PointInfo(index, type, offset, 1, kind, 0, default);
                return true;
            }

            if (!ModbusValueCodec.TryGetKind(point.DataType, out var valueKind, out error))
                return false;

            int wordCount;
            var stringLength = 0;
            ByteOrderMap byteOrder;

            if (valueKind == ModbusValueKind.String)
            {
                if (point.Length <= 0)
                {
                    error = $"DataType 'string' requires Length > 0 (address {point.Address})";
                    return false;
                }

                if (!ModbusValueCodec.TryGetWordCount(valueKind, point.Length, out wordCount, out error))
                    return false;

                if (!TryResolveByteOrder(point.ByteOrder, 2, out byteOrder, out error))
                    return false;

                stringLength = point.Length;
            }
            else
            {
                if (point.Length != 0)
                {
                    error = $"Length is only supported for DataType 'string' (address {point.Address})";
                    return false;
                }

                if (!ModbusValueCodec.TryGetWordCount(valueKind, 0, out wordCount, out error))
                    return false;

                if (!TryResolveByteOrder(point.ByteOrder, ModbusValueCodec.GetByteWidth(valueKind),
                        out byteOrder, out error))
                    return false;
            }

            if (offset + wordCount > 65536)
            {
                error = $"Point '{point.Address}' spans beyond the Modbus address space (65536)";
                return false;
            }

            info = new PointInfo(index, type, offset, wordCount, valueKind, stringLength, byteOrder);
            return true;
        }

        private bool TryResolveByteOrder(string? pointOrder, int width, out ByteOrderMap byteOrder,
            out string? error)
        {
            error = null;
            if (!string.IsNullOrWhiteSpace(pointOrder))
                return ModbusValueCodec.TryParseByteOrder(pointOrder, width, out byteOrder, out error);

            if (!string.IsNullOrWhiteSpace(_defaultByteOrder)
                && ModbusValueCodec.TryParseByteOrder(_defaultByteOrder, width, out byteOrder, out _))
            {
                return true;
            }

            byteOrder = ByteOrderMap.Identity(width);
            return true;
        }

        private List<BatchGroup> BuildWriteGroups(List<PointInfo> plans,
            List<KeyValuePair<TagPointConfiguration, object>> entryList, DriverResult[] results)
        {
            var byType = new Dictionary<ModbusType, List<PointInfo>>();
            foreach (var plan in plans)
            {
                if (!byType.TryGetValue(plan.Type, out var list))
                    byType[plan.Type] = list = [];
                list.Add(plan);
            }

            var groups = new List<BatchGroup>();
            foreach (var kv in byType)
            {
                kv.Value.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                var valid = RemoveOverlappingWrites(kv.Value, entryList, results);
                groups.AddRange(MergeGroups(kv.Key, valid, WriteLimit(kv.Key)));
            }

            return groups;
        }

        private static List<PointInfo> RemoveOverlappingWrites(List<PointInfo> sorted,
            List<KeyValuePair<TagPointConfiguration, object>> entryList, DriverResult[] results)
        {
            // 同一寄存器被两个写点位覆盖时值语义不明，整簇拒绝而不是静默覆盖。
            var valid = new List<PointInfo>(sorted.Count);
            var i = 0;

            while (i < sorted.Count)
            {
                var j = i + 1;
                var maxEnd = sorted[i].Offset + sorted[i].WordCount;
                while (j < sorted.Count && sorted[j].Offset < maxEnd)
                {
                    var end = sorted[j].Offset + sorted[j].WordCount;
                    if (end > maxEnd)
                        maxEnd = end;
                    j++;
                }

                if (j - i == 1)
                {
                    valid.Add(sorted[i]);
                }
                else
                {
                    for (var k = i; k < j; k++)
                    {
                        var address = entryList[sorted[k].Index].Key.Address;
                        results[sorted[k].Index] = DriverResult.Bad(address, QualityCode.BadConfigError,
                            $"Overlapping write points are not supported (address {address})");
                    }
                }

                i = j;
            }

            return valid;
        }

        private static List<BatchGroup> MergeGroups(ModbusType type, List<PointInfo> points, int limit)
        {
            // 按区间合并：相邻/重叠点位共用一次读取；超过 PDU 上限时断开。
            points.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            var groups = new List<BatchGroup>();
            BatchGroup? current = null;

            foreach (var point in points)
            {
                var end = point.Offset + point.WordCount;
                if (current is null || point.Offset > current.End || end - current.Start > limit)
                {
                    current = new BatchGroup
                    {
                        Type = type,
                        Start = point.Offset,
                        End = end,
                        Points = [point],
                    };
                    groups.Add(current);
                }
                else
                {
                    if (end > current.End)
                        current.End = end;
                    current.Points.Add(point);
                }
            }

            return groups;
        }

        private async Task ReadBatchAsync(IModbusMaster master, TagPointConfiguration[] points,
            BatchGroup group, DriverResult[] results)
        {
            switch (group.Type)
            {
                case ModbusType.Coil:
                {
                    var coils = await master.ReadCoilsAsync(_slaveId, group.Start, (ushort)group.Count)
                        .ConfigureAwait(false);
                    AssignBitResults(points, group, coils, results);
                    break;
                }

                case ModbusType.DiscreteInput:
                {
                    var inputs = await master.ReadInputsAsync(_slaveId, group.Start, (ushort)group.Count)
                        .ConfigureAwait(false);
                    AssignBitResults(points, group, inputs, results);
                    break;
                }

                case ModbusType.InputRegister:
                {
                    var registers = await master.ReadInputRegistersAsync(_slaveId, group.Start, (ushort)group.Count)
                        .ConfigureAwait(false);
                    AssignRegisterResults(points, group, registers, results);
                    break;
                }

                case ModbusType.HoldingRegister:
                {
                    var registers = await master.ReadHoldingRegistersAsync(_slaveId, group.Start, (ushort)group.Count)
                        .ConfigureAwait(false);
                    AssignRegisterResults(points, group, registers, results);
                    break;
                }
            }
        }

        private static void AssignBitResults(TagPointConfiguration[] points, BatchGroup group,
            bool[] values, DriverResult[] results)
        {
            if (values.Length < group.Count)
                throw new InvalidOperationException(
                    $"Modbus returned {values.Length} bits, expected {group.Count}");

            foreach (var point in group.Points)
            {
                results[point.Index] = DriverResult.Good(points[point.Index].Address,
                    values[point.Offset - group.Start]);
            }
        }

        private static void AssignRegisterResults(TagPointConfiguration[] points, BatchGroup group,
            ushort[] registers, DriverResult[] results)
        {
            if (registers.Length < group.Count)
                throw new InvalidOperationException(
                    $"Modbus returned {registers.Length} registers, expected {group.Count}");

            foreach (var point in group.Points)
            {
                var value = ModbusValueCodec.Decode(
                    registers.AsSpan(point.Offset - group.Start, point.WordCount),
                    point.Kind, point.StringLength, point.ByteOrder);
                results[point.Index] = DriverResult.Good(points[point.Index].Address, value);
            }
        }

        private async Task WriteBatchAsync(IModbusMaster master, BatchGroup group, ushort[][] encoded)
        {
            if (group.Type == ModbusType.Coil)
            {
                var values = new bool[group.Count];
                foreach (var point in group.Points)
                    values[point.Offset - group.Start] = encoded[point.Index][0] != 0;

                await master.WriteMultipleCoilsAsync(_slaveId, group.Start, values).ConfigureAwait(false);
            }
            else
            {
                var values = new ushort[group.Count];
                foreach (var point in group.Points)
                    encoded[point.Index].CopyTo(values, point.Offset - group.Start);

                await master.WriteMultipleRegistersAsync(_slaveId, group.Start, values).ConfigureAwait(false);
            }
        }

        private IModbusMaster GetMasterOrThrow()
        {
            lock (_stateLock)
            {
                if (_master is null || _status != DriverStatus.Connected)
                    throw new InvalidOperationException("Modbus driver is not connected");
                return _master;
            }
        }

        /// <summary>通信级故障时置 Faulted，连接池据此丢弃驱动并重建，使断线重连生效。</summary>
        private void MarkFaultedIfTransport(Exception ex)
        {
            if (TransportFailureDetector.IsTransportFailure(ex))
                lock (_stateLock) _status = DriverStatus.Faulted;
        }

        internal static bool TryParseAddress(string address, out ModbusType type, out ushort offset)
        {
            type = ModbusType.Coil;
            offset = 0;

            if (string.IsNullOrEmpty(address) || address.Length < 2) return false;

            var prefix = address[0];
            if (!int.TryParse(address.Substring(1), out var num) || num < 1 || num > 65536) return false;

            switch (prefix)
            {
                case '0':
                    type = ModbusType.Coil;
                    offset = (ushort)(num - 1);
                    return true;
                case '1':
                    type = ModbusType.DiscreteInput;
                    offset = (ushort)(num - 1);
                    return true;
                case '3':
                    type = ModbusType.InputRegister;
                    offset = (ushort)(num - 1);
                    return true;
                case '4':
                    type = ModbusType.HoldingRegister;
                    offset = (ushort)(num - 1);
                    return true;
                default:
                    return false;
            }
        }
    }
}
