using EthernetIPSharp.Logix;
using Microsoft.Extensions.Logging;
using PlcLibrary.DriverDomain;
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

        private static readonly MethodInfo? ReadAsyncMethod = typeof(TagClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "ReadAsync" && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 2);

        private static readonly MethodInfo? WriteAsyncMethod = typeof(TagClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "WriteAsync" && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 3);

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

            TagClient? client = null;
            try
            {
                client = new TagClient(_config.Host, _config.Port, _config.Path, _config.UseConnected);

                await client.ConnectAsync(ct).ConfigureAwait(false);

                SetState(client, DriverStatus.Connected);
                AllenBradleyLog.LogConnected(_logger, _config.Host, _config.Port);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SetState(null, DriverStatus.Faulted);
                if (client is not null)
                {
                    try { await client.DisposeAsync().ConfigureAwait(false); }
                    catch { }
                }
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
            var handled = new bool[points.Length];

            // 批量：非 string 点位一次 ReadMultipleAsync（返回原始字节，CIP 小端本地解码）
            var batchIndices = new List<int>();
            var batchTags = new List<string>();
            var batchTypes = new List<Type>();
            for (var i = 0; i < points.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var type = DataTypeMapper.Resolve(points[i].DataType);
                if (type == typeof(string)) continue; // string 标签走逐点 ReadStringAsync
                batchIndices.Add(i);
                batchTags.Add(points[i].Address);
                batchTypes.Add(type);
            }

            if (batchIndices.Count > 0)
            {
                try
                {
                    var raw = await tagClient.ReadMultipleAsync(batchTags, ct).ConfigureAwait(false);
                    for (var j = 0; j < batchIndices.Count; j++)
                    {
                        var idx = batchIndices[j];
                        if (raw.TryGetValue(batchTags[j], out var bytes) && TryDecodeCipValue(batchTypes[j], bytes, out var value))
                            results[idx] = DriverResult.Good(points[idx].Address, value);
                        else
                            results[idx] = DriverResult.Bad(points[idx].Address, QualityCode.BadCommFailure,
                                "Batch read returned no/invalid data for tag");
                        handled[idx] = true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 批量失败：传输级故障置 Faulted；逐点回退保留隔离语义
                    MarkFaultedIfTransport(ex);
                    AllenBradleyLog.LogReadPointFailed(_logger, ex, "batch");
                    foreach (var idx in batchIndices)
                    {
                        handled[idx] = true;
                        try
                        {
                            var type = DataTypeMapper.Resolve(points[idx].DataType);
                            var value = await ReadTypedAsync(tagClient, points[idx].Address, type, ct).ConfigureAwait(false);
                            results[idx] = DriverResult.Good(points[idx].Address, value);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex2)
                        {
                            AllenBradleyLog.LogReadPointFailed(_logger, ex2, points[idx].Address);
                            results[idx] = DriverResult.Bad(points[idx].Address, QualityCode.BadCommFailure, ex2.Message);
                        }
                    }
                }
            }

            // 逐点：string 类型
            for (var i = 0; i < points.Length; i++)
            {
                if (handled[i]) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    var type = DataTypeMapper.Resolve(points[i].DataType);
                    var value = await ReadTypedAsync(tagClient, points[i].Address, type, ct).ConfigureAwait(false);
                    results[i] = DriverResult.Good(points[i].Address, value);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AllenBradleyLog.LogReadPointFailed(_logger, ex, points[i].Address);
                    MarkFaultedIfTransport(ex);
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        /// <summary>按 CIP 小端序解码原始字节为对应 CLR 类型（BOOL/SINT/INT/DINT/LINT/REAL/LREAL）。</summary>
        private static bool TryDecodeCipValue(Type type, byte[] data, out object? value)
        {
            value = null;
            if (data.Length == 0) return false;

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean: value = data[0] != 0; return true;
                case TypeCode.SByte: value = unchecked((sbyte)data[0]); return true;
                case TypeCode.Byte: value = data[0]; return true;
                case TypeCode.Int16: if (data.Length < 2) return false; value = BitConverter.ToInt16(data, 0); return true;
                case TypeCode.UInt16: if (data.Length < 2) return false; value = BitConverter.ToUInt16(data, 0); return true;
                case TypeCode.Int32: if (data.Length < 4) return false; value = BitConverter.ToInt32(data, 0); return true;
                case TypeCode.UInt32: if (data.Length < 4) return false; value = BitConverter.ToUInt32(data, 0); return true;
                case TypeCode.Int64: if (data.Length < 8) return false; value = BitConverter.ToInt64(data, 0); return true;
                case TypeCode.UInt64: if (data.Length < 8) return false; value = BitConverter.ToUInt64(data, 0); return true;
                case TypeCode.Single: if (data.Length < 4) return false; value = BitConverter.ToSingle(data, 0); return true;
                case TypeCode.Double: if (data.Length < 8) return false; value = BitConverter.ToDouble(data, 0); return true;
                default: return false;
            }
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
                    var type = DataTypeMapper.Resolve(entryList[i].Key.DataType);
                    await WriteTypedAsync(tagClient, entryList[i].Key.Address,
                        entryList[i].Value, type, ct).ConfigureAwait(false);
                    results[i] = DriverResult.Good(entryList[i].Key.Address, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AllenBradleyLog.LogWritePointFailed(_logger, ex, entryList[i].Key.Address);
                    MarkFaultedIfTransport(ex);
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

        private static async Task<object?> ReadTypedAsync(TagClient client, string tagName, Type type, CancellationToken ct)
        {
            if (type == typeof(string))
                return await client.ReadStringAsync(tagName, ct).ConfigureAwait(false);

            if (ReadAsyncMethod is null)
                throw new NotSupportedException("AllenBradley: ReadAsync<T>(string, CancellationToken) not found in TagClient");

            var method = ReadAsyncMethod.MakeGenericMethod(type);
            var task = (Task)method.Invoke(client, [tagName, ct])!;
            await task.ConfigureAwait(false);
            var resultProperty = task.GetType().GetProperty("Result")!;
            return resultProperty.GetValue(task);
        }

        private static async Task WriteTypedAsync(TagClient client, string tagName, object value, Type type, CancellationToken ct)
        {
            if (WriteAsyncMethod is null)
                throw new NotSupportedException("AllenBradley: WriteAsync<T>(string, T, CancellationToken) not found in TagClient");

            var converted = Convert.ChangeType(value, type);
            var method = WriteAsyncMethod.MakeGenericMethod(type);
            var task = (Task)method.Invoke(client, [tagName, converted, ct])!;
            await task.ConfigureAwait(false);
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

        /// <summary>通信级故障时置 Faulted，连接池据此丢弃驱动并重建，使断线重连生效。</summary>
        private void MarkFaultedIfTransport(Exception ex)
        {
            if (TransportFailureDetector.IsTransportFailure(ex))
                lock (_stateLock) _status = DriverStatus.Faulted;
        }
    }
}
