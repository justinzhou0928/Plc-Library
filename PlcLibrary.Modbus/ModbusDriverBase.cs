using Microsoft.Extensions.Logging;
using NModbus;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using System;
using System.Collections.Generic;
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
            _master = masterFactory();
        }

        public DriverStatus DriverStatus
        {
            get { lock (_stateLock) return _status; }
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await DisconnectAsync(ct).ConfigureAwait(false);

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
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            IModbusMaster? oldMaster;
            lock (_stateLock)
            {
                oldMaster = _master;
                _master = null;
                _status = DriverStatus.Disconnected;
            }
            try { oldMaster?.Dispose(); } catch { }
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
            ct.ThrowIfCancellationRequested();
            var master = GetMasterOrThrow();
            var results = new DriverResult[points.Length];

            for (var i = 0; i < points.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    results[i] = await ReadSingleAsync(master, points[i], ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ModbusLog.LogReadFailed(_logger, ex, points[i].Address);
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var master = GetMasterOrThrow();
            var results = new DriverResult[values.Count];
            var i = 0;

            foreach (var kvp in values)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await WriteSingleAsync(master, kvp.Key, kvp.Value, ct).ConfigureAwait(false);
                    results[i] = DriverResult.Good(kvp.Key.Address, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ModbusLog.LogWriteFailed(_logger, ex, kvp.Key.Address);
                    results[i] = DriverResult.Bad(kvp.Key.Address, QualityCode.BadCommFailure, ex.Message);
                }
                i++;
            }

            return results;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            ModbusLog.LogDriverDisposed(_logger);
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

        private async Task<DriverResult> ReadSingleAsync(IModbusMaster master, TagPointConfiguration point, CancellationToken ct)
        {
            if (!TryParseAddress(point.Address, out var type, out var offset))
            {
                ModbusLog.LogAddressParseFailed(_logger, point.Address);
                return DriverResult.Bad(point.Address, QualityCode.BadConfigError, $"Invalid Modbus address: {point.Address}");
            }

            var result = type switch
            {
                ModbusType.Coil => await ReadCoilAsync(master, offset, ct).ConfigureAwait(false),
                ModbusType.DiscreteInput => await ReadDiscreteInputAsync(master, offset, ct).ConfigureAwait(false),
                ModbusType.InputRegister => await ReadInputRegisterAsync(master, offset, ct).ConfigureAwait(false),
                ModbusType.HoldingRegister => await ReadHoldingRegisterAsync(master, offset, ct).ConfigureAwait(false),
                _ => DriverResult.Bad(point.Address, QualityCode.BadConfigError, $"Unknown type for: {point.Address}"),
            };

            return result with { Address = point.Address };
        }

        private async Task WriteSingleAsync(IModbusMaster master, TagPointConfiguration point, object value, CancellationToken ct)
        {
            if (!TryParseAddress(point.Address, out var type, out var offset))
                throw new InvalidOperationException($"Invalid Modbus address: {point.Address}");

            switch (type)
            {
                case ModbusType.Coil:
                    await master.WriteSingleCoilAsync(_slaveId, offset, Convert.ToBoolean(value)).ConfigureAwait(false);
                    break;
                case ModbusType.HoldingRegister:
                    await master.WriteSingleRegisterAsync(_slaveId, offset, Convert.ToUInt16(value)).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"Write not supported for {type} at {point.Address}");
            }
        }

        private async Task<DriverResult> ReadCoilAsync(IModbusMaster master, ushort offset, CancellationToken ct)
        {
            var values = await master.ReadCoilsAsync(_slaveId, offset, 1).ConfigureAwait(false);
            return values.Length > 0
                ? DriverResult.Good(offset.ToString(), values[0])
                : DriverResult.Bad(offset.ToString(), QualityCode.BadCommFailure, "Empty response");
        }

        private async Task<DriverResult> ReadDiscreteInputAsync(IModbusMaster master, ushort offset, CancellationToken ct)
        {
            var values = await master.ReadInputsAsync(_slaveId, offset, 1).ConfigureAwait(false);
            return values.Length > 0
                ? DriverResult.Good(offset.ToString(), values[0])
                : DriverResult.Bad(offset.ToString(), QualityCode.BadCommFailure, "Empty response");
        }

        private async Task<DriverResult> ReadInputRegisterAsync(IModbusMaster master, ushort offset, CancellationToken ct)
        {
            var values = await master.ReadInputRegistersAsync(_slaveId, offset, 1).ConfigureAwait(false);
            return values.Length > 0
                ? DriverResult.Good(offset.ToString(), values[0])
                : DriverResult.Bad(offset.ToString(), QualityCode.BadCommFailure, "Empty response");
        }

        private async Task<DriverResult> ReadHoldingRegisterAsync(IModbusMaster master, ushort offset, CancellationToken ct)
        {
            var values = await master.ReadHoldingRegistersAsync(_slaveId, offset, 1).ConfigureAwait(false);
            return values.Length > 0
                ? DriverResult.Good(offset.ToString(), values[0])
                : DriverResult.Bad(offset.ToString(), QualityCode.BadCommFailure, "Empty response");
        }

        internal static bool TryParseAddress(string address, out ModbusType type, out ushort offset)
        {
            type = ModbusType.Coil;
            offset = 0;

            if (string.IsNullOrEmpty(address) || address.Length < 2) return false;

            var prefix = address[0];
            if (!int.TryParse(address.Substring(1), out var num) || num < 1) return false;

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
