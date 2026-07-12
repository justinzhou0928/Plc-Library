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
    public abstract class ModbusDriverBase(ILogger logger, ModbusDriverConfig config, IModbusMaster master) : IProtocolDriver
    {
        private readonly byte _slaveId = config.SlaveId;
        private readonly object _stateLock = new();
        private DriverStatus _status = DriverStatus.Disconnected;

        public DriverStatus DriverStatus
        {
            get { lock (_stateLock) return _status; }
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await DisconnectAsync(ct).ConfigureAwait(false);
            lock (_stateLock) _status = DriverStatus.Connected;
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            lock (_stateLock) _status = DriverStatus.Disconnected;
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
            var results = new DriverResult[points.Length];

            for (var i = 0; i < points.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    results[i] = await ReadSingleAsync(points[i], ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ModbusLog.LogReadFailed(logger, ex, points[i].Address);
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var results = new DriverResult[values.Count];
            var i = 0;

            foreach (var kvp in values)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await WriteSingleAsync(kvp.Key, kvp.Value, ct).ConfigureAwait(false);
                    results[i] = DriverResult.Good(kvp.Key.Address, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ModbusLog.LogWriteFailed(logger, ex, kvp.Key.Address);
                    results[i] = DriverResult.Bad(kvp.Key.Address, QualityCode.BadCommFailure, ex.Message);
                }
                i++;
            }

            return results;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            master.Dispose();
            ModbusLog.LogDriverDisposed(logger);
        }

        private async Task<DriverResult> ReadSingleAsync(TagPointConfiguration point, CancellationToken ct)
        {
            if (!TryParseAddress(point.Address, out var type, out var offset))
            {
                ModbusLog.LogAddressParseFailed(logger, point.Address);
                return DriverResult.Bad(point.Address, QualityCode.BadConfigError, $"Invalid Modbus address: {point.Address}");
            }

            return type switch
            {
                ModbusType.Coil => await ReadCoilAsync(offset, ct).ConfigureAwait(false),
                ModbusType.DiscreteInput => await ReadDiscreteInputAsync(offset, ct).ConfigureAwait(false),
                ModbusType.InputRegister => await ReadInputRegisterAsync(offset, ct).ConfigureAwait(false),
                ModbusType.HoldingRegister => await ReadHoldingRegisterAsync(offset, ct).ConfigureAwait(false),
                _ => DriverResult.Bad(point.Address, QualityCode.BadConfigError, $"Unknown type for: {point.Address}"),
            };
        }

        private async Task WriteSingleAsync(TagPointConfiguration point, object value, CancellationToken ct)
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

        private async Task<DriverResult> ReadCoilAsync(ushort offset, CancellationToken ct)
        {
            var values = await master.ReadCoilsAsync(_slaveId, offset, 1).ConfigureAwait(false);
            return values.Length > 0
                ? DriverResult.Good(offset.ToString(), values[0])
                : DriverResult.Bad(offset.ToString(), QualityCode.BadCommFailure, "Empty response");
        }

        private async Task<DriverResult> ReadDiscreteInputAsync(ushort offset, CancellationToken ct)
        {
            var values = await master.ReadInputsAsync(_slaveId, offset, 1).ConfigureAwait(false);
            return values.Length > 0
                ? DriverResult.Good(offset.ToString(), values[0])
                : DriverResult.Bad(offset.ToString(), QualityCode.BadCommFailure, "Empty response");
        }

        private async Task<DriverResult> ReadInputRegisterAsync(ushort offset, CancellationToken ct)
        {
            var values = await master.ReadInputRegistersAsync(_slaveId, offset, 1).ConfigureAwait(false);
            return values.Length > 0
                ? DriverResult.Good(offset.ToString(), values[0])
                : DriverResult.Bad(offset.ToString(), QualityCode.BadCommFailure, "Empty response");
        }

        private async Task<DriverResult> ReadHoldingRegisterAsync(ushort offset, CancellationToken ct)
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
