using Microsoft.Extensions.Logging;
using NewLife.Omron.Protocols;
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

namespace PlcLibrary.Omron
{
    [ProtocolDriverName("Omron_FINS")]
    public sealed class OmronDriver : IProtocolDriver
    {
        /// <summary>单次 FINS 读请求的最大字数（协议上限 999，取保守值）。</summary>
        private const int MaxWordsPerRead = 480;

        /// <summary>单次 FINS 位读请求的最大位数。</summary>
        private const int MaxBitsPerRead = 256;

        /// <summary>合并相邻位点时允许的最大空隙（位），避免为稀疏点读入过大区间。</summary>
        private const int MaxBitGap = 64;

        private readonly ILogger<OmronDriver> _logger;
        private readonly OmronDriverConfig _config;
        private readonly object _stateLock = new();
        private FinsClient? _finsClient;
        private DriverStatus _status = DriverStatus.Disconnected;

        private static readonly Dictionary<Type, MethodInfo?> WriteMethods = BuildWriteMethods();

        private static Dictionary<Type, MethodInfo?> BuildWriteMethods()
        {
            var methods = typeof(FinsClient).GetMethods(BindingFlags.Public | BindingFlags.Instance);
            return new()
            {
                [typeof(bool)] = FindMethod(methods, "WriteBoolAsync", 2),
                [typeof(short)] = FindMethod(methods, "WriteInt16Async", 2),
                [typeof(int)] = FindMethod(methods, "WriteInt32Async", 2),
                [typeof(long)] = FindMethod(methods, "WriteInt64Async", 2),
                [typeof(ushort)] = FindMethod(methods, "WriteUInt16Async", 2),
                [typeof(uint)] = FindMethod(methods, "WriteUInt32Async", 2),
                [typeof(ulong)] = FindMethod(methods, "WriteUInt64Async", 2),
                [typeof(float)] = FindMethod(methods, "WriteFloatAsync", 2),
                [typeof(double)] = FindMethod(methods, "WriteDoubleAsync", 2),
            };
        }

        /// <summary>按名称 + 参数个数精确匹配，避免第三方库出现重载时取错方法；找不到返回 null（调用处抛 NotSupportedException）。</summary>
        private static MethodInfo? FindMethod(IEnumerable<MethodInfo> methods, string name, int parameterCount)
            => methods.FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameterCount);

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

