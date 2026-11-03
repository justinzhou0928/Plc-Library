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
/// 拓扑说明（Windows/Linux 通用）：
/// 模拟器与驱动都使用 SDK 的 shared + exclusive UDP 端口模式：shared 套接字绑定
/// 0.0.0.0:Port 收 WhoIs/广播，exclusive 套接字绑定各自回环地址（模拟器 127.0.0.2、
/// 驱动 localendpointip=127.0.0.1）收单播。共享端口依赖 SO_REUSEADDR，因此不要用
/// useExclusivePort:true（它在 Linux 上会设置 ExclusiveAddressUse，与共享端口冲突）。
/// WhoIs 由 SDK 在 shared socket 上触发 OnWhoIs，模拟器从 exclusive socket 回 IAm。
/// </remarks>
public sealed class BacnetSimServer : IAsyncDisposable
{
    /// <summary>模拟器监听的回环别名地址。</summary>
    public const string LoopbackAddress = "127.0.0.2";

    private const int DiscoveryTimeoutMs = 1000;

    private readonly object _gate = new();
    private readonly Dictionary<(BacnetObjectTypes Type, uint Instance), SimObject> _objects;
    private readonly BacnetClient _client;
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
            var port = GetFreeUdpPort();
            BacnetClient? client = null;
            try
            {
                // 使用 SDK 的 shared + exclusive 端口模式（useExclusivePort:false）：
                // 共享 socket 收广播/WhoIs，独占 socket 收 127.0.0.2:port 的单播。
                // 不要用 useExclusivePort:true —— 它在 Linux 上会设置 ExclusiveAddressUse，
                // 与驱动/其它测试进程的 0.0.0.0:port 共享 socket 冲突（Address already in use）。
                var transport = new BacnetIpUdpProtocolTransport(
                    port, port, localEndpointIp: LoopbackAddress);
                client = new BacnetClient(transport) { Timeout = DiscoveryTimeoutMs };
                client.OnWhoIs += HandleWhoIs;
                client.OnReadPropertyMultipleRequest += HandleReadPropertyMultiple;
                client.OnReadPropertyRequest += HandleReadProperty;
                client.OnWritePropertyRequest += HandleWriteProperty;
                client.Start();

                _client = client;
                Port = port;
                return;
            }
            catch (Exception ex)
            {
                client?.Dispose();
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

    /// <summary>为 true 时不再回包，用于验证驱动的读写超时。</summary>
    public bool PauseResponses { get; set; }

    /// <summary>是否对不存在的对象返回逐对象错误。</summary>
    public bool PerObjectErrors { get; }

    /// <summary>是否在 RMP-ACK 中省略不存在的对象。</summary>
    public bool OmitUnknownObjects { get; }

    /// <summary>
    /// 生成驱动连接串。port 同时用于本地共享/独占绑定与远程目标端口（host 不含端口时
    /// 驱动用 port 拼目标地址），localendpointip 让驱动独占套接字绑定 127.0.0.1。
    /// </summary>
    public string CreateConnectionString(uint deviceInstance = 0)
    {
        var builder = new StringBuilder();
        // host 只写 IP，端口由 port 字段表达：驱动会把它编码进 BacnetAddress 地址。
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

    private static int GetFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private void HandleWhoIs(BacnetClient sender, BacnetAddress adr, int lowLimit, int highLimit)
    {
        Interlocked.Increment(ref _whoIsRequestCount);

        if (lowLimit >= 0 && DeviceInstance < (uint)lowLimit)
            return;
        if (highLimit >= 0 && DeviceInstance > (uint)highLimit)
            return;

        try
        {
            sender.Iam(DeviceInstance, BacnetSegmentations.SEGMENTATION_NONE, adr);
        }
        catch
        {
            // IAm 发送失败只影响发现测试；让测试自己超时失败。
        }
    }

    private void HandleReadPropertyMultiple(BacnetClient sender, BacnetAddress adr, byte invokeId,
        IList<BacnetReadAccessSpecification> specs, BacnetMaxSegments maxSegments)
    {
        if (PauseResponses)
            return;

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
        if (PauseResponses)
            return;

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
        if (PauseResponses)
            return;

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

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return default;

        _client.Dispose();
        return default;
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
