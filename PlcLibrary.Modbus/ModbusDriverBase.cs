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
        private readonly object _stateLock = new();
        private IModbusMaster? _master;
        private DriverStatus _status = DriverStatus.Disconnected;

        protected ModbusDriverBase(ILogger logger, ModbusDriverConfig config, Func<IModbusMaster> masterFactory)
        {
            _logger = logger;
            _slaveId = config.SlaveId;
            _masterFactory = masterFactory;
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
                    await ReadBatchAsync(master, points, group, results, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ModbusLog.LogReadFailed(_logger, ex, group.Start.ToString());
                    MarkFaultedIfTransport(ex);
                    foreach (var idx in group.Indices)
                        results[idx] = DriverResult.Bad(points[idx].Address, QualityCode.BadCommFailure, ex.Message);
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

            var groups = BuildWriteGroups(entryList, results);

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await WriteBatchAsync(master, group, entryList, results, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ModbusLog.LogWriteFailed(_logger, ex, group.Start.ToString());
                    MarkFaultedIfTransport(ex);
                    foreach (var idx in group.Indices)
                        results[idx] = DriverResult.Bad(entryList[idx].Key.Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            ModbusLog.LogDriverDisposed(_logger);
        }

        // ─── batch infrastructure ───

        private const int MaxReadBitsPerRequest = 2000;      // NModbus: 一次读线圈/离散输入上限
        private const int MaxReadRegistersPerRequest = 125;  // NModbus: 一次读寄存器上限
        private const int MaxWriteCoilsPerRequest = 1968;    // Modbus 写多线圈 PDU 上限 (0x7B0)
        private const int MaxWriteRegistersPerRequest = 123; // Modbus 写多寄存器 PDU 上限 (0x7B)

        private static int ReadLimit(ModbusType type)
            => type is ModbusType.Coil or ModbusType.DiscreteInput
                ? MaxReadBitsPerRequest : MaxReadRegistersPerRequest;

        private static int WriteLimit(ModbusType type)
            => type == ModbusType.Coil ? MaxWriteCoilsPerRequest : MaxWriteRegistersPerRequest;

        private readonly record struct PointInfo(int Index, ushort Offset);

        private sealed class BatchGroup
        {
            public ModbusType Type;
            public ushort Start;
            public ushort Count;
            public List<int> Indices = [];
        }

        private List<BatchGroup> BuildBatchGroups(TagPointConfiguration[] points, DriverResult[] results)
        {
            var byType = new Dictionary<ModbusType, List<PointInfo>>();

            for (var i = 0; i < points.Length; i++)
            {
                if (!TryParseAddress(points[i].Address, out var type, out var offset))
                {
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadConfigError, $"Invalid Modbus address: {points[i].Address}");
                    continue;
                }

                if (!byType.TryGetValue(type, out var list))
                    byType[type] = list = [];

                list.Add(new PointInfo(i, offset));
            }

            var groups = new List<BatchGroup>();
            foreach (var kv in byType)
            {
                kv.Value.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                var limit = ReadLimit(kv.Key);
                BatchGroup? current = null;

                foreach (var pi in kv.Value)
                {
                    if (current is null || pi.Offset != current.Start + current.Count || current.Count >= limit)
                    {
                        current = new BatchGroup
                        {
                            Type = kv.Key,
                            Start = pi.Offset,
                            Count = 1,
                            Indices = [pi.Index]
                        };
                        groups.Add(current);
                    }
                    else
                    {
                        current.Count++;
                        current.Indices.Add(pi.Index);
                    }
                }
            }

            return groups;
        }

        private async Task ReadBatchAsync(IModbusMaster master, TagPointConfiguration[] points,
            BatchGroup group, DriverResult[] results, CancellationToken ct)
        {
            switch (group.Type)
            {
                case ModbusType.Coil:
                    var coils = await master.ReadCoilsAsync(_slaveId, group.Start, group.Count).ConfigureAwait(false);
                    for (var i = 0; i < group.Indices.Count; i++)
                        results[group.Indices[i]] = DriverResult.Good(points[group.Indices[i]].Address, coils[i]);
                    break;

                case ModbusType.DiscreteInput:
                    var inputs = await master.ReadInputsAsync(_slaveId, group.Start, group.Count).ConfigureAwait(false);
                    for (var i = 0; i < group.Indices.Count; i++)
                        results[group.Indices[i]] = DriverResult.Good(points[group.Indices[i]].Address, inputs[i]);
                    break;

                case ModbusType.InputRegister:
                    var iRegs = await master.ReadInputRegistersAsync(_slaveId, group.Start, group.Count).ConfigureAwait(false);
                    for (var i = 0; i < group.Indices.Count; i++)
                        results[group.Indices[i]] = DriverResult.Good(points[group.Indices[i]].Address, iRegs[i]);
                    break;

                case ModbusType.HoldingRegister:
                    var hRegs = await master.ReadHoldingRegistersAsync(_slaveId, group.Start, group.Count).ConfigureAwait(false);
                    for (var i = 0; i < group.Indices.Count; i++)
                        results[group.Indices[i]] = DriverResult.Good(points[group.Indices[i]].Address, hRegs[i]);
                    break;
            }
        }

        private List<BatchGroup> BuildWriteGroups(
            List<KeyValuePair<TagPointConfiguration, object>> entryList, DriverResult[] results)
        {
            var byType = new Dictionary<ModbusType, List<PointInfo>>();

            for (var i = 0; i < entryList.Count; i++)
            {
                if (!TryParseAddress(entryList[i].Key.Address, out var type, out var offset))
                {
                    results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadConfigError,
                        $"Invalid Modbus address: {entryList[i].Key.Address}");
                    continue;
                }

                if (type is not ModbusType.Coil and not ModbusType.HoldingRegister)
                {
                    results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadConfigError,
                        $"Write not supported for {type}");
                    continue;
                }

                if (!byType.TryGetValue(type, out var list))
                    byType[type] = list = [];

                list.Add(new PointInfo(i, offset));
            }

            var groups = new List<BatchGroup>();
            foreach (var kv in byType)
            {
                kv.Value.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                var limit = WriteLimit(kv.Key);
                BatchGroup? current = null;

                foreach (var pi in kv.Value)
                {
                    if (current is null || pi.Offset != current.Start + current.Count || current.Count >= limit)
                    {
                        current = new BatchGroup
                        {
                            Type = kv.Key,
                            Start = pi.Offset,
                            Count = 1,
                            Indices = [pi.Index]
                        };
                        groups.Add(current);
                    }
                    else
                    {
                        current.Count++;
                        current.Indices.Add(pi.Index);
                    }
                }
            }

            return groups;
        }

        private async Task WriteBatchAsync(IModbusMaster master, BatchGroup group,
            List<KeyValuePair<TagPointConfiguration, object>> entryList, DriverResult[] results, CancellationToken ct)
        {
            if (group.Type == ModbusType.Coil)
            {
                var values = new bool[group.Count];
                for (var i = 0; i < group.Indices.Count; i++)
                    values[i] = Convert.ToBoolean(entryList[group.Indices[i]].Value);

                await master.WriteMultipleCoilsAsync(_slaveId, group.Start, values).ConfigureAwait(false);
            }
            else
            {
                var values = new ushort[group.Count];
                for (var i = 0; i < group.Indices.Count; i++)
                {
                    // unchecked：负数按二进制补码写入（-1 -> 0xFFFF），避免 Convert.ToUInt16 抛 OverflowException
                    values[i] = unchecked((ushort)Convert.ToInt64(entryList[group.Indices[i]].Value));
                }

                await master.WriteMultipleRegistersAsync(_slaveId, group.Start, values).ConfigureAwait(false);
            }

            foreach (var idx in group.Indices)
                results[idx] = DriverResult.Good(entryList[idx].Key.Address, null);
        }

        // ─── helpers ───

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
