using System.Net;
using System.Net.Sockets;

namespace PlcLibrary.Tests.Simulators;

/// <summary>
/// 最小化的 MELSEC MC 协议（3E 二进制帧，TCP）从站模拟器，仅用于离线验证驱动。
/// 支持批量读（0x0401，字/位）与批量写（0x1401，字/位），其余命令返回非零结束代码。
/// </summary>
internal sealed class McProtocolSimServer : IAsyncDisposable
{
    private const byte DeviceD = 0xA8;
    private const byte DeviceM = 0x90;

    private const ushort CommandBatchRead = 0x0401;
    private const ushort CommandBatchWrite = 0x1401;
    private const ushort SubCommandWord = 0x0000;
    private const ushort SubCommandBit = 0x0001;

    private const ushort EndCodeSuccess = 0x0000;
    private const ushort EndCodeUnsupportedCommand = 0xC059;
    private const ushort EndCodeDeviceRangeError = 0xC05B;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly object _sync = new();
    private readonly List<Task> _connections = [];
    private readonly List<string> _requestFrames = [];
    private readonly ushort[] _words;
    private readonly bool[] _bits;

    private int _readRequests;
    private int _writeRequests;
    private bool _omitLastResponsePoint;

    public McProtocolSimServer(int wordCount = 4096, int bitCount = 4096)
    {
        _words = new ushort[wordCount];
        _bits = new bool[bitCount];
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    /// <summary>收到的原始请求帧（十六进制），按接收顺序。</summary>
    public IReadOnlyList<string> RequestFrames
    {
        get { lock (_sync) return _requestFrames.ToArray(); }
    }

    public int RequestCount
    {
        get { lock (_sync) return _requestFrames.Count; }
    }

    public int ReadRequestCount
    {
        get { lock (_sync) return _readRequests; }
    }

    public int WriteRequestCount
    {
        get { lock (_sync) return _writeRequests; }
    }

    /// <summary>响应中丢弃最后一个点位的数据（用于构造「响应数据不足」场景）。</summary>
    public void OmitLastResponsePointOnce() => _omitLastResponsePoint = true;

    public ushort GetWord(int address)
    {
        lock (_sync) return _words[address];
    }

    public void SetWord(int address, ushort value)
    {
        lock (_sync) _words[address] = value;
    }

    public bool GetBit(int address)
    {
        lock (_sync) return _bits[address];
    }

    public void SetBit(int address, bool value)
    {
        lock (_sync) _bits[address] = value;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        Task[] pending;
        lock (_sync) pending = _connections.ToArray();
        foreach (var task in pending)
        {
            try { await task.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }

            var task = HandleClientAsync(client);
            lock (_sync) _connections.Add(task);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var pending = new List<byte>();

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (read == 0) return;

                    for (var i = 0; i < read; i++) pending.Add(buffer[i]);

                    while (pending.Count >= 9)
                    {
                        var frameLength = pending[7] | (pending[8] << 8);
                        if (frameLength < 2 || frameLength > 4096)
                        {
                            return;
                        }
                        var total = 9 + frameLength;
                        if (pending.Count < total) break;

                        var frame = pending.GetRange(0, total).ToArray();
                        pending.RemoveRange(0, total);

                        // MC 读请求最短 21 字节（含 12 字节数据部），过短视为非法帧并断开。
                        if (frame.Length < 21) return;

                        var response = Process(frame);
                        await stream.WriteAsync(response, _cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }
    }

    private byte[] Process(byte[] frame)
    {
        lock (_sync) _requestFrames.Add(Convert.ToHexString(frame));

        var command = (ushort)(frame[11] | (frame[12] << 8));
        var subCommand = (ushort)(frame[13] | (frame[14] << 8));
        var head = frame[15] | (frame[16] << 8) | (frame[17] << 16);
        var device = frame[18];
        var count = frame[19] | (frame[20] << 8);

        if (command == CommandBatchRead && (subCommand == SubCommandWord || subCommand == SubCommandBit))
        {
            lock (_sync) _readRequests++;
            return HandleRead(frame, subCommand, head, device, count);
        }

        if (command == CommandBatchWrite && (subCommand == SubCommandWord || subCommand == SubCommandBit))
        {
            lock (_sync) _writeRequests++;
            return HandleWrite(frame, subCommand, head, device, count);
        }

        return BuildResponse(frame, EndCodeUnsupportedCommand, []);
    }

    private byte[] HandleRead(byte[] frame, ushort subCommand, int head, byte device, int count)
    {
        if (count <= 0)
        {
            return BuildResponse(frame, EndCodeDeviceRangeError, []);
        }

        if (subCommand == SubCommandWord)
        {
            if (device != DeviceD || head < 0 || head + count > _words.Length)
            {
                return BuildResponse(frame, EndCodeDeviceRangeError, []);
            }

            var payload = new byte[count * 2];
            lock (_sync)
            {
                for (var i = 0; i < count; i++)
                {
                    var value = _words[head + i];
                    payload[i * 2] = (byte)value;
                    payload[(i * 2) + 1] = (byte)(value >> 8);
                }
            }
            return BuildResponse(frame, EndCodeSuccess, TrimLastPoint(payload, 2));
        }

        // 位读取：M 为位软元件；D 以字为单位做位访问（head = word * 16 + bit）。
        var bits = new bool[count];
        lock (_sync)
        {
            if (device == DeviceM)
            {
                if (head < 0 || head + count > _bits.Length)
                    return BuildResponse(frame, EndCodeDeviceRangeError, []);
                for (var i = 0; i < count; i++) bits[i] = _bits[head + i];
            }
            else if (device == DeviceD)
            {
                for (var i = 0; i < count; i++)
                {
                    var absolute = head + i;
                    var wordIndex = absolute / 16;
                    if (wordIndex < 0 || wordIndex >= _words.Length)
                        return BuildResponse(frame, EndCodeDeviceRangeError, []);
                    bits[i] = ((_words[wordIndex] >> (absolute % 16)) & 1) == 1;
                }
            }
            else
            {
                return BuildResponse(frame, EndCodeDeviceRangeError, []);
            }
        }

        return BuildResponse(frame, EndCodeSuccess, TrimLastPoint(PackBits(bits), 1));
    }

    private byte[] HandleWrite(byte[] frame, ushort subCommand, int head, byte device, int count)
    {
        if (count <= 0)
        {
            return BuildResponse(frame, EndCodeDeviceRangeError, []);
        }

        if (subCommand == SubCommandWord)
        {
            if (device != DeviceD || head < 0 || head + count > _words.Length)
            {
                return BuildResponse(frame, EndCodeDeviceRangeError, []);
            }

            lock (_sync)
            {
                for (var i = 0; i < count; i++)
                {
                    var offset = 21 + (i * 2);
                    if (offset + 1 >= frame.Length)
                        return BuildResponse(frame, EndCodeDeviceRangeError, []);
                    _words[head + i] = (ushort)(frame[offset] | (frame[offset + 1] << 8));
                }
            }
            return BuildResponse(frame, EndCodeSuccess, []);
        }

        var requested = count;
        lock (_sync)
        {
            if (device == DeviceM)
            {
                if (head < 0 || head + requested > _bits.Length)
                    return BuildResponse(frame, EndCodeDeviceRangeError, []);
                var values = UnpackBits(frame, 21, requested);
                for (var i = 0; i < requested; i++) _bits[head + i] = values[i];
            }
            else if (device == DeviceD)
            {
                for (var i = 0; i < requested; i++)
                {
                    var absolute = head + i;
                    var wordIndex = absolute / 16;
                    if (wordIndex < 0 || wordIndex >= _words.Length)
                        return BuildResponse(frame, EndCodeDeviceRangeError, []);
                }
                var values = UnpackBits(frame, 21, requested);
                for (var i = 0; i < requested; i++)
                {
                    var absolute = head + i;
                    var wordIndex = absolute / 16;
                    var bit = absolute % 16;
                    if (values[i]) _words[wordIndex] |= (ushort)(1 << bit);
                    else _words[wordIndex] &= (ushort)~(1 << bit);
                }
            }
            else
            {
                return BuildResponse(frame, EndCodeDeviceRangeError, []);
            }
        }

        return BuildResponse(frame, EndCodeSuccess, []);
    }

    private byte[] TrimLastPoint(byte[] payload, int bytesPerPoint)
    {
        bool omit;
        lock (_sync)
        {
            omit = _omitLastResponsePoint;
            _omitLastResponsePoint = false;
        }
        return omit && payload.Length >= bytesPerPoint
            ? payload[..^bytesPerPoint]
            : payload;
    }

    /// <summary>MC 3E 二进制位数据每字节 2 点：先出现的点位于高 4 位（0x10 表示首点为 ON）。</summary>
    private static byte[] PackBits(bool[] bits)
    {
        var payload = new byte[(bits.Length + 1) / 2];
        for (var i = 0; i < bits.Length; i++)
        {
            var nibble = bits[i] ? 0x01 : 0x00;
            payload[i / 2] |= (byte)(i % 2 == 0 ? nibble << 4 : nibble);
        }
        return payload;
    }

    private static bool[] UnpackBits(byte[] frame, int offset, int count)
    {
        var values = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var index = offset + (i / 2);
            if (index >= frame.Length) return values;
            var nibble = i % 2 == 0 ? (frame[index] >> 4) & 0x0F : frame[index] & 0x0F;
            values[i] = nibble != 0;
        }
        return values;
    }

    private static byte[] BuildResponse(byte[] request, ushort endCode, byte[] payload)
    {
        var response = new byte[11 + payload.Length];
        response[0] = 0xD0;
        response[1] = 0x00;
        response[2] = request[2]; // 网络号
        response[3] = request[3]; // PC 号
        response[4] = request[4]; // 模块 IO 号（低）
        response[5] = request[5]; // 模块 IO 号（高）
        response[6] = request[6]; // 站号
        var length = 2 + payload.Length;
        response[7] = (byte)length;
        response[8] = (byte)(length >> 8);
        response[9] = (byte)endCode;
        response[10] = (byte)(endCode >> 8);
        Array.Copy(payload, 0, response, 11, payload.Length);
        return response;
    }
}
