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
            await Task.CompletedTask;
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

        public Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var results = new DriverResult[points.Length];

            for (var i = 0; i < points.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    results[i] = ReadSingle(points[i]);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ModbusLog.LogReadFailed(logger, ex, points[i].Address);
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return Task.FromResult(results);
        }

        public Task<DriverResult[]> WriteAsync(
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
                    WriteSingle(kvp.Key, kvp.Value);
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

            return Task.FromResult(results);
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            master.Dispose();
            ModbusLog.LogDriverDisposed(logger);
        }

        private DriverResult ReadSingle(TagPointConfiguration point)
        {
            if (!TryParseAddress(point.Address, out var type, out var offset))
            {
                ModbusLog.LogAddressParseFailed(logger, point.Address);
                return DriverResult.Bad(point.Address, QualityCode.BadConfigError, $"Invalid Modbus address: {point.Address}");
            }

            return type switch
            {
                ModbusType.Coil => ReadCoil(offset),
                ModbusType.DiscreteInput => ReadDiscreteInput(offset),
                ModbusType.InputRegister => ReadInputRegister(offset),
                ModbusType.HoldingRegister => ReadHoldingRegister(offset),
                _ => DriverResult.Bad(point.Address, QualityCode.BadConfigError, $"Unknown type for: {point.Address}"),
            };
        }

        private void WriteSingle(TagPointConfiguration point, object value)
        {
            if (!TryParseAddress(point.Address, out var type, out var offset))
                throw new InvalidOperationException($"Invalid Modbus address: {point.Address}");

            switch (type)
            {
                case ModbusType.Coil:
                    master.WriteSingleCoilAsync(_slaveId, offset, Convert.ToBoolean(value)).GetAwaiter().GetResult();
                    break;
                case ModbusType.HoldingRegister:
                    master.WriteSingleRegisterAsync(_slaveId, offset, Convert.ToUInt16(value)).GetAwaiter().GetResult();
                    break;
                default:
                    throw new InvalidOperationException($"Write not supported for {type} at {point.Address}");
            }
        }

        private DriverResult ReadCoil(ushort offset)
        {
            var values = master.ReadCoilsAsync(_slaveId, offset, 1).GetAwaiter().GetResult();
            return values.Length > 0
                ? DriverResult.Good(offset.ToString(), values[0])
                : DriverResult.Bad(offset.ToString(), QualityCode.BadCommFailure, "Empty response");
        }

        private DriverResult ReadDiscreteInput(ushort offset)
        {
            var values = master.ReadInputsAsync(_slaveId, offset, 1).GetAwaiter().GetResult();
            return values.Length > 0
                ? DriverResult.Good(offset.ToString(), values[0])
                : DriverResult.Bad(offset.ToString(), QualityCode.BadCommFailure, "Empty response");
        }

        private DriverResult ReadInputRegister(ushort offset)
        {
            var values = master.ReadInputRegistersAsync(_slaveId, offset, 1).GetAwaiter().GetResult();
            return values.Length > 0
                ? DriverResult.Good(offset.ToString(), values[0])
                : DriverResult.Bad(offset.ToString(), QualityCode.BadCommFailure, "Empty response");
        }

        private DriverResult ReadHoldingRegister(ushort offset)
        {
            var values = master.ReadHoldingRegistersAsync(_slaveId, offset, 1).GetAwaiter().GetResult();
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
        public enum ModbusType
        {
            Coil,
            DiscreteInput,
            InputRegister,
            HoldingRegister,
        }
    }
}
