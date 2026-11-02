using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PlcLibrary.Tests.Simulators;

/// <summary>
/// Minimal offline EtherNet/IP + CIP "Logix tag" target. It implements only the
/// encapsulation commands and CIP services that <c>EthernetIPSharp.Logix.TagClient</c>
/// emits for the Allen-Bradley driver: RegisterSession, SendRRData, SendUnitData,
/// UnregisterSession, Read Tag (0x4C), Write Tag (0x4D), Multiple Service Packet
/// (0x0A), Forward Open (0x54), and Forward Close (0x4E).
/// </summary>
public sealed class EtherNetIpSimServer : IAsyncDisposable
{
    private const ushort CommandRegisterSession = 0x0065;
    private const ushort CommandUnregisterSession = 0x0066;
    private const ushort CommandSendRRData = 0x006F;
    private const ushort CommandSendUnitData = 0x0070;

    private const byte ServiceMultipleServicePacket = 0x0A;
    private const byte ServiceReadTag = 0x4C;
    private const byte ServiceWriteTag = 0x4D;
    private const byte ServiceForwardClose = 0x4E;
    private const byte ServiceForwardOpen = 0x54;

    private const ushort TypeBool = 0x00C1;
    private const ushort TypeDint = 0x00C4;
    private const ushort TypeReal = 0x00CA;
    private const ushort TypeString = 0x00CE;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, SimTag> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _frames = [];
    private readonly List<SimRequest> _requests = [];
    private readonly List<ushort> _receivedCommands = [];
    private Task? _acceptTask;
    private TcpClient? _activeClient;
    private NetworkStream? _activeStream;
    private uint _otConnectionId;
    private uint _toConnectionId;
    private ushort _connectionSerial;
    private ushort _originatorVendorId;
    private uint _originatorSerial;
    private uint _otRpi;
    private uint _toRpi;
    private int _disposed;

    /// <param name="rejectMultiService">
    /// When true, the simulator rejects the top-level Multiple Service Packet with
    /// status 0x08 so tests can exercise the driver's per-point fallback path.
    /// </param>
    public EtherNetIpSimServer(bool rejectMultiService = false)
    {
        RejectMultiService = rejectMultiService;
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public bool RejectMultiService { get; }

    public int Port { get; private set; }

    public string Endpoint => $"127.0.0.1:{Port}";

    public uint SessionHandle { get; private set; }

    /// <summary>为 true 时不再回包（保留 RegisterSession），用于验证驱动的超时/取消行为。</summary>
    public bool PauseResponses { get; set; }

    public IReadOnlyList<string> Frames
    {
        get { lock (_gate) return _frames.ToArray(); }
    }

    public IReadOnlyList<SimRequest> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    public IReadOnlyList<ushort> ReceivedCommands
    {
        get { lock (_gate) return _receivedCommands.ToArray(); }
    }

    public IReadOnlyList<SimTag> Tags
    {
        get { lock (_gate) return _tags.Values.ToArray(); }
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = AcceptLoopAsync(_cts.Token);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void AddInt32(string name, int value)
        => AddTag(new SimTag(name, TypeDint, 4, BitConverter.GetBytes(value), isString: false));

    public void AddSingle(string name, float value)
        => AddTag(new SimTag(name, TypeReal, 4, BitConverter.GetBytes(value), isString: false));

    public void AddBool(string name, bool value)
        => AddTag(new SimTag(name, TypeBool, 1, [value ? (byte)1 : (byte)0], isString: false));

    public void AddString(string name, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length > 82)
            throw new ArgumentException("Logix STRING values are limited to 82 ASCII characters.", nameof(value));

        var data = new byte[88];
        BinaryPrimitives.WriteInt32LittleEndian(data, bytes.Length);
        bytes.CopyTo(data, 4);
        AddTag(new SimTag(name, TypeString, 88, data, isString: true));
    }

    public int GetInt32(string name)
    {
        var tag = GetTag(name);
        if (tag.TypeCode != TypeDint)
            throw new InvalidOperationException($"{name} is not a DINT tag.");
        return BitConverter.ToInt32(tag.Data, 0);
    }

    public float GetSingle(string name)
    {
        var tag = GetTag(name);
        if (tag.TypeCode != TypeReal)
            throw new InvalidOperationException($"{name} is not a REAL tag.");
        return BitConverter.ToSingle(tag.Data, 0);
    }

    public bool GetBool(string name)
    {
        var tag = GetTag(name);
        if (tag.TypeCode != TypeBool)
            throw new InvalidOperationException($"{name} is not a BOOL tag.");
        return tag.Data[0] != 0;
    }

    public string GetString(string name)
    {
        var tag = GetTag(name);
        if (!tag.IsString)
            throw new InvalidOperationException($"{name} is not a STRING tag.");
        var length = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(tag.Data), 0, 82);
        return Encoding.ASCII.GetString(tag.Data, 4, length);
    }

