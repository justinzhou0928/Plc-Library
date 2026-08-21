using Microsoft.Extensions.Logging;
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Snet.Core;
using Snet.Model.data;
using Snet.Model.@enum;
using Snet.Mitsubishi;

namespace PlcLibrary.Mitsubishi
{
    [ProtocolDriverName("Mitsubishi")]
    public sealed class MitsubishiDriver : IProtocolDriver
    {
        private readonly ILogger<MitsubishiDriver> _logger;
        private readonly MitsubishiDriverConfig _config;
        private readonly object _stateLock = new();
        private MitsubishiOperate? _operate;
        private DriverStatus _status = DriverStatus.Disconnected;

        private static readonly Dictionary<Type, DataType> DataTypeMapping = new()
        {
            [typeof(bool)]   = DataType.Bool,
            [typeof(byte)]   = DataType.Byte,
            [typeof(short)]  = DataType.Int16,
            [typeof(ushort)] = DataType.UInt16,
            [typeof(int)]    = DataType.Int32,
            [typeof(uint)]   = DataType.UInt32,
            [typeof(long)]   = DataType.Int64,
            [typeof(ulong)]  = DataType.UInt64,
            [typeof(float)]  = DataType.Float,
            [typeof(double)] = DataType.Double,
            [typeof(string)] = DataType.String,
        };

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

            var address = BuildAddress(points);
            var results = new DriverResult[points.Length];

            try
            {
                var result = await operate.ReadAsync(address).ConfigureAwait(false);
                if (result.GetDetails<ConcurrentDictionary<string, AddressValue>>(out var data))
                {
                    for (var i = 0; i < points.Length; i++)
                    {
                        // Snet 响应字典的 key 可能是 SN（TagId）或 AddressName，双保险查找
                        if (data!.TryGetValue(points[i].TagId, out var value)
                            || (!string.Equals(points[i].TagId, points[i].Address, StringComparison.OrdinalIgnoreCase)
                                && data.TryGetValue(points[i].Address, out value)))
                        {
                            results[i] = value.Quality == QualityType.Normal
                                ? DriverResult.Good(points[i].Address, value.ResultValue)
                                : DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure,
                                    $"Quality: {value.Quality}");
                        }
                        else
                        {
                            results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure,
                                "Point not found in response");
                        }
                    }
                }
                else
                {
                    for (var i = 0; i < points.Length; i++)
                        results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure,
                            result.Message ?? "Unknown error");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                MitsubishiLog.LogReadFailed(_logger, ex);
                MarkFaultedIfTransport(ex);
                for (var i = 0; i < points.Length; i++)
                    results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure, ex.Message);
            }

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
                // 同 Address 多点位写入会互相覆盖：后者标 BadConfigError，且不参与批量写
                if (!seen.Add(entryList[i].Key.Address))
                {
                    results[i] = DriverResult.Bad(entryList[i].Key.Address, QualityCode.BadConfigError,
                        "Duplicate address in write batch");
                    continue;
                }
                writeValues[entryList[i].Key.Address] = entryList[i].Value;
            }

            try
            {
                if (writeValues.Count > 0)
                {
                    var result = await operate.WriteAsync(writeValues).ConfigureAwait(false);

                    for (var i = 0; i < entryList.Count; i++)
                    {
                        if (results[i].Status == QualityCode.BadConfigError) continue; // 重复地址已在前面标记
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

        private static Address BuildAddress(TagPointConfiguration[] points)
        {
            return new Address
            {
                SN = Guid.NewGuid().ToString(),
                CreationTime = DateTime.Now,
                AddressArray = points.Select(p => new AddressDetails
                {
                    SN = p.TagId,
                    AddressName = p.Address,
                    AddressDataType = ResolveDataType(p.DataType),
                    IsEnable = true,
                    AddressType = AddressType.Reality
                }).ToList()
            };
        }

        private static DataType ResolveDataType(string? dataType)
        {
            var type = DataTypeMapper.Resolve(dataType);
            return DataTypeMapping.TryGetValue(type, out var dt) ? dt : DataType.Int16;
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
    }
}
