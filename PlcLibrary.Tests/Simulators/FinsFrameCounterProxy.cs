using System.Net;
using System.Net.Sockets;

namespace PlcLibrary.Tests.Simulators;

/// <summary>
/// 通用 TCP 计数代理：把客户端流量原样转发到上游服务端，同时按 FINS/TCP 帧头统计「数据帧」数量。
/// 用于断言驱动是否把多个点位合并成少量请求（不改变协议内容）。
/// </summary>
internal sealed class FinsFrameCounterProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly int _upstreamPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptTask;
    private int _dataFrameCount;

    public FinsFrameCounterProxy(int upstreamPort)
    {
        _upstreamPort = upstreamPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = AcceptLoopAsync(_cts.Token);
    }

    public int Port { get; }

    /// <summary>已转发的 FINS 数据帧数（command=2），即一次读/写命令。</summary>
    public int DataFrameCount => Volatile.Read(ref _dataFrameCount);

    /// <summary>最近一次数据帧的十六进制内容（调试/断言用）。</summary>
    public string? LastFrameHex { get; private set; }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        { await _acceptTask.ConfigureAwait(false); }
        catch { }
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { return; }

            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var upstream = new TcpClient())
        {
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, _upstreamPort, ct).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            var clientStream = client.GetStream();
            var upstreamStream = upstream.GetStream();
            var buffer = new List<byte>(1024);

            var down = PumpAsync(upstreamStream, clientStream, null, ct);
            var up = PumpAsync(clientStream, upstreamStream, buffer, ct);
            await Task.WhenAny(down, up).ConfigureAwait(false);
            try
            { await Task.WhenAll(down, up).ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to, List<byte>? frameBuffer, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await from.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                    return;

                if (frameBuffer is not null)
                    CountFrames(buffer.AsSpan(0, read), frameBuffer);

                await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch { }
    }

    private void CountFrames(ReadOnlySpan<byte> chunk, List<byte> pending)
    {
        pending.AddRange(chunk.ToArray());

        while (pending.Count >= 8)
        {
            if (pending[0] != 'F' || pending[1] != 'I' || pending[2] != 'N' || pending[3] != 'S')
            {
                pending.RemoveAt(0);
                continue;
            }

            var length = (pending[4] << 24) | (pending[5] << 16) | (pending[6] << 8) | pending[7];
            var frameLength = 8 + length;
            if (length < 4 || frameLength > 65536)
            {
                pending.RemoveAt(0);
                continue;
            }

            if (pending.Count < frameLength)
                return;

            var command = (pending[8] << 24) | (pending[9] << 16) | (pending[10] << 8) | pending[11];
            if (command == 2)
            {
                Interlocked.Increment(ref _dataFrameCount);
                LastFrameHex = Convert.ToHexString(pending.GetRange(0, frameLength).ToArray());
            }

            pending.RemoveRange(0, frameLength);
        }
    }
}
