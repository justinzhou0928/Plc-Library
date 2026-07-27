using EthernetIPSharp.Logix;
using Microsoft.Extensions.Logging;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.AllenBradley
{
    [ProtocolDriverName("AllenBradley")]
    public sealed class AllenBradleyDriver : IProtocolDriver
    {
        private readonly ILogger<AllenBradleyDriver> _logger;
        private readonly AllenBradleyDriverConfig _config;
        private readonly object _stateLock = new();
        private TagClient? _tagClient;
        private DriverStatus _status = DriverStatus.Disconnected;

        private static readonly MethodInfo ReadAsyncMethod = typeof(TagClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "ReadAsync" && m.IsGenericMethodDefinition);

        public AllenBradleyDriver(ILogger<AllenBradleyDriver> logger, DeviceConfiguration device)
        {
            _logger = logger;
            _config = AllenBradleyDriverConfig.Parse(device.ConnectionString);
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
                var client = string.IsNullOrEmpty(_config.Path)
                    ? new TagClient(_config.Host, useConnected: _config.UseConnected)
                    : new TagClient(_config.Host, path: _config.Path, useConnected: _config.UseConnected);

                await client.ConnectAsync(ct).ConfigureAwait(false);

                SetState(client, DriverStatus.Connected);
                AllenBradleyLog.LogConnected(_logger, _config.Host, _config.Port);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SetState(null, DriverStatus.Faulted);
                AllenBradleyLog.LogConnectionFailed(_logger, ex, _config.Host, _config.Port);
                throw;
            }
        }

        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            TagClient? old;
            lock (_stateLock)
            {
                old = _tagClient;
                _tagClient = null;
                _status = DriverStatus.Disconnected;
            }

            if (old is not null)
            {
                try { await old.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
        }

        public async Task<bool> TryReconnectAsync(CancellationToken ct = default)
        {
            try
            {
                await DisconnectAsync(ct).ConfigureAwait(false);
                await ConnectAsync(ct).ConfigureAwait(false);
                AllenBradleyLog.LogReconnected(_logger);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_stateLock) _status = DriverStatus.Faulted;
                AllenBradleyLog.LogReconnectFailed(_logger, ex);
                return false;
            }
        }

        public async Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
        {
            var tagClient = GetClientOrThrow();
            if (points.Length == 0) return [];

            var results = new DriverResult[points.Length];

            for (var i = 0; i < points.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var value = await ReadTagAsObjectAsync(tagClient, points[i].Address, ct).ConfigureAwait(false);
                    results[i] = DriverResult.Good(points[i].Address, value);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AllenBradleyLog.LogReadPointFailed(_logger, ex, points[i].Address);
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            var tagClient = GetClientOrThrow();
            if (values.Count == 0) return [];

            var entryList = values.ToList();
            var results = new DriverResult[entryList.Count];

            for (var i = 0; i < entryList.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await WriteTagAsObjectAsync(tagClient, entryList[i].Key.Address,
                        entryList[i].Value, ct).ConfigureAwait(false);
                    results[i] = DriverResult.Good(entryList[i].Key.Address, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AllenBradleyLog.LogWritePointFailed(_logger, ex, entryList[i].Key.Address);
                    results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            AllenBradleyLog.LogDisposed(_logger);
        }

        private static async Task<object?> ReadTagAsObjectAsync(TagClient client, string tagName, CancellationToken ct)
        {
            var types = new[] { typeof(int), typeof(float), typeof(bool), typeof(short), typeof(uint) };
            foreach (var t in types)
            {
                try
                {
                    var method = ReadAsyncMethod.MakeGenericMethod(t);
                    var task = (Task)method.Invoke(client, [tagName, ct])!;
                    await task.ConfigureAwait(false);
                    var resultProperty = task.GetType().GetProperty("Result")!;
                    return resultProperty.GetValue(task);
                }
                catch (TargetInvocationException) { }
                catch { }
            }

            throw new InvalidOperationException($"Cannot read tag '{tagName}': type not supported");
        }

        private static async Task WriteTagAsObjectAsync(TagClient client, string tagName, object value,
            CancellationToken ct)
        {
            await client.WriteAsync(tagName, Convert.ToInt32(value), ct).ConfigureAwait(false);
        }

        private void SetState(TagClient? client, DriverStatus status)
        {
            lock (_stateLock)
            {
                _tagClient = client;
                _status = status;
            }
        }

        private TagClient GetClientOrThrow()
        {
            lock (_stateLock)
            {
                if (_tagClient is null || _status != DriverStatus.Connected)
                    throw new InvalidOperationException("AllenBradley driver is not connected");
                return _tagClient;
            }
        }
    }
}
