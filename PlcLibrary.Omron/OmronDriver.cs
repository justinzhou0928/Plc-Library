using Microsoft.Extensions.Logging;
using NewLife.Omron.Protocols;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
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
                    var value = await finsClient.ReadInt16Async(points[i].Address).ConfigureAwait(false);
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
                    var bytes = BitConverter.GetBytes(Convert.ToInt16(entryList[i].Value));
                    await finsClient.WriteAsync(entryList[i].Key.Address, bytes).ConfigureAwait(false);
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
