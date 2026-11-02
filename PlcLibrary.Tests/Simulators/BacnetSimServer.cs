using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlcLibrary.Tests.Simulators;

/// <summary>
/// 进程内 BACnet/IP 设备模拟器：在真实 UDP 套接字上应答驱动的 ReadProperty /
/// ReadPropertyMultiple / WriteProperty 请求，并可应答 WhoIs 发现。
/// 只实现验证驱动所需的最小对象集：AV:1、AV:2、AI:0、BV:3。
/// </summary>
/// <remarks>
/// 拓扑说明（Windows 回环）：
/// 驱动把配置里的 Port 用成本地共享端口（绑定 0.0.0.0:Port；LocalEndpointIp 只作用于
/// Port+1 那个独占套接字）。远程设备端口则来自 host 字符串里的端口，因为驱动把 Port
/// 传给了 BacnetAddress 的 network 参数（见 CreateConnectionString 的说明）。
/// 模拟器因此绑定 127.0.0.2:Port 的独占套接字；驱动虽然也绑了通配的 0.0.0.0:Port，
/// 但 Windows 会把目标为 127.0.0.2:Port 的单播交给更具体的那个套接字。
/// 广播 WhoIs 不会送到独占套接字，所以另开一个 0.0.0.0:Port 的普通 UdpClient
/// 只负责接收广播，并让模拟器从 127.0.0.2:Port 回 IAm。
/// </remarks>
public sealed class BacnetSimServer : IAsyncDisposable
{
    /// <summary>模拟器监听的回环别名地址。</summary>
    public const string LoopbackAddress = "127.0.0.2";

    private const int DiscoveryTimeoutMs = 1000;

    private readonly object _gate = new();
    private readonly Dictionary<(BacnetObjectTypes Type, uint Instance), SimObject> _objects;
    private readonly BacnetClient _client;
    private readonly UdpClient _broadcastListener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _broadcastLoop;
    private int _whoIsRequestCount;
    private int _disposed;

    /// <param name="deviceInstance">模拟设备的 Device 实例号，用于 WhoIs/IAm 发现。</param>
    /// <param name="perObjectErrors">
    /// true 时对不存在的对象返回 RMP-ACK 内逐对象错误（协议推荐形式）；
    /// false 时返回整包 Error PDU。用于验证驱动对两种错误形式的不同处理。
    /// </param>
    /// <param name="omitUnknownObjects">
    /// true 时在 RMP-ACK 中直接省略不存在的对象（模拟不完整/异常响应），
    /// 用于验证驱动对缺失结果的处理。
    /// </param>
    public BacnetSimServer(uint deviceInstance = 1234, bool perObjectErrors = false, bool omitUnknownObjects = false)
    {
        DeviceInstance = deviceInstance;
        PerObjectErrors = perObjectErrors;
        OmitUnknownObjects = omitUnknownObjects;
        _objects = CreateDefaultObjects();

        for (var attempt = 0; ; attempt++)
        {
            var listener = BindBroadcastListener();
            var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
            BacnetClient? client = null;
            try
            {
                var transport = new BacnetIpUdpProtocolTransport(
                    port, useExclusivePort: true, localEndpointIp: LoopbackAddress);
                client = new BacnetClient(transport) { Timeout = DiscoveryTimeoutMs };
                client.OnReadPropertyMultipleRequest += HandleReadPropertyMultiple;
                client.OnReadPropertyRequest += HandleReadProperty;
                client.OnWritePropertyRequest += HandleWriteProperty;
                client.Start();

                _client = client;
                _broadcastListener = listener;
                Port = port;
                _broadcastLoop = Task.Run(() => BroadcastLoopAsync(_cts.Token));
                return;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                listener.Dispose();
                if (attempt >= 19)
                    throw new InvalidOperationException("Unable to start the BACnet simulator on a free loopback port.", ex);
            }
        }
    }

    /// <summary>模拟设备实例号（WhoIs/IAm 使用）。</summary>
    public uint DeviceInstance { get; }

    /// <summary>模拟器监听的 UDP 端口（也是驱动连接串里的 port）。</summary>
    public int Port { get; }

    /// <summary>模拟器的 IP 地址。</summary>
    public string Host => LoopbackAddress;

    /// <summary>收到 WhoIs 的次数（用于断言发现流程确实发生）。</summary>
    public int WhoIsRequestCount => Volatile.Read(ref _whoIsRequestCount);

    /// <summary>最近一次 WriteProperty 请求的原始参数（用于记录驱动的实际报文行为）。</summary>
    public SimWriteRequest? LastWrite { get; private set; }

