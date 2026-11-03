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
using System.Buffers.Binary;
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

                await WithTimeoutAsync(token => client.ConnectAsync(token), ct).ConfigureAwait(false);

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
            var types = new Type?[points.Length];

            for (var i = 0; i < points.Length; i++)
            {
                if (!DataTypeMapper.TryResolve(points[i].DataType, out var resolved))
                {
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadConfigError,
                        $"AllenBradley: unsupported DataType '{points[i].DataType}'");
                    handled[i] = true;
                    continue;
                }
                types[i] = resolved;
            }

            // 批量：非 string 点位一次 ReadMultipleAsync（返回原始字节，CIP 小端本地解码）
            var batchIndices = new List<int>();
            var batchTags = new List<string>();
            var batchTypes = new List<Type>();
            for (var i = 0; i < points.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (handled[i] || types[i] == typeof(string)) continue; // string 标签走逐点 ReadStringAsync
                batchIndices.Add(i);
                batchTags.Add(points[i].Address);
                batchTypes.Add(types[i]!);
            }

            if (batchIndices.Count > 0)
            {
                try
                {
                    var raw = await WithTimeoutAsync(
                        token => tagClient.ReadMultipleAsync(batchTags, token), ct).ConfigureAwait(false);
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
                    for (var j = 0; j < batchIndices.Count; j++)
                    {
                        var idx = batchIndices[j];
                        var type = batchTypes[j];
                        handled[idx] = true;
                        try
                        {
                            var value = await WithTimeoutAsync(
                                token => ReadTypedAsync(tagClient, points[idx].Address, type, token), ct).ConfigureAwait(false);
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
                    var type = types[i]!;
                    var value = await WithTimeoutAsync(
                        token => ReadTypedAsync(tagClient, points[i].Address, type, token), ct).ConfigureAwait(false);
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

        /// <summary>
        /// 给单次操作套上配置的 <c>timeout</c>：超时转换为 <see cref="TimeoutException"/>（传输级故障），
        /// 由连接池丢弃驱动；调用方取消仍抛 <see cref="OperationCanceledException"/>。
        /// </summary>
        private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            if (_config.Timeout <= 0) return await action(ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_config.Timeout);
            try
            {
                return await action(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"AllenBradley operation timed out after {_config.Timeout}ms");
            }
        }

        private async Task WithTimeoutAsync(Func<CancellationToken, Task> action, CancellationToken ct)
            => await WithTimeoutAsync<bool>(async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);

        /// <summary>
        /// 解码 <c>TagClient.ReadMultipleAsync</c> 返回的原始值。
        /// 该 API 对每个点位返回「2 字节 CIP 数据类型码 + 原始数据」，必须跳过并校验类型码，
        /// 否则类型码会被当作数据：DINT/REAL 整体错位，BOOL 恒为 true（0xC1 != 0），且质量仍为 Good。
        /// </summary>
        /// <summary>CLR 类型 → CIP 基本数据类型码（ODVA）；0 表示不支持。</summary>
        private static ushort ExpectedCipTypeCode(Type type) => Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => 0x00C1,
            TypeCode.SByte => 0x00C2,
            TypeCode.Int16 => 0x00C3,
            TypeCode.Int32 => 0x00C4,
            TypeCode.Int64 => 0x00C5,
            TypeCode.Byte => 0x00C6,
            TypeCode.UInt16 => 0x00C7,
            TypeCode.UInt32 => 0x00C8,
            TypeCode.UInt64 => 0x00C9,
            TypeCode.Single => 0x00CA,
            TypeCode.Double => 0x00CB,
            _ => 0,
        };

        private static bool TryDecodeCipValue(Type type, byte[] data, out object? value)
        {
            value = null;
            const int typeCodeLength = 2;
            if (data.Length < typeCodeLength) return false;

            var expectedTypeCode = ExpectedCipTypeCode(type);
            if (expectedTypeCode == 0) return false;

            var actualTypeCode = (ushort)(data[0] | (data[1] << 8));
            if (actualTypeCode != expectedTypeCode) return false;

            var payload = data.AsSpan(typeCodeLength);
            if (payload.Length == 0) return false;
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean: value = payload[0] != 0; return true;
                case TypeCode.SByte: value = unchecked((sbyte)payload[0]); return true;
                case TypeCode.Byte: value = payload[0]; return true;
                case TypeCode.Int16: if (payload.Length < 2) return false; value = BinaryPrimitives.ReadInt16LittleEndian(payload); return true;
                case TypeCode.UInt16: if (payload.Length < 2) return false; value = BinaryPrimitives.ReadUInt16LittleEndian(payload); return true;
                case TypeCode.Int32: if (payload.Length < 4) return false; value = BinaryPrimitives.ReadInt32LittleEndian(payload); return true;
                case TypeCode.UInt32: if (payload.Length < 4) return false; value = BinaryPrimitives.ReadUInt32LittleEndian(payload); return true;
                case TypeCode.Int64: if (payload.Length < 8) return false; value = BinaryPrimitives.ReadInt64LittleEndian(payload); return true;
                case TypeCode.UInt64: if (payload.Length < 8) return false; value = BinaryPrimitives.ReadUInt64LittleEndian(payload); return true;
                case TypeCode.Single: if (payload.Length < 4) return false; value = BinaryPrimitives.ReadSingleLittleEndian(payload); return true;
                case TypeCode.Double: if (payload.Length < 8) return false; value = BinaryPrimitives.ReadDoubleLittleEndian(payload); return true;
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
                    if (!DataTypeMapper.TryResolve(entryList[i].Key.DataType, out var type))
                    {
                        results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadConfigError,
                            $"AllenBradley: unsupported DataType '{entryList[i].Key.DataType}'");
                        continue;
                    }

                    await WithTimeoutAsync(
                        token => WriteTypedAsync(tagClient, entryList[i].Key.Address,
                            entryList[i].Value, type, token), ct).ConfigureAwait(false);
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
