using Microsoft.Extensions.Logging;
using NewLife.Omron.Protocols;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverDomain.Parser;
using PlcLibrary.General.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Omron
{
    [ProtocolDriverName("Omron_FINS")]
    public sealed class OmronDriver : IProtocolDriver
    {
        private readonly ILogger<OmronDriver> _logger;
        private readonly OmronDriverConfig _config;
        private readonly object _stateLock = new();
        private FinsClient? _finsClient;
        private DriverStatus _status = DriverStatus.Disconnected;

        private static readonly Dictionary<Type, MethodInfo> ReadMethods;
        private static readonly Dictionary<Type, MethodInfo> WriteMethods;

        static OmronDriver()
        {
            var methods = typeof(FinsClient).GetMethods(BindingFlags.Public | BindingFlags.Instance);
            ReadMethods = new()
            {
                [typeof(bool)]   = methods.First(m => m.Name == "ReadBoolAsync"),
                [typeof(short)]  = methods.First(m => m.Name == "ReadInt16Async"),
                [typeof(int)]    = methods.First(m => m.Name == "ReadInt32Async"),
                [typeof(long)]   = methods.First(m => m.Name == "ReadInt64Async"),
                [typeof(ushort)] = methods.First(m => m.Name == "ReadUInt16Async"),
                [typeof(uint)]   = methods.First(m => m.Name == "ReadUInt32Async"),
                [typeof(ulong)]  = methods.First(m => m.Name == "ReadUInt64Async"),
                [typeof(float)]  = methods.First(m => m.Name == "ReadFloatAsync"),
                [typeof(double)] = methods.First(m => m.Name == "ReadDoubleAsync"),
            };
            WriteMethods = new()
            {
                [typeof(bool)]   = methods.First(m => m.Name == "WriteBoolAsync"),
                [typeof(short)]  = methods.First(m => m.Name == "WriteInt16Async"),
                [typeof(int)]    = methods.First(m => m.Name == "WriteInt32Async"),
                [typeof(long)]   = methods.First(m => m.Name == "WriteInt64Async"),
                [typeof(ushort)] = methods.First(m => m.Name == "WriteUInt16Async"),
                [typeof(uint)]   = methods.First(m => m.Name == "WriteUInt32Async"),
                [typeof(ulong)]  = methods.First(m => m.Name == "WriteUInt64Async"),
                [typeof(float)]  = methods.First(m => m.Name == "WriteFloatAsync"),
                [typeof(double)] = methods.First(m => m.Name == "WriteDoubleAsync"),
            };
        }

        public OmronDriver(ILogger<OmronDriver> logger, DeviceConfiguration device)
        {
            _logger = logger;
            _config = OmronDriverConfig.Parse(device.ConnectionString);
        }

        public DriverStatus DriverStatus
        {
            get { lock (_stateLock) return _status; }
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await DisconnectAsync(ct).ConfigureAwait(false);
            SetState(null, DriverStatus.Connecting);

            try
            {
                var client = new FinsClient
                {
                    IpAddress = _config.Host,
                    Port = _config.Port,
                    ConnectTimeOut = _config.Timeout,
                    DataFormat = DataFormat.CDAB,
                };

                await client.ConnectAsync().ConfigureAwait(false);

                SetState(client, DriverStatus.Connected);
                OmronLog.LogConnected(_logger, _config.Host, _config.Port);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SetState(null, DriverStatus.Faulted);
                OmronLog.LogConnectionFailed(_logger, ex, _config.Host, _config.Port);
                throw;
            }
        }

        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            FinsClient? old;
            lock (_stateLock)
            {
                old = _finsClient;
                _finsClient = null;
                _status = DriverStatus.Disconnected;
            }

            if (old is not null)
            {
                try { old.Close(); }
                catch { }
                try { old.Dispose(); }
                catch { }
            }
        }

        public async Task<bool> TryReconnectAsync(CancellationToken ct = default)
        {
            try
            {
                await DisconnectAsync(ct).ConfigureAwait(false);
                await ConnectAsync(ct).ConfigureAwait(false);
                OmronLog.LogReconnected(_logger);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_stateLock) _status = DriverStatus.Faulted;
                OmronLog.LogReconnectFailed(_logger, ex);
                return false;
            }
        }

        public async Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
        {
            var finsClient = GetClientOrThrow();
            if (points.Length == 0) return [];

            var results = new DriverResult[points.Length];

            for (var i = 0; i < points.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var type = DataTypeMapper.Resolve(points[i].DataType);
                    var value = await ReadTypedAsync(finsClient, points[i].Address, type).ConfigureAwait(false);
                    results[i] = DriverResult.Good(points[i].Address, value);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    OmronLog.LogReadPointFailed(_logger, ex, points[i].Address);
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            var finsClient = GetClientOrThrow();
            if (values.Count == 0) return [];

            var entryList = values.ToList();
            var results = new DriverResult[entryList.Count];

            for (var i = 0; i < entryList.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var type = DataTypeMapper.Resolve(entryList[i].Key.DataType);
                    await WriteTypedAsync(finsClient, entryList[i].Key.Address, entryList[i].Value, type).ConfigureAwait(false);
                    results[i] = DriverResult.Good(entryList[i].Key.Address, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    OmronLog.LogWritePointFailed(_logger, ex, entryList[i].Key.Address);
                    results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            OmronLog.LogDisposed(_logger);
        }

        private static async Task<object?> ReadTypedAsync(FinsClient client, string address, Type type)
        {
            if (!ReadMethods.TryGetValue(type, out var method))
                throw new NotSupportedException($"Omron: unsupported read type '{type.Name}'. Use int, float, bool, etc.");

            var task = (Task)method.Invoke(client, [address])!;
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }

        private static async Task WriteTypedAsync(FinsClient client, string address, object value, Type type)
        {
            if (!WriteMethods.TryGetValue(type, out var method))
                throw new NotSupportedException($"Omron: unsupported write type '{type.Name}'. Use int, float, bool, etc.");

            var converted = Convert.ChangeType(value, type);
            var task = (Task)method.Invoke(client, [address, converted])!;
            await task.ConfigureAwait(false);
        }

        private void SetState(FinsClient? client, DriverStatus status)
        {
            lock (_stateLock)
            {
                _finsClient = client;
                _status = status;
            }
        }

        private FinsClient GetClientOrThrow()
        {
            lock (_stateLock)
            {
                if (_finsClient is null || _status != DriverStatus.Connected)
                    throw new InvalidOperationException("Omron FINS driver is not connected");
                return _finsClient;
            }
        }
    }
}