    /// <summary>最近一次 ReadPropertyMultiple 请求里的 propertyArrayIndex。</summary>
    public uint? LastReadArrayIndex { get; private set; }

    /// <summary>是否对不存在的对象返回逐对象错误。</summary>
    public bool PerObjectErrors { get; }

    /// <summary>是否在 RMP-ACK 中省略不存在的对象。</summary>
    public bool OmitUnknownObjects { get; }

    /// <summary>
    /// 生成驱动连接串。注意：端口必须写进 host。驱动把 Port 传给
    /// <see cref="BacnetAddress"/> 的 network 参数而非端口，远程端口实际取 host 中的端口
    /// （缺省 47808）；不写进 host 时驱动会连到 47808 而不是本模拟器的临时端口。
    /// </summary>
    public string CreateConnectionString(uint deviceInstance = 0)
    {
        var builder = new StringBuilder();
        // host 只写 IP，端口由 port 字段表达：驱动必须把端口编码进 BacnetAddress 的地址字符串，
        // 而不是传给 network 参数（回归点）。
        builder.Append("host:").Append(Host);
        builder.Append(";port:").Append(Port);
        builder.Append(";localendpointip:127.0.0.1");
        builder.Append(";timeout:").Append(DiscoveryTimeoutMs);
        if (deviceInstance != 0)
            builder.Append(";deviceinstance:").Append(deviceInstance);
        return builder.ToString();
    }

    /// <summary>读取模拟对象当前 Present_Value 的 CLR 值；对象不存在返回 null。</summary>
    public object? GetPresentValue(BacnetObjectTypes type, uint instance)
    {
        lock (_gate)
        {
            return _objects.TryGetValue((type, instance), out var obj) ? obj.PresentValue.Value : null;
        }
    }

