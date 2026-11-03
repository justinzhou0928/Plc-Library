using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PlcLibrary.Tests.Simulators;

/// <summary>
/// 最小 S7comm 从站模拟器（ISO-on-TCP + COTP + S7 PDU），用于离线验证 S7Driver 的真实报文路径。
/// 支持 Setup Communication、Read Var（0x04）、Write Var（0x05），数据区为按 DB 号映射的字节数组。
/// 仅实现驱动实际用到的报文；未知功能码直接断开连接，避免客户端长时间挂起。
/// </summary>
internal sealed class S7SimServer : IAsyncDisposable
{
    private const byte AreaDataBlock = 0x84;
    private const int MaxTableBytes = 8192;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptTask;
    private readonly ConcurrentDictionary<int, byte[]> _db = new();
    private readonly ConcurrentQueue<string> _frames = new();
    private readonly object _gate = new();
    private int _readRequests;
    private int _writeRequests;
    private int _connections;
    private int _dropPendingReads;
    private int _disposed;

    public S7SimServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = AcceptLoopAsync(_cts.Token);
    }

    public int Port { get; }

    public int ReadRequestCount => Volatile.Read(ref _readRequests);

    public int WriteRequestCount => Volatile.Read(ref _writeRequests);

    public int ConnectionCount => Volatile.Read(ref _connections);

    public IReadOnlyList<string> CapturedFrames => _frames.ToArray();

    /// <summary>最后一次读请求的 DB 号与起始字节，用于断言批量读取合并行为。</summary>
    public (int Db, int StartByte, int ByteCount)? LastRead { get; private set; }

    /// <summary>在内存中构造 S7 STRING（2 字节头：最大长度、实际长度 + ASCII 数据）。</summary>
    public void SetString(int db, int startByte, int capacity, string value)
    {
        if (value.Length > capacity)
            throw new ArgumentOutOfRangeException(nameof(value));
        var bytes = new byte[capacity + 2];
        bytes[0] = (byte)capacity;
        bytes[1] = (byte)value.Length;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7F)
                throw new ArgumentException("模拟器仅支持 ASCII 字符串", nameof(value));
            bytes[2 + i] = (byte)value[i];
        }

        WriteMemory(db, startByte, bytes);
    }

    public void WriteInt16(int db, int startByte, short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        WriteMemory(db, startByte, bytes);
    }

    public void WriteInt32(int db, int startByte, int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        WriteMemory(db, startByte, bytes);
    }

    public void WriteReal(int db, int startByte, float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(bytes, value);
        WriteMemory(db, startByte, bytes);
    }

    public void WriteBit(int db, int byteOffset, int bit, bool value)
    {
        lock (_gate)
        {
            var memory = GetMemory(db);
            if (value)
                memory[byteOffset] |= (byte)(1 << bit);
            else
                memory[byteOffset] &= (byte)~(1 << bit);
        }
    }

    public byte[] ReadMemory(int db, int startByte, int length)
    {
        lock (_gate)
        {
            var memory = GetMemory(db);
            var copy = new byte[length];
            Array.Copy(memory, startByte, copy, 0, length);
            return copy;
        }
    }

    public short ReadInt16(int db, int startByte)
        => BinaryPrimitives.ReadInt16BigEndian(ReadMemory(db, startByte, 2));

    public float ReadReal(int db, int startByte)
        => BinaryPrimitives.ReadSingleBigEndian(ReadMemory(db, startByte, 4));

    public string ReadString(int db, int startByte)
    {
        var header = ReadMemory(db, startByte, 2);
        return System.Text.Encoding.ASCII.GetString(ReadMemory(db, startByte + 2, header[1]));
    }

    /// <summary>让接下来的 N 次读请求在收到后立即关闭连接（模拟链路中断）。</summary>
    public void DropNextReads(int count) => Interlocked.Exchange(ref _dropPendingReads, count);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        _listener.Stop();
        try
        { await _acceptTask.ConfigureAwait(false); }
        catch { }
        _cts.Dispose();
    }

    private void WriteMemory(int db, int startByte, byte[] bytes)
    {
        lock (_gate)
        {
            var memory = GetMemory(db);
            if (startByte + bytes.Length > memory.Length)
                throw new ArgumentOutOfRangeException(nameof(startByte), "写入超出模拟 DB 容量");
            Array.Copy(bytes, 0, memory, startByte, bytes.Length);
        }
    }

    private byte[] GetMemory(int db)
        => _db.GetOrAdd(db, _ => new byte[MaxTableBytes]);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception) { return; }

            Interlocked.Increment(ref _connections);
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var frame = await ReadTpktAsync(stream, ct).ConfigureAwait(false);
                    if (frame is null)
                        return;
                    _frames.Enqueue(Convert.ToHexString(frame));

                    // TPKT(4) + COTP：frame[4]=COTP 长度，frame[5]=0xE0 连接请求 / 0xF0 数据传输
                    if (frame.Length >= 6 && frame[5] == 0xE0)
                    {
                        await stream.WriteAsync(BuildConnectionConfirm(frame), ct).ConfigureAwait(false);
                        continue;
                    }

                    if (frame.Length < 7 || frame[5] != 0xF0)
                        continue;

                    var request = frame[7..];
                    // 读请求被要求丢弃时直接关闭连接，模拟链路中断
                    if (request.Length > 10 && request[10] == 0x04
                        && Interlocked.Decrement(ref _dropPendingReads) >= 0)
                    {
                        return;
                    }

                    var response = HandleS7Pdu(request);
                    if (response is null)
                        return;
                    await stream.WriteAsync(BuildTpkt(response), ct).ConfigureAwait(false);
                }
            }
            catch (Exception) { }
        }
    }

    private byte[]? HandleS7Pdu(byte[] pdu)
    {
        if (pdu.Length < 10 || pdu[0] != 0x32)
            return null;
        var pduRef = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(4, 2));
        var paramLength = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6, 2));
        var dataLength = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(8, 2));
        if (10 + paramLength + dataLength > pdu.Length)
            return null;

        var parameters = pdu.AsSpan(10, paramLength).ToArray();
        var data = pdu.AsSpan(10 + paramLength, dataLength).ToArray();
        if (parameters.Length == 0)
            return null;

        return parameters[0] switch
        {
            0xF0 => BuildSetupCommunication(pduRef),
            0x04 => BuildReadVarResponse(pduRef, parameters),
            0x05 => BuildWriteVarResponse(pduRef, parameters, data),
            _ => null,
        };
    }

    private static byte[] BuildConnectionConfirm(byte[] cr)
    {
        // CC 回显客户端 src-ref 作为 dst-ref，并协商 TPDU size = 1024
        var cc = new byte[22];
        cc[0] = 0x03;
        cc[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(cc.AsSpan(2), 22);
        cc[4] = 0x11;
        cc[5] = 0xD0;
        cc[6] = cr[8];
        cc[7] = cr[9];
        cc[8] = 0x00;
        cc[9] = 0x01;
        cc[10] = 0x00;
        cc[11] = 0xC0;
        cc[12] = 0x01;
        cc[13] = 0x0A;
        cc[14] = 0xC1;
        cc[15] = 0x02;
        cc[16] = 0x01;
        cc[17] = 0x00;
        cc[18] = 0xC2;
        cc[19] = 0x02;
        cc[20] = 0x01;
        cc[21] = 0x02;
        return cc;
    }

    private static byte[] BuildTpkt(byte[] s7)
    {
        var frame = new byte[s7.Length + 7];
        frame[0] = 0x03;
        frame[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)frame.Length);
        frame[4] = 0x02;
        frame[5] = 0xF0;
        frame[6] = 0x80;
        s7.CopyTo(frame, 7);
        return frame;
    }

    private static byte[] BuildSetupCommunication(ushort pduRef)
    {
        var parameters = new byte[] { 0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0xE0 };
        return BuildAckData(pduRef, parameters, []);
    }

    private static byte[] BuildAckData(ushort pduRef, byte[] parameters, byte[] data)
    {
        var frame = new byte[12 + parameters.Length + data.Length];
        frame[0] = 0x32;
        frame[1] = 0x03;
        frame[2] = 0x00;
        frame[3] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), pduRef);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6, 2), (ushort)parameters.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8, 2), (ushort)data.Length);
        frame[10] = 0x00;
        frame[11] = 0x00;
        parameters.CopyTo(frame, 12);
        data.CopyTo(frame, 12 + parameters.Length);
        return frame;
    }

    private byte[] BuildReadVarResponse(ushort pduRef, byte[] parameters)
    {
        var itemCount = parameters.Length >= 2 ? parameters[1] : 0;
        var data = new List<byte>(itemCount * 8);
        var responseParameters = new byte[2];
        responseParameters[0] = 0x04;
        responseParameters[1] = (byte)itemCount;

        Interlocked.Increment(ref _readRequests);

        for (var i = 0; i < itemCount; i++)
        {
            var offset = 2 + i * 12;
            if (offset + 12 > parameters.Length)
            {
                data.AddRange(BuildReadItemResult(0x05, 0x00, ReadOnlySpan<byte>.Empty, 0));
                continue;
            }

            var transportSize = parameters[offset + 3];
            var count = BinaryPrimitives.ReadUInt16BigEndian(parameters.AsSpan(offset + 4, 2));
            var db = BinaryPrimitives.ReadUInt16BigEndian(parameters.AsSpan(offset + 6, 2));
            var area = parameters[offset + 8];
            var address = (parameters[offset + 9] << 16) | (parameters[offset + 10] << 8) | parameters[offset + 11];

            if (area != AreaDataBlock)
            {
                data.AddRange(BuildReadItemResult(0x0A, transportSize, ReadOnlySpan<byte>.Empty, 0));
                continue;
            }

            var isBit = transportSize == 0x01;
            var byteOffset = isBit ? address >> 3 : address >> 3;
            var elementBits = transportSize switch
            {
                0x01 => 1,
                0x02 or 0x03 => 8,
                0x04 or 0x05 or 0x09 => 16,
                0x06 or 0x07 or 0x08 or 0x0A => 32,
                0x0B => 64,
                _ => 8,
            };
            var requestedBits = count * elementBits;
            var byteCount = (requestedBits + 7) / 8;

            if (byteOffset < 0 || byteOffset + byteCount > MaxTableBytes)
            {
                data.AddRange(BuildReadItemResult(0x05, transportSize, ReadOnlySpan<byte>.Empty, 0));
                continue;
            }

            byte[] payload;
            lock (_gate)
            {
                var memory = GetMemory(db);
                payload = new byte[byteCount];
                if (isBit)
                {
                    for (var b = 0; b < count; b++)
                    {
                        var bitAddress = address + b;
                        var bitValue = (memory[bitAddress >> 3] >> (bitAddress & 7)) & 1;
                        if (bitValue != 0)
                            payload[b >> 3] |= (byte)(1 << (b & 7));
                    }
                }
                else
                {
                    Array.Copy(memory, byteOffset, payload, 0, byteCount);
                    LastRead = (db, byteOffset, byteCount);
                }
            }

            data.AddRange(BuildReadItemResult(0xFF, transportSize, payload, requestedBits));
        }

        return BuildAckData(pduRef, responseParameters, data.ToArray());
    }

    private static byte[] BuildReadItemResult(byte returnCode, byte transportSize, ReadOnlySpan<byte> payload, int lengthInBits)
    {
        var padded = payload.Length % 2 == 0 ? payload.Length : payload.Length + 1;
        var item = new byte[4 + padded];
        item[0] = returnCode;
        item[1] = returnCode == 0xFF ? transportSize : (byte)0x00;
        BinaryPrimitives.WriteUInt16BigEndian(item.AsSpan(2, 2), (ushort)lengthInBits);
        payload.CopyTo(item.AsSpan(4));
        return item;
    }

    private byte[] BuildWriteVarResponse(ushort pduRef, byte[] parameters, byte[] data)
    {
        var itemCount = parameters.Length >= 2 ? parameters[1] : 0;
        var cursor = 0;
        var results = new byte[itemCount * 4];
        var memoryWrites = new List<(int Db, int ByteOffset, byte[] Bytes, int? Bit, bool BitValue)>();

        for (var i = 0; i < itemCount; i++)
        {
            var offset = 2 + i * 12;
            if (offset + 12 > parameters.Length || cursor + 4 > data.Length)
                break;

            var transportSize = parameters[offset + 3];
            var count = BinaryPrimitives.ReadUInt16BigEndian(parameters.AsSpan(offset + 4, 2));
            var db = BinaryPrimitives.ReadUInt16BigEndian(parameters.AsSpan(offset + 6, 2));
            var area = parameters[offset + 8];
            var address = (parameters[offset + 9] << 16) | (parameters[offset + 10] << 8) | parameters[offset + 11];

            var lengthInBits = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(cursor + 2, 2));
            var payloadLength = (lengthInBits + 7) / 8;
            var payload = data.AsSpan(cursor + 4, Math.Min(payloadLength, data.Length - cursor - 4)).ToArray();
            cursor += 4 + (payloadLength % 2 == 0 ? payloadLength : payloadLength + 1);

            results[i * 4] = 0xFF;
            if (area != AreaDataBlock)
            { results[i * 4] = 0x0A; continue; }

            if (transportSize == 0x01)
            {
                var bitAddress = address;
                var bitValue = payload.Length > 0 && (payload[0] & 1) != 0;
                memoryWrites.Add((db, 0, [], bitAddress, bitValue));
            }
            else
            {
                memoryWrites.Add((db, address >> 3, payload, null, false));
            }
        }

        lock (_gate)
        {
            foreach (var write in memoryWrites)
            {
                var memory = GetMemory(write.Db);
                if (write.Bit is { } bitAddress)
                {
                    var mask = (byte)(1 << (bitAddress & 7));
                    if (write.BitValue)
                        memory[bitAddress >> 3] |= mask;
                    else
                        memory[bitAddress >> 3] &= (byte)~mask;
                }
                else if (write.ByteOffset + write.Bytes.Length <= memory.Length)
                {
                    Array.Copy(write.Bytes, 0, memory, write.ByteOffset, write.Bytes.Length);
                }
            }
        }

        Interlocked.Increment(ref _writeRequests);
        var responseParameters = new byte[] { 0x05, (byte)itemCount };
        return BuildAckData(pduRef, responseParameters, results);
    }

    private static async Task<byte[]?> ReadTpktAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return null;
        if (header[0] != 0x03)
            return null;
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        if (length < 7)
            return null;
        var body = new byte[length - 4];
        if (!await ReadExactAsync(stream, body, ct).ConfigureAwait(false))
            return null;
        var frame = new byte[length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, 4);
        return frame;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }
}