            FinsClient? client = null;
            try
            {
                client = new FinsClient
                {
                    IpAddress = _config.Host,
                    Port = _config.Port,
                    ConnectTimeOut = _config.Timeout,
                    ReceiveTimeOut = _config.Timeout,
                    DataFormat = DataFormat.CDAB,
                    AutoReconnect = true,
                };

                await client.ConnectAsync().ConfigureAwait(false);

                SetState(client, DriverStatus.Connected);
                OmronLog.LogConnected(_logger, _config.Host, _config.Port);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SetState(null, DriverStatus.Faulted);
                if (client is not null)
                {
                    try { client.Close(); } catch { }
                    try { client.Dispose(); } catch { }
                }
                OmronLog.LogConnectionFailed(_logger, ex, _config.Host, _config.Port);
                throw;
            }
        }

        public Task DisconnectAsync(CancellationToken ct = default)
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

            return Task.CompletedTask;
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
            var client = GetClientOrThrow();
            if (points.Length == 0) return [];

            var results = new DriverResult[points.Length];
            var plans = new List<ReadPlan>(points.Length);

            for (var i = 0; i < points.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var point = points[i];

                if (!DataTypeMapper.TryResolve(point.DataType, out var resolved))
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Omron: unsupported DataType '{point.DataType}'. Supported: bool, short, ushort, int, uint, long, ulong, float, double");
                    continue;
                }

                var words = 0;
                if (resolved != typeof(bool) && !TryGetElement(resolved, out words))
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Omron: unsupported DataType '{point.DataType}' for FINS read");
                    continue;
                }

                FinsAddress address;
                try { address = FinsAddress.Parse(point.Address); }
                catch (Exception ex)
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Omron: invalid address '{point.Address}': {ex.Message}");
                    continue;
                }

                if (address.BitOffset > 15)
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Omron: bit offset in '{point.Address}' must be 0-15");
                    continue;
                }

                // 位后缀只对 bool 有意义；数值点位带 .bit 会被底层忽略并读到整字，
                // 属于静默错值，必须在本地拒绝。
                if (resolved != typeof(bool) && address.IsBit)
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Omron: bit suffix is only supported for bool points ('{point.Address}')");
                    continue;
                }

                plans.Add(new ReadPlan(i, point, resolved, address, words));
            }

            foreach (var group in BuildWordGroups(plans))
                await ReadWordGroupAsync(client, group, results).ConfigureAwait(false);

            foreach (var group in BuildBitGroups(plans))
                await ReadBitGroupAsync(client, group, results).ConfigureAwait(false);

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
                    if (!DataTypeMapper.TryResolve(entryList[i].Key.DataType, out var type))
                        throw new NotSupportedException(
                            $"Omron: unsupported DataType '{entryList[i].Key.DataType}'. Supported: bool, short, ushort, int, uint, long, ulong, float, double");

                    await WriteTypedAsync(finsClient, entryList[i].Key.Address, entryList[i].Value, type).ConfigureAwait(false);
                    results[i] = DriverResult.Good(entryList[i].Key.Address, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (NotSupportedException ex)
                {
                    results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadConfigError, ex.Message);
                }
                catch (Exception ex)
                {
                    OmronLog.LogWritePointFailed(_logger, ex, entryList[i].Key.Address);
                    MarkFaultedIfTransport(ex);
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

        /// <summary>按存储区把字访问点位合并为尽量少的连续区间（单次读请求覆盖多个点位）。</summary>
        private static List<List<ReadPlan>> BuildWordGroups(List<ReadPlan> plans)
        {
            var groups = new List<List<ReadPlan>>();
            foreach (var area in plans.Where(p => !p.IsBit).GroupBy(p => p.Address.MemoryType))
            {
                var ordered = area.OrderBy(p => p.Address.Address).ToList();
                var current = new List<ReadPlan>();
                var start = 0;
                var end = 0;

                foreach (var plan in ordered)
                {
                    var planStart = plan.Address.Address;
                    var planEnd = planStart + plan.Words;
                    var mergedEnd = Math.Max(end, planEnd);

                    if (current.Count == 0 || planStart > end || mergedEnd - start > MaxWordsPerRead)
                    {
                        if (current.Count > 0) groups.Add(current);
                        current = [plan];
                        start = planStart;
                        end = planEnd;
                    }
                    else
                    {
                        current.Add(plan);
                        end = mergedEnd;
                    }
                }

                if (current.Count > 0) groups.Add(current);
            }

            return groups;
        }

        /// <summary>按位软元件合并相邻位点（允许小空隙），一次位读覆盖多个 bool 点位。</summary>
        private static List<List<ReadPlan>> BuildBitGroups(List<ReadPlan> plans)
        {
            var groups = new List<List<ReadPlan>>();
            foreach (var area in plans.Where(p => p.IsBit).GroupBy(p => p.Address.MemoryType))
            {
                var ordered = area.OrderBy(p => p.BitIndex).ToList();
                var current = new List<ReadPlan>();
                var start = 0;
                var end = 0;

                foreach (var plan in ordered)
                {
                    if (current.Count == 0
                        || plan.BitIndex > end + MaxBitGap
                        || plan.BitIndex - start >= MaxBitsPerRead)
                    {
                        if (current.Count > 0) groups.Add(current);
                        current = [plan];
                        start = plan.BitIndex;
                        end = plan.BitIndex + 1;
                    }
                    else
                    {
                        current.Add(plan);
                        end = Math.Max(end, plan.BitIndex + 1);
                    }
                }

                if (current.Count > 0) groups.Add(current);
            }

            return groups;
        }

        private async Task ReadWordGroupAsync(FinsClient client, List<ReadPlan> group, DriverResult[] results)
        {
            var start = group[0].Address.Address;
            var wordCount = group.Max(p => p.Address.Address + p.Words) - start;
            var startAddress = group[0].Point.Address;

            try
            {
                var bytes = await Task.Run(() => client.Read(startAddress, (ushort)wordCount)).ConfigureAwait(false);
                var transform = client.Transform;

                foreach (var plan in group)
                {
                    var offset = (plan.Address.Address - start) * 2;
                    var value = ReadElement(transform, bytes, offset, plan.Type);
                    results[plan.Index] = value is null
                        ? DriverResult.Bad(plan.Point.Address, QualityCode.BadCommFailure,
                            $"Omron: response too short for '{plan.Point.Address}'")
                        : DriverResult.Good(plan.Point.Address, value);
                }
            }
            catch (Exception ex)
            {
                OmronLog.LogReadGroupFailed(_logger, ex, startAddress, group.Count);
                MarkFaultedIfTransport(ex);
                foreach (var plan in group)
                    results[plan.Index] = DriverResult.Bad(plan.Point.Address, QualityCode.BadCommFailure, ex.Message);
            }
        }

        private async Task ReadBitGroupAsync(FinsClient client, List<ReadPlan> group, DriverResult[] results)
        {
            var start = group[0].BitIndex;
            var count = group.Max(p => p.BitIndex) - start + 1;
            var startAddress = group[0].BitAddress;

            try
            {
                var bits = await Task.Run(() => client.ReadBit(startAddress, (ushort)count)).ConfigureAwait(false);

                foreach (var plan in group)
                {
                    var index = plan.BitIndex - start;
                    results[plan.Index] = index < bits.Length
                        ? DriverResult.Good(plan.Point.Address, bits[index] != 0)
                        : DriverResult.Bad(plan.Point.Address, QualityCode.BadCommFailure,
                            $"Omron: bit response too short for '{plan.Point.Address}'");
                }
            }
            catch (Exception ex)
            {
                OmronLog.LogReadGroupFailed(_logger, ex, startAddress, group.Count);
                MarkFaultedIfTransport(ex);
                foreach (var plan in group)
                    results[plan.Index] = DriverResult.Bad(plan.Point.Address, QualityCode.BadCommFailure, ex.Message);
            }
        }

        private static object? ReadElement(ByteTransform transform, byte[] buffer, int offset, Type type)
        {
            var bytes = ElementByteCount(type);
            if (offset < 0 || offset + bytes > buffer.Length) return null;

            return Type.GetTypeCode(type) switch
            {
                TypeCode.Int16 => transform.TransInt16(buffer, offset),
                TypeCode.UInt16 => transform.TransUInt16(buffer, offset),
                TypeCode.Int32 => transform.TransInt32(buffer, offset),
                TypeCode.UInt32 => transform.TransUInt32(buffer, offset),
                TypeCode.Int64 => transform.TransInt64(buffer, offset),
                TypeCode.UInt64 => transform.TransUInt64(buffer, offset),
                TypeCode.Single => transform.TransSingle(buffer, offset),
                TypeCode.Double => transform.TransDouble(buffer, offset),
                _ => null,
            };
        }

        private static int ElementByteCount(Type type) => Type.GetTypeCode(type) switch
        {
            TypeCode.Int16 or TypeCode.UInt16 => 2,
            TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Single => 4,
            TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Double => 8,
            _ => 0,
        };

        private static bool TryGetElement(Type type, out int words)
        {
            words = ElementByteCount(type) / 2;
            return words > 0;
        }

        private static async Task WriteTypedAsync(FinsClient client, string address, object value, Type type)
        {
            if (!WriteMethods.TryGetValue(type, out var method) || method is null)
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

        /// <summary>通信级故障时置 Faulted，连接池据此丢弃驱动并重建，使断线重连生效。</summary>
        private void MarkFaultedIfTransport(Exception ex)
        {
            if (TransportFailureDetector.IsTransportFailure(ex))
                lock (_stateLock) _status = DriverStatus.Faulted;
        }

        private sealed class ReadPlan(
            int index,
            TagPointConfiguration point,
            Type type,
            FinsAddress address,
            int words)
        {
            public int Index { get; } = index;

            public TagPointConfiguration Point { get; } = point;

            public Type Type { get; } = type;

            public FinsAddress Address { get; } = address;

            public int Words { get; } = words;

            /// <summary>bool 点位按位访问：字地址 * 16 + 位偏移。</summary>
            public bool IsBit => Type == typeof(bool);

            public int BitIndex => Address.Address * 16 + Address.BitOffset;

            /// <summary>位读请求的起始地址字符串（无位后缀时补 .0）。</summary>
            public string BitAddress => Point.Address.Contains('.')
                ? Point.Address
                : string.Concat(Point.Address, ".0");
        }
    }
}