    private static Dictionary<(BacnetObjectTypes Type, uint Instance), SimObject> CreateDefaultObjects() => new()
    {
        [(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1)] = new(new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 12.5f)),
        [(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 2)] = new(new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 3.25f)),
        [(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 0)] = new(new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 7.75f)),
        // 真实设备的 binary-pv 是 ENUMERATED；这里用 BOOLEAN 是为了断言驱动把 bool 原样透传。
        [(BacnetObjectTypes.OBJECT_BINARY_VALUE, 3)] = new(new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, true)),
    };

    private static UdpClient BindBroadcastListener()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var listener = new UdpClient { ExclusiveAddressUse = false };
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
            if (port < 65535 && IsUdpPortFree(IPAddress.Loopback, port + 1))
                return listener;

            listener.Dispose();
        }

        throw new InvalidOperationException("Unable to allocate a free UDP port pair for the BACnet simulator.");
    }

    private static bool IsUdpPortFree(IPAddress address, int port)
    {
        try
        {
            using var probe = new UdpClient(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private void HandleReadPropertyMultiple(BacnetClient sender, BacnetAddress adr, byte invokeId,
        IList<BacnetReadAccessSpecification> specs, BacnetMaxSegments maxSegments)
    {
        var results = new List<BacnetReadAccessResult>();
        lock (_gate)
        {
            foreach (var spec in specs)
            {
                if (!_objects.TryGetValue((spec.objectIdentifier.Type, spec.objectIdentifier.Instance), out var obj))
                {
                    if (OmitUnknownObjects)
                        continue;

                    if (!PerObjectErrors)
                    {
                        sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROP_MULTIPLE,
                            invokeId, BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT);
                        return;
                    }

                    results.Add(BuildObjectErrorResult(spec.objectIdentifier,
                        BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT));
                    continue;
                }

                var propertyValues = new List<BacnetPropertyValue>();
                foreach (var reference in spec.propertyReferences)
                {
                    LastReadArrayIndex = reference.propertyArrayIndex;
                    if (reference.propertyIdentifier != (uint)BacnetPropertyIds.PROP_PRESENT_VALUE)
                    {
                        sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROP_MULTIPLE,
                            invokeId, BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY);
                        return;
                    }

                    propertyValues.Add(new BacnetPropertyValue
                    {
                        property = new BacnetPropertyReference(BacnetPropertyIds.PROP_PRESENT_VALUE, reference.propertyArrayIndex),
                        value = [obj.PresentValue],
                    });
                }

                results.Add(new BacnetReadAccessResult(spec.objectIdentifier, propertyValues));
            }
        }

        sender.ReadPropertyMultipleResponse(adr, invokeId, null!, results);
    }

    private void HandleReadProperty(BacnetClient sender, BacnetAddress adr, byte invokeId,
        BacnetObjectId objectId, BacnetPropertyReference property, BacnetMaxSegments maxSegments)
    {
        lock (_gate)
        {
            if (!_objects.TryGetValue((objectId.Type, objectId.Instance), out var obj))
            {
                sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, invokeId,
                    BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT);
                return;
            }

            if (property.propertyIdentifier != (uint)BacnetPropertyIds.PROP_PRESENT_VALUE)
            {
                sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, invokeId,
                    BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY);
                return;
            }

            sender.ReadPropertyResponse(adr, invokeId, null!, objectId, property, [obj.PresentValue]);
        }
    }

    private void HandleWriteProperty(BacnetClient sender, BacnetAddress adr, byte invokeId,
        BacnetObjectId objectId, BacnetPropertyValue value, BacnetMaxSegments maxSegments)
    {
        lock (_gate)
        {
            LastWrite = new SimWriteRequest(objectId.Type, objectId.Instance, value.property.propertyIdentifier,
                value.property.propertyArrayIndex, value.priority, value.value?.Count > 0 ? value.value[0] : default);

            if (!_objects.TryGetValue((objectId.Type, objectId.Instance), out var obj))
            {
                sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId,
                    BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT);
                return;
            }

            if (value.property.propertyIdentifier != (uint)BacnetPropertyIds.PROP_PRESENT_VALUE)
            {
                sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId,
                    BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY);
                return;
            }

            if (value.value is not { Count: > 0 })
            {
                sender.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId,
                    BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_MISSING_REQUIRED_PARAMETER);
                return;
            }

            // 刻意忽略 propertyArrayIndex：驱动当前会把 config.Timeout 传到这个参数上（见测试）。
            obj.PresentValue = value.value[0];
            sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invokeId);
        }
    }

    private static BacnetReadAccessResult BuildObjectErrorResult(BacnetObjectId objectId,
        BacnetErrorClasses errorClass, BacnetErrorCodes errorCode) => new(objectId,
        new List<BacnetPropertyValue>
        {
            new()
            {
                property = new BacnetPropertyReference(BacnetPropertyIds.PROP_PRESENT_VALUE, 0),
                value = [new BacnetValue(new BacnetError(errorClass, errorCode))],
            },
        });

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _broadcastListener.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            if (!IsWhoIs(received.Buffer))
                continue;

            Interlocked.Increment(ref _whoIsRequestCount);
            var remote = received.RemoteEndPoint;
            var receiver = new BacnetAddress(BacnetAddressTypes.IP, $"{remote.Address}:{remote.Port}");
            try
            {
                _client.Iam(DeviceInstance, BacnetSegmentations.SEGMENTATION_NONE, receiver);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception)
            {
                // 回 IAm 失败不会影响之后的 Read/Write 测试；让发现测试自己超时失败。
            }
        }
    }

    /// <summary>
    /// 判断 UDP 报文是否为 WhoIs。接受 BVLC Original-Broadcast（0x0B）与 Original-Unicast（0x0A），
    /// 并按 NPDU 控制位跳过 DNET/DADR/hop-count 与 SNET/SADR 字段。
    /// </summary>
    private static bool IsWhoIs(byte[] buffer)
    {
        if (buffer.Length < 6 || buffer[0] != 0x81 || (buffer[1] != 0x0A && buffer[1] != 0x0B))
            return false;

        var offset = 4;
        if (buffer[offset] != 0x01)
            return false;

        var control = buffer[offset + 1];
        offset += 2;

        if ((control & 0x20) != 0)
        {
            if (offset + 3 > buffer.Length)
                return false;

            var destinationLength = buffer[offset + 2];
            offset += 3 + destinationLength + 1; // DNET(2) + DLEN(1) + DADR + hop count(1)
        }

        if ((control & 0x08) != 0)
        {
            if (offset + 3 > buffer.Length)
                return false;

            var sourceLength = buffer[offset + 2];
            offset += 3 + sourceLength; // SNET(2) + SLEN(1) + SADR
        }

        return offset + 1 < buffer.Length
            && buffer[offset] == 0x10
            && buffer[offset + 1] == 0x08;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _broadcastListener.Dispose();
        try
        {
            await _broadcastLoop.ConfigureAwait(false);
        }
        catch
        {
            // 关闭阶段的异常无需传播。
        }

        _client.Dispose();
        _cts.Dispose();
    }

    private sealed class SimObject(BacnetValue presentValue)
    {
        public BacnetValue PresentValue { get; set; } = presentValue;
    }
}

/// <summary>模拟器收到的 WriteProperty 参数快照。</summary>
public readonly record struct SimWriteRequest(
    BacnetObjectTypes ObjectType,
    uint ObjectInstance,
    uint PropertyId,
    uint ArrayIndex,
    byte Priority,
    BacnetValue Value);