    public bool SawInboundCommand(ushort command)
    {
        lock (_gate) return _receivedCommands.Contains(command);
    }

    public bool SawService(byte service)
    {
        lock (_gate) return _requests.Any(r => r.Service == service);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _cts.CancelAsync().ConfigureAwait(false);
        try { _listener.Stop(); } catch { /* already stopped */ }

        TcpClient? client;
        NetworkStream? stream;
        lock (_gate)
        {
            client = _activeClient;
            stream = _activeStream;
            _activeClient = null;
            _activeStream = null;
        }

        try { stream?.Dispose(); } catch { /* best-effort cleanup */ }
        try { client?.Dispose(); } catch { /* best-effort cleanup */ }

        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); } catch { /* shutdown races are expected */ }
        }

        _cts.Dispose();
    }

    private void AddTag(SimTag tag)
    {
        lock (_gate) _tags[tag.Name] = tag;
    }

    private SimTag GetTag(string name)
    {
        lock (_gate)
        {
            if (!_tags.TryGetValue(name, out var tag))
                throw new InvalidOperationException($"Tag '{name}' does not exist in the simulator.");
            return tag;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            var stream = client.GetStream();
            lock (_gate)
            {
                _activeClient = client;
                _activeStream = stream;
            }

            try
            {
                await HandleClientAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeStream, stream))
                    {
                        _activeClient = null;
                        _activeStream = null;
                    }
                }
                try { stream.Dispose(); } catch { /* best-effort */ }
                try { client.Dispose(); } catch { /* best-effort */ }
            }
        }
    }

    private async Task HandleClientAsync(NetworkStream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
            if (frame is null) return;

            var command = BinaryPrimitives.ReadUInt16LittleEndian(frame);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2));
            var session = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4));
            var context = frame.AsSpan(12, 8).ToArray();
            var data = frame.AsSpan(24, length).ToArray();

            lock (_gate) _receivedCommands.Add(command);

            if (PauseResponses && command != CommandRegisterSession) continue;

            switch (command)
            {
                case CommandRegisterSession:
                    SessionHandle = 0x0000BEEF;
                    await WriteFrameAsync(stream, CommandRegisterSession, SessionHandle, context,
                        [0x01, 0x00, 0x00, 0x00], ct).ConfigureAwait(false);
                    break;

                case CommandSendRRData:
                {
                    var cipRequest = ExtractUnconnectedData(data);
                    var cipReply = cipRequest is null
                        ? BuildCipReply(0, 0x13)
                        : HandleCipRequest(cipRequest, insideMultiService: false);
                    await WriteFrameAsync(stream, CommandSendRRData, session, context,
                        BuildUnconnectedReply(cipReply), ct).ConfigureAwait(false);
                    break;
                }

                case CommandSendUnitData:
                {
                    var (connectionId, sequence, cipRequest) = ExtractConnectedData(data);
                    var cipReply = cipRequest is null
                        ? BuildCipReply(0, 0x13)
                        : HandleCipRequest(cipRequest, insideMultiService: false);
                    await WriteFrameAsync(stream, CommandSendUnitData, session, context,
                        BuildConnectedReply(connectionId, sequence, cipReply), ct).ConfigureAwait(false);
                    break;
                }

                case CommandUnregisterSession:
                    return;

                default:
                    // Unknown encapsulation command: answer with the standard
                    // "unsupported command" status instead of leaving the peer waiting.
                    await WriteFrameAsync(stream, command, session, context, [], ct,
                        status: 0x00000001).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[24];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false)) return null;

        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false)) return null;

        var frame = new byte[24 + length];
        header.CopyTo(frame, 0);
        payload.CopyTo(frame, 24);
        lock (_gate) _frames.Add($"RX {Convert.ToHexString(frame)}");
        return frame;
    }

    private async Task WriteFrameAsync(
        NetworkStream stream,
        ushort command,
        uint session,
        byte[] context,
        byte[] data,
        CancellationToken ct,
        uint status = 0)
    {
        var frame = new byte[24 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, command);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), session);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), status);
        context.CopyTo(frame, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(20), 0);
        data.CopyTo(frame, 24);

        lock (_gate) _frames.Add($"TX {Convert.ToHexString(frame)}");
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    private byte[] HandleCipRequest(byte[] cipRequest, bool insideMultiService)
    {
        if (cipRequest.Length < 2) return BuildCipReply(0, 0x13);

        var service = cipRequest[0];
        var pathWordCount = cipRequest[1];
        var pathByteCount = pathWordCount * 2;
        if (2 + pathByteCount > cipRequest.Length) return BuildCipReply(service, 0x13);

        var path = cipRequest.AsSpan(2, pathByteCount).ToArray();
        var requestData = cipRequest.AsSpan(2 + pathByteCount).ToArray();
        var tagName = ParseTagName(path);
        lock (_gate) _requests.Add(new SimRequest(service, tagName, insideMultiService));

        return service switch
        {
            ServiceReadTag => HandleReadTag(service, tagName, requestData),
            ServiceWriteTag => HandleWriteTag(service, tagName, requestData),
            ServiceMultipleServicePacket => HandleMultipleServicePacket(path, requestData, insideMultiService),
            ServiceForwardOpen => HandleForwardOpen(path, requestData),
            ServiceForwardClose => HandleForwardClose(requestData),
            _ => BuildCipReply(service, 0x08), // service not supported
        };
    }

    private byte[] HandleReadTag(byte service, string? tagName, byte[] requestData)
    {
        if (tagName is null) return BuildCipReply(service, 0x05);

        SimTag? tag;
        lock (_gate) _tags.TryGetValue(tagName, out tag);
        if (tag is null) return BuildCipReply(service, 0x05); // path destination unknown

        var elementCount = requestData.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(requestData)
            : (ushort)1;
        if (elementCount == 0) elementCount = 1;

        var payload = new byte[2 + (tag.ElementSize * elementCount)];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, tag.TypeCode);
        if (tag.IsString)
        {
            // Logix STRING reply data: type 0x00CE, 2-byte structure handle, then the
            // 88-byte STRING structure (DINT length + 82 chars + 2 pad bytes).
            // TagClient.ReadStringAsync expects the length at offset 4 and chars at 8.
            payload = [0xCE, 0x00, 0x00, 0x00, .. tag.Data];
        }
        else
        {
            for (var i = 0; i < elementCount; i++)
                tag.Data.CopyTo(payload, 2 + (i * tag.ElementSize));
        }

        return BuildCipReply(service, 0x00, payload);
    }

    private byte[] HandleWriteTag(byte service, string? tagName, byte[] requestData)
    {
        if (tagName is null) return BuildCipReply(service, 0x05);

        SimTag? tag;
        lock (_gate) _tags.TryGetValue(tagName, out tag);
        if (tag is null) return BuildCipReply(service, 0x05);

        if (requestData.Length < 4) return BuildCipReply(service, 0x13);

        var typeCode = BinaryPrimitives.ReadUInt16LittleEndian(requestData);
        var elementCount = BinaryPrimitives.ReadUInt16LittleEndian(requestData.AsSpan(2));
        if (elementCount == 0 || typeCode != tag.TypeCode) return BuildCipReply(service, 0x07);

        var required = tag.ElementSize * elementCount;
        if (requestData.Length < 4 + required) return BuildCipReply(service, 0x13);

        var newData = requestData.AsSpan(4, required).ToArray();
        lock (_gate) tag.Data = newData;
        return BuildCipReply(service, 0x00);
    }

    private byte[] HandleMultipleServicePacket(byte[] path, byte[] requestData, bool insideMultiService)
    {
        if (RejectMultiService) return BuildCipReply(ServiceMultipleServicePacket, 0x08);

        if (path.Length < 4 || requestData.Length < 2)
            return BuildCipReply(ServiceMultipleServicePacket, 0x13);

        var count = BinaryPrimitives.ReadUInt16LittleEndian(requestData);
        if (count == 0 || requestData.Length < 2 + (count * 2))
            return BuildCipReply(ServiceMultipleServicePacket, 0x13);

        var offsets = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt16LittleEndian(requestData.AsSpan(2 + (i * 2)));
            if (offsets[i] < 2 + (count * 2) || offsets[i] > requestData.Length)
                return BuildCipReply(ServiceMultipleServicePacket, 0x13);
        }

        var replies = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var start = offsets[i];
            var end = i + 1 < count ? offsets[i + 1] : requestData.Length;
            if (end < start || end > requestData.Length)
                return BuildCipReply(ServiceMultipleServicePacket, 0x13);

            replies[i] = HandleCipRequest(requestData[start..end], insideMultiService: true);
        }

        var totalLength = 2 + (count * 2) + replies.Sum(r => r.Length);
        var payload = new byte[totalLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, count);

        var bodyOffset = 2 + (count * 2);
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2 + (i * 2)), (ushort)bodyOffset);
            replies[i].CopyTo(payload, bodyOffset);
            bodyOffset += replies[i].Length;
        }

        return BuildCipReply(ServiceMultipleServicePacket, 0x00, payload);
    }

    private byte[] HandleForwardOpen(byte[] path, byte[] requestData)
    {
        if (path.Length < 4 || path[0] != 0x20 || path[1] != 0x06 || path[2] != 0x24 || path[3] != 0x01)
            return BuildCipReply(ServiceForwardOpen, 0x05);
        if (requestData.Length < 36) return BuildCipReply(ServiceForwardOpen, 0x13);

        var requestedToConnectionId = BinaryPrimitives.ReadUInt32LittleEndian(requestData.AsSpan(6));
        _connectionSerial = BinaryPrimitives.ReadUInt16LittleEndian(requestData.AsSpan(10));
        _originatorVendorId = BinaryPrimitives.ReadUInt16LittleEndian(requestData.AsSpan(12));
        _originatorSerial = BinaryPrimitives.ReadUInt32LittleEndian(requestData.AsSpan(14));
        _otRpi = BinaryPrimitives.ReadUInt32LittleEndian(requestData.AsSpan(22));
        _toRpi = BinaryPrimitives.ReadUInt32LittleEndian(requestData.AsSpan(28));
        _otConnectionId = 0x00000001;
        _toConnectionId = requestedToConnectionId;

        var payload = new byte[26];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, _otConnectionId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), _toConnectionId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), _connectionSerial);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), _originatorVendorId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), _originatorSerial);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), _otRpi);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), _toRpi);
        payload[24] = 0; // application reply size
        payload[25] = 0; // reserved
        return BuildCipReply(ServiceForwardOpen, 0x00, payload);
    }

    private byte[] HandleForwardClose(byte[] requestData)
    {
        if (requestData.Length < 8) return BuildCipReply(ServiceForwardClose, 0x13);

        var payload = new byte[10];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, _connectionSerial);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), _originatorVendorId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), _originatorSerial);
        payload[8] = 0;
        payload[9] = 0;
        return BuildCipReply(ServiceForwardClose, 0x00, payload);
    }

    private static string? ParseTagName(byte[] path)
    {
        var name = new StringBuilder();
        var i = 0;
        while (i < path.Length)
        {
            switch (path[i])
            {
                case 0x91: // ANSI extended symbolic segment
                {
                    if (i + 1 >= path.Length) return null;
                    var length = path[i + 1];
                    if (i + 2 + length > path.Length) return null;
                    if (name.Length > 0) name.Append('.');
                    name.Append(Encoding.ASCII.GetString(path, i + 2, length));
                    i += 2 + length;
                    if ((length & 1) != 0) i++; // pad to a 16-bit boundary
                    break;
                }
                case 0x28: // 8-bit element index
                    i += 2;
                    break;
                case 0x29: // 16-bit element index (pad + value)
                    i += 4;
                    break;
                case 0x2A: // 32-bit element index (pad + value)
                    i += 6;
                    break;
                default:
                    return name.Length > 0 ? name.ToString() : null;
            }
        }

        return name.Length > 0 ? name.ToString() : null;
    }

    private static byte[]? ExtractUnconnectedData(byte[] data)
    {
        if (data.Length < 8) return null;

        var itemCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        var offset = 8;
        for (var i = 0; i < itemCount; i++)
        {
            if (offset + 4 > data.Length) return null;
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
            offset += 4;
            if (offset + length > data.Length) return null;
            if (type == 0x00B2) return data.AsSpan(offset, length).ToArray(); // unconnected data
            offset += length;
        }

        return null;
    }

    private static (uint ConnectionId, ushort Sequence, byte[]? Cip) ExtractConnectedData(byte[] data)
    {
        if (data.Length < 8) return (0, 0, null);

        var itemCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        var offset = 8;
        uint connectionId = 0;
        ushort sequence = 0;
        byte[]? cip = null;

        for (var i = 0; i < itemCount; i++)
        {
            if (offset + 4 > data.Length) return (connectionId, sequence, null);
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2));
            offset += 4;
            if (offset + length > data.Length) return (connectionId, sequence, null);

            if (type == 0x00A1 && length >= 4) // connected address
                connectionId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            else if (type == 0x00B1 && length >= 2) // connected data
            {
                sequence = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
                cip = data.AsSpan(offset + 2, length - 2).ToArray();
            }

            offset += length;
        }

        return (connectionId, sequence, cip);
    }

    private static byte[] BuildUnconnectedReply(byte[] cipReply)
    {
        var payload = new byte[16 + cipReply.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 0x00B2);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14), (ushort)cipReply.Length);
        cipReply.CopyTo(payload, 16);
        return payload;
    }

    private static byte[] BuildConnectedReply(uint connectionId, ushort sequence, byte[] cipReply)
    {
        var payload = new byte[22 + cipReply.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), 0x00A1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), connectionId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), 0x00B1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), (ushort)(cipReply.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(20), sequence);
        cipReply.CopyTo(payload, 22);
        return payload;
    }

    private static byte[] BuildCipReply(byte service, byte status, byte[]? data = null)
    {
        var payload = data ?? [];
        var reply = new byte[4 + payload.Length];
        reply[0] = (byte)(service | 0x80);
        reply[1] = 0;
        reply[2] = status;
        reply[3] = 0;
        payload.CopyTo(reply, 4);
        return reply;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }
}

/// <summary>A CIP request observed by the simulator, used by tests to assert the wire path.</summary>
public sealed record SimRequest(byte Service, string? TagName, bool InsideMultiService);

/// <summary>In-memory tag held by <see cref="EtherNetIpSimServer"/>.</summary>
public sealed class SimTag
{
    internal SimTag(string name, ushort typeCode, int elementSize, byte[] data, bool isString)
    {
        Name = name;
        TypeCode = typeCode;
        ElementSize = elementSize;
        Data = data;
        IsString = isString;
    }

    public string Name { get; }
    public ushort TypeCode { get; }
    public int ElementSize { get; }
    public byte[] Data { get; internal set; }
    public bool IsString { get; }
}
