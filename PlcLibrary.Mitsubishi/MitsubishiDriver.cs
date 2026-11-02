using Microsoft.Extensions.Logging;
using Snet.Core;
using Snet.Model.data;
using Snet.Model.@enum;
using Snet.Mitsubishi;
using PlcLibrary.DriverDomain;
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.DriverDomain.Parser;
using PlcLibrary.General.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Mitsubishi
{
    [ProtocolDriverName("Mitsubishi")]
    public sealed class MitsubishiDriver : IProtocolDriver
    {
        /// <summary>单次 MC 批量读的最大字数（3E 帧请求数据上限内，取保守值）。</summary>
        private const int MaxWordsPerRead = 480;

        /// <summary>标量类型 → Snet 数组类型（批量读时一次请求读取连续多个元素）。</summary>
        private static readonly Dictionary<Type, DataType> ArrayTypeMapping = new()
        {
            [typeof(short)] = DataType.Int16Array,
            [typeof(ushort)] = DataType.UInt16Array,
            [typeof(int)] = DataType.Int32Array,
            [typeof(uint)] = DataType.UInt32Array,
            [typeof(long)] = DataType.Int64Array,
            [typeof(ulong)] = DataType.UInt64Array,
            [typeof(float)] = DataType.FloatArray,
            [typeof(double)] = DataType.DoubleArray,
        };

        private static readonly Regex AddressPattern = new(
            "^(?<dev>[A-Za-z]+)(?<word>\\d+)(?:\\.(?<bit>\\d+))?$",
            RegexOptions.CultureInvariant);

        private static readonly Regex WordDeviceAddressPattern = new(
            "^(?<dev>ZR|[DWR])(?<word>\\d+)(?:\\.(?<bit>\\d+))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly ILogger<MitsubishiDriver> _logger;
        private readonly MitsubishiDriverConfig _config;
        private readonly object _stateLock = new();
        private MitsubishiOperate? _operate;
        private DriverStatus _status = DriverStatus.Disconnected;

        public MitsubishiDriver(ILogger<MitsubishiDriver> logger, DeviceConfiguration device)
        {
            _logger = logger;
            _config = MitsubishiDriverConfig.Parse(device.ConnectionString);
        }

        public DriverStatus DriverStatus
        {
            get { lock (_stateLock) return _status; }
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await DisconnectAsync(ct).ConfigureAwait(false);
            SetState(null, DriverStatus.Connecting);

            MitsubishiOperate? operate = null;
            try
            {
                var basics = CreateBasics();
                operate = new MitsubishiOperate(basics);
                var result = await operate.OnAsync().ConfigureAwait(false);

                if (!result.Status)
                {
                    SetState(null, DriverStatus.Faulted);
                    MitsubishiLog.LogConnectionFailed(_logger, new InvalidOperationException(result.Message),
                        _config.Host, _config.Port);
                    throw new InvalidOperationException($"Mitsubishi connection failed: {result.Message}");
                }

                SetState(operate, DriverStatus.Connected);
                MitsubishiLog.LogConnected(_logger, _config.Host, _config.Port);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                SetState(null, DriverStatus.Faulted);
                if (operate is not null)
                {
                    try { await operate.OffAsync().ConfigureAwait(false); } catch { }
                    try { operate.Dispose(); } catch { }
                }
                throw;
            }
        }

        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            MitsubishiOperate? old;
            lock (_stateLock)
            {
                old = _operate;
                _operate = null;
                _status = DriverStatus.Disconnected;
            }

            if (old is not null)
            {
                try { await old.OffAsync().ConfigureAwait(false); }
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
                MitsubishiLog.LogReconnected(_logger);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_stateLock) _status = DriverStatus.Faulted;
                MitsubishiLog.LogReconnectFailed(_logger, ex);
                return false;
            }
        }

        public async Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default)
        {
            var operate = GetOperateOrThrow();
            if (points.Length == 0) return [];

            var results = new DriverResult[points.Length];
            var plans = BuildReadPoints(points, results);

            foreach (var group in BuildNumericGroups(plans))
                await ReadNumericGroupAsync(operate, group, results).ConfigureAwait(false);

            foreach (var group in BuildBoolGroups(plans))
                await ReadBoolGroupAsync(operate, group, results).ConfigureAwait(false);

            foreach (var plan in plans.Where(p => p.Mode == ReadMode.BitDevice))
                await ReadBitDeviceAsync(operate, plan, results).ConfigureAwait(false);

            return results;
        }

        public async Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
        {
            var operate = GetOperateOrThrow();
            if (values.Count == 0) return [];

            var entryList = values.ToList();
            var results = new DriverResult[entryList.Count];

            var writeValues = new ConcurrentDictionary<string, object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < entryList.Count; i++)
            {
                var key = NormalizeWriteAddress(entryList[i].Key, out var error);
                if (key is null)
                {
                    results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadConfigError,
                        error ?? "Invalid Mitsubishi write address");
                    continue;
                }

                // 归一化后同地址多点位写入会互相覆盖：后者标 BadConfigError，且不参与批量写
                if (!seen.Add(key))
                {
                    results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadConfigError,
                        "Duplicate address in write batch");
                    continue;
                }
                writeValues[key] = entryList[i].Value;
            }

            try
            {
                if (writeValues.Count > 0)
                {
                    var result = await operate.WriteAsync(writeValues).ConfigureAwait(false);

                    for (var i = 0; i < entryList.Count; i++)
                    {
                        if (results[i].Status == QualityCode.BadConfigError) continue; // 重复地址/非法地址已在前面标记
                        results[i] = result.Status
                            ? DriverResult.Good(entryList[i].Key.Address, null)
                            : DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadCommFailure,
                                result.Message ?? "Unknown error");
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                MitsubishiLog.LogWriteFailed(_logger, ex);
                MarkFaultedIfTransport(ex);
                for (var i = 0; i < entryList.Count; i++)
                {
                    if (results[i].Status != QualityCode.BadConfigError)
                        results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadCommFailure, ex.Message);
                }
            }

            return results;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            MitsubishiLog.LogDisposed(_logger);
        }

        /// <summary>把点位解析成可合并的读计划；非法地址/类型在结果数组里直接标 BadConfigError。</summary>
        private static List<ReadPoint> BuildReadPoints(TagPointConfiguration[] points, DriverResult[] results)
        {
            var plans = new List<ReadPoint>(points.Length);

            for (var i = 0; i < points.Length; i++)
            {
                var point = points[i];

                if (!DataTypeMapper.TryResolve(point.DataType, out var type))
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Mitsubishi: unsupported DataType '{point.DataType}'");
                    continue;
                }

                var match = AddressPattern.Match(point.Address.Trim());
                if (!match.Success)
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Mitsubishi: invalid address '{point.Address}'");
                    continue;
                }

                var device = match.Groups["dev"].Value.ToUpperInvariant();
                if (!int.TryParse(match.Groups["word"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var word)
                    || word is < 0 or > ushort.MaxValue)
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Mitsubishi: address word out of range '{point.Address}'");
                    continue;
                }

                var hasBit = match.Groups["bit"].Success;
                if (type == typeof(bool))
                {
                    if (IsWordDevice(device))
                    {
                        var bit = 0;
                        if (hasBit
                            && (!int.TryParse(match.Groups["bit"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out bit)
                                || bit > 15))
                        {
                            results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                                $"Bit index in '{point.Address}' must be 0-15 for word device '{device}'");
                            continue;
                        }

                        plans.Add(new ReadPoint(i, point, type, DataType.UInt16Array, 1, word, device, bit, ReadMode.WordBit));
                    }
                    else
                    {
                        // 位软元件（M/X/Y/B…）直接位访问，不能按字读取
                        plans.Add(new ReadPoint(i, point, type, DataType.Bool, 1, word, device, 0, ReadMode.BitDevice));
                    }

                    continue;
                }

                if (hasBit)
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Mitsubishi: bit suffix is only supported for bool points '{point.Address}'");
                    continue;
                }

                if (!ArrayTypeMapping.TryGetValue(type, out var arrayType))
                {
                    results[i] = DriverResult.Bad(point.Address, QualityCode.BadConfigError,
                        $"Mitsubishi: unsupported DataType '{point.DataType}' for batch read");
                    continue;
                }

                plans.Add(new ReadPoint(i, point, type, arrayType, ElementWords(type), word, device, 0, ReadMode.Numeric));
            }

            return plans;
        }

        /// <summary>同设备、同类型的连续字点位合并为一次数组读（一次 MC 帧）。</summary>
        private static List<List<ReadPoint>> BuildNumericGroups(List<ReadPoint> plans)
        {
            var groups = new List<List<ReadPoint>>();

            foreach (var typed in plans.Where(p => p.Mode == ReadMode.Numeric)
                         .GroupBy(p => (p.Device, p.ArrayType, p.Words)))
            {
                var ordered = typed.OrderBy(p => p.Word).ToList();
                var current = new List<ReadPoint>();
                var next = 0;

                foreach (var plan in ordered)
                {
                    var maxElements = Math.Max(1, MaxWordsPerRead / plan.Words);
                    if (current.Count == 0 || plan.Word != next || current.Count >= maxElements)
                    {
                        if (current.Count > 0) groups.Add(current);
                        current = [plan];
                    }
                    else
                    {
                        current.Add(plan);
                    }

                    next = plan.Word + plan.Words;
                }

                if (current.Count > 0) groups.Add(current);
            }

            return groups;
        }

        /// <summary>同设备的 bool 点位按覆盖的字区间合并（同一字上的多个位共用一次字数组读）。</summary>
        private static List<List<ReadPoint>> BuildBoolGroups(List<ReadPoint> plans)
        {
            var groups = new List<List<ReadPoint>>();

            foreach (var device in plans.Where(p => p.Mode == ReadMode.WordBit).GroupBy(p => p.Device))
            {
                var ordered = device.OrderBy(p => p.Word).ThenBy(p => p.Bit).ToList();
                var current = new List<ReadPoint>();
                var end = 0;

                foreach (var plan in ordered)
                {
                    if (current.Count == 0 || plan.Word >= end + MaxWordsPerRead)
                    {
                        if (current.Count > 0) groups.Add(current);
                        current = [plan];
                        end = plan.Word + 1;
                    }
                    else
                    {
                        current.Add(plan);
                        end = Math.Max(end, plan.Word + 1);
                    }
                }

                if (current.Count > 0) groups.Add(current);
            }

            return groups;
        }

        private async Task ReadNumericGroupAsync(MitsubishiOperate operate, List<ReadPoint> group, DriverResult[] results)
        {
            var first = group[0];
            var request = CreateAddress(new AddressDetails
            {
                SN = first.Point.TagId,
                AddressName = first.WordAddress,
                AddressDataType = first.ArrayType,
                Length = (ushort)group.Count,
                IsEnable = true,
                AddressType = AddressType.Reality,
            });

            try
            {
                var result = await operate.ReadAsync(request).ConfigureAwait(false);
                if (!result.GetDetails<ConcurrentDictionary<string, AddressValue>>(out var data)
                    || !data!.TryGetValue(first.WordAddress, out var value))
                {
                    MarkGroupBad(group, results, result.Message ?? "No response for address");
                    return;
                }

                if (value.Quality != QualityType.Normal)
                {
                    MarkGroupBad(group, results, $"Quality: {value.Quality}");
                    return;
                }

                if (value.ResultValue is not Array values)
                {
                    MarkGroupBad(group, results, "Response does not contain an array value");
                    return;
                }

                for (var i = 0; i < group.Count; i++)
                {
                    results[group[i].Index] = i < values.Length
                        ? DriverResult.Good(group[i].Point.Address, values.GetValue(i))
                        : DriverResult.Bad(group[i].Point.Address, QualityCode.BadCommFailure,
                            "Missing element in response");
                }
            }
            catch (Exception ex)
            {
                MitsubishiLog.LogReadFailed(_logger, ex);
                MarkFaultedIfTransport(ex);
                MarkGroupBad(group, results, ex.Message);
            }
        }

        private async Task ReadBoolGroupAsync(MitsubishiOperate operate, List<ReadPoint> group, DriverResult[] results)
        {
            var startWord = group[0].Word;
            var endWord = group.Max(p => p.Word) + 1;
            var wordCount = endWord - startWord;
            var first = group[0];
            var request = CreateAddress(new AddressDetails
            {
                SN = first.Point.TagId,
                AddressName = first.DeviceWordAddress(startWord),
                AddressDataType = DataType.UInt16Array,
                Length = (ushort)wordCount,
                IsEnable = true,
                AddressType = AddressType.Reality,
            });

            try
            {
                var result = await operate.ReadAsync(request).ConfigureAwait(false);
                if (!result.GetDetails<ConcurrentDictionary<string, AddressValue>>(out var data)
                    || !data!.TryGetValue(first.DeviceWordAddress(startWord), out var value))
                {
                    MarkGroupBad(group, results, result.Message ?? "No response for address");
                    return;
                }

                if (value.Quality != QualityType.Normal)
                {
                    MarkGroupBad(group, results, $"Quality: {value.Quality}");
                    return;
                }

                if (value.ResultValue is not Array words || words.Length < wordCount)
                {
                    MarkGroupBad(group, results, "Response does not contain enough words");
                    return;
                }

                foreach (var plan in group)
                {
                    var index = plan.Word - startWord;
                    var word = Convert.ToUInt16(words.GetValue(index), CultureInfo.InvariantCulture);
                    results[plan.Index] = DriverResult.Good(plan.Point.Address, ((word >> plan.Bit) & 1) != 0);
                }
            }
            catch (Exception ex)
            {
                MitsubishiLog.LogReadFailed(_logger, ex);
                MarkFaultedIfTransport(ex);
                MarkGroupBad(group, results, ex.Message);
            }
        }

        private static void MarkGroupBad(List<ReadPoint> group, DriverResult[] results, string message)
        {
            foreach (var plan in group)
                results[plan.Index] = DriverResult.Bad(plan.Point.Address, QualityCode.BadCommFailure, message);
        }

        /// <summary>位软元件（M/X/Y/B…）逐点位访问；这类设备不支持按字读取，无法用数组批量合并。</summary>
        private async Task ReadBitDeviceAsync(MitsubishiOperate operate, ReadPoint plan, DriverResult[] results)
        {
            var request = CreateAddress(new AddressDetails
            {
                SN = plan.Point.TagId,
                AddressName = plan.Point.Address,
                AddressDataType = DataType.Bool,
                Length = 1,
                IsEnable = true,
                AddressType = AddressType.Reality,
            });

            try
            {
                var result = await operate.ReadAsync(request).ConfigureAwait(false);
                if (!result.GetDetails<ConcurrentDictionary<string, AddressValue>>(out var data)
                    || !data!.TryGetValue(plan.Point.Address, out var value))
                {
                    results[plan.Index] = DriverResult.Bad(plan.Point.Address, QualityCode.BadCommFailure,
                        result.Message ?? "No response for address");
                    return;
                }

                results[plan.Index] = value.Quality == QualityType.Normal && value.ResultValue is bool flag
                    ? DriverResult.Good(plan.Point.Address, flag)
                    : DriverResult.Bad(plan.Point.Address, QualityCode.BadCommFailure,
                        value.Quality != QualityType.Normal ? $"Quality: {value.Quality}" : "Response is not a boolean");
            }
            catch (Exception ex)
            {
                MitsubishiLog.LogReadFailed(_logger, ex);
                MarkFaultedIfTransport(ex);
                results[plan.Index] = DriverResult.Bad(plan.Point.Address, QualityCode.BadCommFailure, ex.Message);
            }
        }

        private static Address CreateAddress(AddressDetails details) => new()
        {
            SN = Guid.NewGuid().ToString(),
            CreationTime = DateTime.Now,
            AddressArray = [details],
        };

        /// <summary>
        /// 字软元件的 bool 写入地址：Snet 对 <c>D110.5</c> 会先读字再按位回写（read-modify-write），
        /// 而对 <c>D110</c> 会发 MC 位写命令（head=110，按规范等价于 D6.14）。
        /// 因此统一补全为 <c>D110.&lt;bit&gt;</c> 形式。
        /// </summary>
        private static string? NormalizeWriteAddress(TagPointConfiguration point, out string? error)
        {
            error = null;
            if (DataTypeMapper.Resolve(point.DataType) != typeof(bool)) return point.Address;

            if (!TryParseWordDeviceBitAddress(point.Address, out var wordAddress, out var bitIndex, out error))
                return point.Address;

            return error is not null
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"{wordAddress}.{bitIndex}");
        }

        /// <summary>
        /// 解析字软元件地址（D/W/R/ZR + 可选 .bit）。返回 false 表示不是字软元件（如 M110），
        /// 返回 true 且 error 非空表示是字软元件但位号非法（&gt;15）。
        /// </summary>
        private static bool TryParseWordDeviceBitAddress(
            string address, out string wordAddress, out int bitIndex, out string? error)
        {
            wordAddress = address;
            bitIndex = 0;
            error = null;

            var match = WordDeviceAddressPattern.Match(address);
            if (!match.Success) return false;

            var device = match.Groups["dev"].Value.ToUpperInvariant();
            if (device is not ("D" or "W" or "R" or "ZR")) return false;

            var word = int.Parse(match.Groups["word"].Value, CultureInfo.InvariantCulture);
            wordAddress = string.Concat(device, word.ToString(CultureInfo.InvariantCulture));

            if (!match.Groups["bit"].Success) return true;

            if (!int.TryParse(match.Groups["bit"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out bitIndex)
                || bitIndex > 15)
            {
                error = $"Bit index in '{address}' must be 0-15 for word device '{device}'";
                return true;
            }

            return true;
        }

        private static int ElementWords(Type type) => Type.GetTypeCode(type) switch
        {
            TypeCode.Int16 or TypeCode.UInt16 => 1,
            TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Single => 2,
            TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Double => 4,
            _ => 0,
        };

        private static bool IsWordDevice(string device) => device is "D" or "W" or "R" or "ZR";

        private enum ReadMode
        {
            Numeric,
            WordBit,
            BitDevice,
        }

        private MitsubishiData.Basics CreateBasics()
        {
            return new MitsubishiData.Basics
            {
                IpAddress = _config.Host,
                Port = _config.Port,
                ConnectTimeOut = _config.Timeout,
                ReceiveTimeOut = _config.Timeout,
                ProtocolType = _config.ProtocolType switch
                {
                    "MC" => MitsubishiData.ProtocolType.MelsecMcNet,
                    "A1E" => MitsubishiData.ProtocolType.MelsecA1ENet,
                    "A3C" => MitsubishiData.ProtocolType.MelsecA1EAsciiNet,
                    "FX" => MitsubishiData.ProtocolType.MelsecFxSerial,
                    _ => throw new InvalidOperationException(
                        $"Unknown Mitsubishi protocol type '{_config.ProtocolType}'. Supported: MC / A1E / A3C / FX")
                },
            };
        }

        private void SetState(MitsubishiOperate? operate, DriverStatus status)
        {
            lock (_stateLock)
            {
                _operate = operate;
                _status = status;
            }
        }

        private MitsubishiOperate GetOperateOrThrow()
        {
            lock (_stateLock)
            {
                if (_operate is null || _status != DriverStatus.Connected)
                    throw new InvalidOperationException("Mitsubishi driver is not connected");
                return _operate;
            }
        }

        /// <summary>通信级故障时置 Faulted，连接池据此丢弃驱动并重建，使断线重连生效。</summary>
        private void MarkFaultedIfTransport(Exception ex)
        {
            if (TransportFailureDetector.IsTransportFailure(ex))
                lock (_stateLock) _status = DriverStatus.Faulted;
        }

        private sealed class ReadPoint(
            int index,
            TagPointConfiguration point,
            Type type,
            DataType arrayType,
            int words,
            int word,
            string device,
            int bit,
            ReadMode mode)
        {
            public int Index { get; } = index;

            public TagPointConfiguration Point { get; } = point;

            public Type Type { get; } = type;

            public DataType ArrayType { get; } = arrayType;

            public int Words { get; } = words;

            public int Word { get; } = word;

            public string Device { get; } = device;

            public int Bit { get; } = bit;

            public ReadMode Mode { get; } = mode;

            public string WordAddress => DeviceWordAddress(Word);

            public string DeviceWordAddress(int word) => string.Concat(Device, word.ToString(CultureInfo.InvariantCulture));
        }
    }
}
