using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using PlcLibrary.Bacnet;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.General.Configuration;
using PlcLibrary.Tests.Simulators;

namespace PlcLibrary.Tests.Bacnet;

/// <summary>
/// 真实 UDP 回环的 BACnet 端到端验证：进程内模拟器应答真实 BACnet/IP 报文，
/// 驱动走真实的 ReadPropertyMultiple / WriteProperty 服务。
/// </summary>
public class BacnetDriverSimulatorTests
{
    private const string DeviceId = "bacnet-sim";

    [Fact]
    public async Task ConnectAsync_WithDirectAddress_ReportsConnected()
    {
        await using var sim = new BacnetSimServer();
        await using var driver = await ConnectDriverAsync(sim);

        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);
    }

    [Fact]
    public async Task ReadAsync_MixedPoints_ReturnsValuesByIndexAndIsolatesInvalidAddress()
    {
        await using var sim = new BacnetSimServer();
        await using var driver = await ConnectDriverAsync(sim);

        var points = new[]
        {
            Point("av1", "AV:1"),
            Point("ai0", "AI:0"),
            Point("bad", "ZZ:1"),
            Point("bv3", "BV:3"),
        };

        var results = await driver.ReadAsync(points);

        Assert.Equal(points.Length, results.Length);

        Assert.Equal("AV:1", results[0].Address);
        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(12.5f, Assert.IsType<float>(results[0].Value));

        Assert.Equal("AI:0", results[1].Address);
        Assert.Equal(QualityCode.Good, results[1].Status);
        Assert.Equal(7.75f, Assert.IsType<float>(results[1].Value));

        // 非法地址在客户端本地被拒绝，不能影响同一批次的合法点位。
        Assert.Equal("ZZ:1", results[2].Address);
        Assert.Equal(QualityCode.BadConfigError, results[2].Status);
        Assert.Contains("Unknown BACnet object type", results[2].ErrorMessage);

        Assert.Equal("BV:3", results[3].Address);
        Assert.Equal(QualityCode.Good, results[3].Status);
        Assert.True(Assert.IsType<bool>(results[3].Value));
    }

    [Fact]
    public async Task WriteAsync_ValidPointUpdatesSimulator_InvalidAddressReturnsBadConfig()
    {
        await using var sim = new BacnetSimServer();
        await using var driver = await ConnectDriverAsync(sim);

        var valid = Point("av1", "AV:1");
        var invalid = Point("bad", "ZZ:1");
        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [valid] = 42.5f,
            [invalid] = 1.0f,
        });

        Assert.Equal(2, results.Length);
        Assert.Equal(QualityCode.Good, results.Single(r => r.Address == "AV:1").Status);
        Assert.Equal(QualityCode.BadConfigError, results.Single(r => r.Address == "ZZ:1").Status);

        // 写请求必须真的到达模拟器并改变设备侧存储值。
        Assert.Equal(42.5f,
            Assert.IsType<float>(sim.GetPresentValue(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1)));

        // 再读一次，确认写入值在真实报文往返中可见。
        var readBack = Assert.Single(await driver.ReadAsync([valid]));
        Assert.Equal(QualityCode.Good, readBack.Status);
        Assert.Equal(42.5f, Assert.IsType<float>(readBack.Value));
    }

    [Fact]
    public async Task ReadAsync_NonExistentObject_ReturnsBadCommFailureWithoutThrowing()
    {
        await using var sim = new BacnetSimServer();
        await using var driver = await ConnectDriverAsync(sim);

        var results = await driver.ReadAsync([Point("missing", "AV:99")]);

        var result = Assert.Single(results);
        Assert.Equal(QualityCode.BadCommFailure, result.Status);
        Assert.Equal("AV:99", result.Address);
        Assert.Contains("Error from device", result.ErrorMessage);
        Assert.Contains("ERROR_CLASS_OBJECT", result.ErrorMessage);
        Assert.Contains("ERROR_CODE_UNKNOWN_OBJECT", result.ErrorMessage);
    }

    /// <summary>
    /// 设备返回 Error PDU（点位级错误）时，驱动应保持 Connected，只把该点标为 BadCommFailure，
    /// 连接池不能因此丢弃驱动。
    /// </summary>
    [Fact]
    public async Task ReadAsync_NonExistentObject_KeepsDriverConnected()
    {
        await using var sim = new BacnetSimServer();
        await using var driver = await ConnectDriverAsync(sim);

        _ = await driver.ReadAsync([Point("missing", "AV:99")]);

        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);
    }

    /// <summary>
    /// 回归测试：标准 RMP-ACK 的逐对象错误（BacnetError）必须降级为 BadCommFailure，
    /// 不能以 Good 质量进入缓存 —— 否则未知对象会被当成有效数据。
    /// </summary>
    [Fact]
    public async Task ReadAsync_PerObjectError_ReturnsBadQuality()
    {
        await using var sim = new BacnetSimServer(perObjectErrors: true);
        await using var driver = await ConnectDriverAsync(sim);

        var results = await driver.ReadAsync([Point("av1", "AV:1"), Point("missing", "AV:99")]);

        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(12.5f, Assert.IsType<float>(results[0].Value));

        Assert.Equal(QualityCode.BadCommFailure, results[1].Status);
        Assert.Null(results[1].Value);
        Assert.Contains("BACnet error", results[1].ErrorMessage);
    }

    /// <summary>
    /// 回归测试：若 RMP-ACK 省略了某个请求对象（不完整响应），该点位必须是 BadCommFailure，
    /// 而不是数组默认值（Status=Good、Address/Value=null）。
    /// </summary>
    [Fact]
    public async Task ReadAsync_ResponseOmitsObject_ReturnsBadQuality()
    {
        await using var sim = new BacnetSimServer(omitUnknownObjects: true);
        await using var driver = await ConnectDriverAsync(sim);

        var results = await driver.ReadAsync([Point("av1", "AV:1"), Point("missing", "AV:99")]);

        Assert.Equal(QualityCode.Good, results[0].Status);
        Assert.Equal(12.5f, Assert.IsType<float>(results[0].Value));

        Assert.Equal(QualityCode.BadCommFailure, results[1].Status);
        Assert.Equal("AV:99", results[1].Address);
        Assert.Null(results[1].Value);
        Assert.Equal("No response for object", results[1].ErrorMessage);
    }

    /// <summary>
    /// 回归测试：WritePropertyAsync 的参数顺序是 invokeId, priority, arrayIndex, ct。
    /// Present_Value 不是数组属性，arrayIndex 必须是 BACNET_ARRAY_ALL（0xFFFFFFFF），
    /// 不能把配置的超时值编码进报文。
    /// </summary>
    [Fact]
    public async Task WriteAsync_UsesArrayAllInsteadOfTimeout()
    {
        await using var sim = new BacnetSimServer();
        await using var driver = await ConnectDriverAsync(sim);

        var results = await driver.WriteAsync(new Dictionary<TagPointConfiguration, object>
        {
            [Point("av1", "AV:1")] = 1.5f,
        });

        Assert.Equal(QualityCode.Good, Assert.Single(results).Status);
        Assert.True(sim.LastWrite.HasValue);
        Assert.NotEqual(1000u, sim.LastWrite!.Value.ArrayIndex);
        Assert.Equal(uint.MaxValue, sim.LastWrite.Value.ArrayIndex);
    }

    /// <summary>
    /// 回归测试：读取 Present_Value 时 array index 必须是 BACNET_ARRAY_ALL，
    /// 而不是 0（部分严格设备会拒绝非数组属性的显式索引）。
    /// </summary>
    [Fact]
    public async Task ReadAsync_UsesArrayAllInsteadOfZero()
    {
        await using var sim = new BacnetSimServer();
        await using var driver = await ConnectDriverAsync(sim);

        _ = await driver.ReadAsync([Point("av1", "AV:1")]);

        Assert.Equal(uint.MaxValue, sim.LastReadArrayIndex);
    }

    [Fact]
    public async Task DisconnectAsync_SetsDisconnected_AndReadThrowsInvalidOperationException()
    {
        await using var sim = new BacnetSimServer();
        await using var driver = await ConnectDriverAsync(sim);

        await driver.DisconnectAsync();

        Assert.Equal(DriverStatus.Disconnected, driver.DriverStatus);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => driver.ReadAsync([Point("av1", "AV:1")]));
        Assert.Contains("not connected", ex.Message);
    }

    [Fact]
    public async Task ReadAsync_PeerStopsResponding_TimesOutWithBadQualityAndFaults()
    {
        await using var sim = new BacnetSimServer();
        await using var driver = await ConnectDriverAsync(sim);
        sim.PauseResponses = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await driver.ReadAsync([Point("av1", "AV:1")]);
        sw.Stop();

        Assert.Equal(QualityCode.BadCommFailure, Assert.Single(results).Status);
        Assert.Equal(DriverStatus.Faulted, driver.DriverStatus);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"BACnet timeout 未生效: {sw.Elapsed}");
    }

    [Fact]
    public async Task ConnectAsync_WithDeviceInstance_ResolvesViaWhoIsIam()
    {
        const uint instance = 1234;
        await using var sim = new BacnetSimServer(instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var driver = await ConnectDriverAsync(sim, instance, cts.Token);

        Assert.Equal(DriverStatus.Connected, driver.DriverStatus);
        Assert.True(sim.WhoIsRequestCount > 0);

        var read = Assert.Single(await driver.ReadAsync([Point("av1", "AV:1")], cts.Token));
        Assert.Equal(QualityCode.Good, read.Status);
        Assert.Equal(12.5f, Assert.IsType<float>(read.Value));
    }

    private static TagPointConfiguration Point(string tagId, string address) => new()
    {
        TagId = tagId,
        Address = address,
    };

    private static DeviceConfiguration CreateDevice(BacnetSimServer sim, uint deviceInstance) => new()
    {
        Id = DeviceId,
        Protocol = "BACnet",
        ConnectionString = sim.CreateConnectionString(deviceInstance),
    };

    private static async Task<BacnetDriver> ConnectDriverAsync(
        BacnetSimServer sim, uint deviceInstance = 0, CancellationToken ct = default)
    {
        var driver = new BacnetDriver(NullLogger<BacnetDriver>.Instance, CreateDevice(sim, deviceInstance));
        try
        {
            await driver.ConnectAsync(ct);
            return driver;
        }
        catch
        {
            await driver.DisposeAsync();
            throw;
        }
    }
}
