using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NModbus;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General.Configuration;
using PlcLibrary.Modbus;
using PlcLibrary.Monitor.Extensions;
using PlcLibrary.Monitor.Interfaces;
using System.Net;
using System.Net.Sockets;

namespace PlcLibrary.Tests.Monitor;

/// <summary>
/// 真实协议回环测试：在本机起 Modbus TCP 从站（真实协议），
/// 用 ModbusTcpDriver 连接，验证监控缓存端到端只推「变化」。
/// </summary>
public class ModbusLoopbackIntegrationTests
{
    private static IHost BuildHost() =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddPlcLibrary();
                services.AddDriver<ModbusTcpDriver>();
                services.AddPlcMonitor();
            })
            .Build();

    private static DeviceConfiguration CreateDevice(string id, int port, params (string TagId, string Address)[] points) => new()
    {
        Id = id,
        Protocol = "Modbus_TCP",
        ConnectionString = $"host:127.0.0.1;port:{port};slaveid:1",
        CollectionInterval = TimeSpan.FromMilliseconds(100),
        TagPoints = points.Select(p => new TagPointConfiguration { TagId = p.TagId, Address = p.Address }).ToArray(),
    };

    private static async Task StopHostAsync(IHost host)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StopAsync(cts.Token);
        (host as IDisposable)?.Dispose();
    }

    private static async Task WaitUntilCachedAsync(IPlcMonitor monitor, string deviceId, string tagId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (monitor.Get(deviceId, tagId) is null)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Point {deviceId}/{tagId} was not cached in time.");
            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilDeviceCachedAsync(IPlcMonitor monitor, string deviceId, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (monitor.GetDevice(deviceId).Count < count)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Device {deviceId} did not cache {count} points in time.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Monitor_RealModbusTcp_PushesOnlyChangedValues()
    {
        await using var slave = new ModbusSlaveServer(1);
        slave.Slave.DataStore.HoldingRegisters.WritePoints(0, new ushort[] { 42 });

        var host = BuildHost();
        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var monitor = host.Services.GetRequiredService<IPlcMonitor>();

            await scheduler.ApplyDevicesAsync([CreateDevice("modbus-01", slave.Port, ("hr1", "40001"))]);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var subCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var enumerator = monitor.SubscribeAsync("modbus-01", "hr1", subCts.Token).GetAsyncEnumerator();
            try
            {
                // 首个值：真实读取 42
                Assert.True(await enumerator.MoveNextAsync());
                Assert.Equal((ushort)42, (ushort)enumerator.Current.Value!);
                Assert.Equal("modbus-01", enumerator.Current.DeviceId);

                // 改变从站值 → 下一次轮询只推「变化」43
                slave.Slave.DataStore.HoldingRegisters.WritePoints(0, new ushort[] { 43 });
                Assert.True(await enumerator.MoveNextAsync());
                Assert.Equal((ushort)43, (ushort)enumerator.Current.Value!);

                // 值不变：约 6 次轮询内不应有第三个元素，读操作只能被取消结束
                subCts.CancelAfter(600);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    async () => await enumerator.MoveNextAsync());
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }
        finally
        {
            await StopHostAsync(host);
        }
    }

    [Fact]
    public async Task SubscribeDeviceAsync_RealModbus_PushesOnlyChangedPoints()
    {
        await using var slave = new ModbusSlaveServer(1);
        slave.Slave.DataStore.HoldingRegisters.WritePoints(0, new ushort[] { 42, 7 });

        var host = BuildHost();
        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var monitor = host.Services.GetRequiredService<IPlcMonitor>();

            await scheduler.ApplyDevicesAsync([CreateDevice("modbus-01", slave.Port, ("hr1", "40001"), ("hr2", "40002"))]);

            // 初始快照走拉模式
            await WaitUntilDeviceCachedAsync(monitor, "modbus-01", 2);
            Assert.Equal((ushort)42, (ushort)monitor.Get("modbus-01", "hr1")!.Value.Value!);
            Assert.Equal((ushort)7, (ushort)monitor.Get("modbus-01", "hr2")!.Value.Value!);

            // 设备级订阅：只推变化
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var subCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var enumerator = monitor.SubscribeDeviceAsync("modbus-01", subCts.Token).GetAsyncEnumerator();
            try
            {
                // 只改 hr1：应只收到 hr1 的变化（hr2 未变，不推）
                slave.Slave.DataStore.HoldingRegisters.WritePoints(0, new ushort[] { 43, 7 });
                Assert.True(await enumerator.MoveNextAsync());
                Assert.Equal("hr1", enumerator.Current.TagId);
                Assert.Equal((ushort)43, (ushort)enumerator.Current.Value!);

                subCts.CancelAfter(400);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    async () => await enumerator.MoveNextAsync());
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }
        finally
        {
            await StopHostAsync(host);
        }
    }

    [Fact]
    public async Task TwoDevices_SameTagAndAddress_AreIsolated()
    {
        await using var slaveA = new ModbusSlaveServer(1);
        await using var slaveB = new ModbusSlaveServer(1);
        slaveA.Slave.DataStore.HoldingRegisters.WritePoints(0, new ushort[] { 42 });
        slaveB.Slave.DataStore.HoldingRegisters.WritePoints(0, new ushort[] { 99 });

        var host = BuildHost();
        await host.StartAsync();
        try
        {
            var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
            var monitor = host.Services.GetRequiredService<IPlcMonitor>();

            // 两台设备：TagId 相同、地址相同，仅端口不同（同型号 PLC 只换 IP 的场景）
            await scheduler.ApplyDevicesAsync([
                CreateDevice("plc-a", slaveA.Port, ("hr1", "40001")),
                CreateDevice("plc-b", slaveB.Port, ("hr1", "40001")),
            ]);

            await WaitUntilCachedAsync(monitor, "plc-a", "hr1");
            await WaitUntilCachedAsync(monitor, "plc-b", "hr1");

            // 相同 TagId + 地址，但值互相隔离
            Assert.Equal((ushort)42, (ushort)monitor.Get("plc-a", "hr1")!.Value.Value!);
            Assert.Equal((ushort)99, (ushort)monitor.Get("plc-b", "hr1")!.Value.Value!);

            // 订阅 A：改 A 收到变化；改 B 时 A 不应收到
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var subCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var aEnumerator = monitor.SubscribeAsync("plc-a", "hr1", subCts.Token).GetAsyncEnumerator();
            try
            {
                Assert.True(await aEnumerator.MoveNextAsync());
                Assert.Equal((ushort)42, (ushort)aEnumerator.Current.Value!);

                slaveA.Slave.DataStore.HoldingRegisters.WritePoints(0, new ushort[] { 43 });
                Assert.True(await aEnumerator.MoveNextAsync());
                Assert.Equal((ushort)43, (ushort)aEnumerator.Current.Value!);

                // 改 B：A 的订阅不应被干扰
                slaveB.Slave.DataStore.HoldingRegisters.WritePoints(0, new ushort[] { 100 });
                subCts.CancelAfter(400);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    async () => await aEnumerator.MoveNextAsync());

                // 两侧缓存各自生效且互不影响
                Assert.Equal((ushort)43, (ushort)monitor.Get("plc-a", "hr1")!.Value.Value!);
                Assert.Equal((ushort)100, (ushort)monitor.Get("plc-b", "hr1")!.Value.Value!);
            }
            finally
            {
                await aEnumerator.DisposeAsync();
            }
        }
        finally
        {
            await StopHostAsync(host);
        }
    }
}

internal sealed class ModbusSlaveServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
    private readonly IModbusSlaveNetwork _network;
    private readonly Task _listenTask;

    public ModbusSlaveServer(byte unitId)
    {
        var factory = new ModbusFactory();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _network = factory.CreateSlaveNetwork(_listener);
        Slave = factory.CreateSlave(unitId, null);
        _network.AddSlave(Slave);
        _listenTask = _network.ListenAsync(_cts.Token);
    }

    public int Port { get; }

    public IModbusSlave Slave { get; }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _listenTask; } catch { }
        (_network as IDisposable)?.Dispose();
        _listener.Stop();
        _cts.Dispose();
    }
}
