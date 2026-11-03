using PlcLibrary.DriverDomain;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.Tests.DriverDomain;

/// <summary>
/// 基类统一语义：重连失败返回 false + Faulted，取消向上抛，Dispose 模板调用 Disconnect。
/// </summary>
public class ProtocolDriverBaseTests
{
    [Fact]
    public async Task TryReconnectAsync_ConnectFails_ReturnsFalseAndFaults()
    {
        var driver = new FakeDriver { FailConnect = true };

        var result = await driver.TryReconnectAsync();

        Assert.False(result);
        Assert.Equal(DriverStatus.Faulted, driver.DriverStatus);
        Assert.True(driver.ReconnectFailed);
    }

    [Fact]
    public async Task TryReconnectAsync_CancellationPropagates()
    {
        var driver = new FakeDriver { CancelConnect = true };

        await Assert.ThrowsAsync<OperationCanceledException>(() => driver.TryReconnectAsync());
    }

    [Fact]
    public async Task DisposeAsync_UsesDisconnectTemplate()
    {
        var driver = new FakeDriver();

        await driver.DisposeAsync();

        Assert.True(driver.Disconnected);
    }

    private sealed class FakeDriver : ProtocolDriverBase<object>
    {
        public bool FailConnect { get; init; }
        public bool CancelConnect { get; init; }
        public bool ReconnectFailed { get; private set; }
        public bool Disconnected { get; private set; }

        public override Task ConnectAsync(CancellationToken ct = default)
        {
            if (CancelConnect) throw new OperationCanceledException(ct);
            if (FailConnect) throw new InvalidOperationException("connect failed");
            SetState(new object(), DriverStatus.Connected);
            return Task.CompletedTask;
        }

        public override Task DisconnectAsync(CancellationToken ct = default)
        {
            DetachClient();
            Disconnected = true;
            return Task.CompletedTask;
        }

        public override Task<DriverResult[]> ReadAsync(
            TagPointConfiguration[] points, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<DriverResult>());

        public override Task<DriverResult[]> WriteAsync(
            IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<DriverResult>());

        protected override void OnReconnectFailed(Exception ex) => ReconnectFailed = true;
    }
}
