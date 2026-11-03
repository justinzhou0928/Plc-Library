using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Opc.Ua;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverPool.Engine;
using PlcLibrary.DriverPool.Models;
using PlcLibrary.General.Configuration;
using PlcLibrary.OpcUa;
using PlcLibrary.Tests.Simulators;

namespace PlcLibrary.Tests.OpcUa;

/// <summary>
/// OPC UA 标签浏览能力：从 Objects 文件夹浏览到 Server 对象，再读取变量的数据类型/可写性。
/// </summary>
public class OpcUaTagBrowseTests
{
    [Fact]
    public async Task Browse_ObjectsFolder_ReturnsServerObject()
    {
        await using var server = new OpcUaTestServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(cts.Token);

        var driver = new OpcUaDriver(NullLogger<OpcUaDriver>.Instance, CreateDevice(server.Port));
        await driver.ConnectAsync(cts.Token);

        try
        {
            var tags = await driver.BrowseAsync(null, cts.Token);

            Assert.NotEmpty(tags);
            Assert.Contains(tags, t => t.IsFolder && t.Name.Contains("Server", StringComparison.OrdinalIgnoreCase));
            Assert.All(tags, t => Assert.False(string.IsNullOrWhiteSpace(t.Address)));
        }
        finally
        {
            await driver.DisposeAsync();
        }
    }

    [Fact]
    public async Task Browse_ServerNode_ReturnsVariablesWithDataType()
    {
        await using var server = new OpcUaTestServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(cts.Token);

        var driver = new OpcUaDriver(NullLogger<OpcUaDriver>.Instance, CreateDevice(server.Port));
        await driver.ConnectAsync(cts.Token);

        try
        {
            var tags = await driver.BrowseAsync(ObjectIds.Server.ToString(), cts.Token);

            Assert.NotEmpty(tags);
            var serverStatus = Assert.Single(tags, t => t.Name == "ServerStatus");
            Assert.False(serverStatus.IsFolder);
            Assert.False(string.IsNullOrWhiteSpace(serverStatus.DataType));
        }
        finally
        {
            await driver.DisposeAsync();
        }
    }

    [Fact]
    public async Task Browse_InvalidAddress_ThrowsInvalidOperationException()
    {
        await using var server = new OpcUaTestServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(cts.Token);

        var driver = new OpcUaDriver(NullLogger<OpcUaDriver>.Instance, CreateDevice(server.Port));
        await driver.ConnectAsync(cts.Token);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => driver.BrowseAsync("not-a-node-id", cts.Token));
        }
        finally
        {
            await driver.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeviceBrowserAccessor_BrowsesViaPool()
    {
        await using var server = new OpcUaTestServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(cts.Token);

        var device = CreateDevice(server.Port);
        var factory = new GenericDriverFactory(
            "OPC_UA",
            d => new OpcUaDriver(NullLogger<OpcUaDriver>.Instance, d),
            supportsPush: true);
        await using var pool = new DeviceDriverPool(
            Options.Create(new PoolOptions
            {
                MaxConnectionsPerDevice = 1,
                OperationTimeout = TimeSpan.FromSeconds(10),
                PoolIdleTimeout = TimeSpan.Zero,
            }),
            [factory],
            NullLoggerFactory.Instance);

        IDeviceBrowser browser = pool;
        var tags = await browser.BrowseAsync(device, null, cts.Token);

        Assert.NotEmpty(tags);
        Assert.Contains(tags, t => t.IsFolder);
    }

    private static string PkiRoot() => Path.Combine(Path.GetTempPath(), "plc-ua-browse", Guid.NewGuid().ToString("N"));

    private static DeviceConfiguration CreateDevice(int port) => new()
    {
        Id = "ua-browse",
        Protocol = "OPC_UA",
        ConnectionString =
            $"endpoint:opc.tcp://127.0.0.1:{port};security:None;timeout:10000;sessiontimeout:60000;autoacceptcertificate:true;" +
            $"pkiownpath:{Path.Combine(PkiRoot(), "own")};" +
            $"pkitrustedpath:{Path.Combine(PkiRoot(), "trusted")};" +
            $"pkiissuerpath:{Path.Combine(PkiRoot(), "issuer")};" +
            $"pkirejectedpath:{Path.Combine(PkiRoot(), "rejected")}",
    };
}
