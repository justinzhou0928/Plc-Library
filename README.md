# PlcLibrary

PLC 数据采集库，提供连接池管理、定时采集调度、数据分发管道。

- 协议无关驱动接口，一行注册新协议
- 连接池 + Polly 弹性策略（重试、超时、断路器），每设备独立隔离
- 设备配置热更新，差量 reconcile
- Channel 管道 fan-out 到多个 `IDataHandler`
- 主动读写 + 自动采集双模式

## 安装

```bash
dotnet add package PlcLibrary
```

S7 驱动额外引用：

```bash
dotnet add package PlcLibrary.S7
```

## 快速开始

```csharp
using Microsoft.Extensions.Logging;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.Extensions;
using PlcLibrary.General.Configuration;
using PlcLibrary.Pipeline.Interfaces;
using PlcLibrary.S7;

var builder = Host.CreateApplicationBuilder(args);

var devices = new[]
{
    new DeviceConfiguration
    {
        Id = "plc-001",
        Name = "1号线",
        Protocol = "S7",
        ConnectionString = "host:10.38.103.107;port:102;timeout:3000;rack:0;slot:0;cpu:S71200;",
        TagPoints = new[]
        {
            new TagPointConfiguration { TagId = "t1", Address = "DB21.DBX10.2", DataType = "System.Boolean" },
            new TagPointConfiguration { TagId = "t2", Address = "DB21.DBX10.0", DataType = "System.Boolean" },
        },
        CollectionInterval = TimeSpan.FromSeconds(1),
    },
};

builder.Services
    .AddPlcLibrary()
    .AddDriver<S7Driver>("S7")
    .AddSingleton<IDataHandler, ConsoleHandler>();

var host = builder.Build();
await host.Services.GetRequiredService<ITaskScheduler>().ApplyDevicesAsync(devices);
await host.RunAsync();

internal sealed class ConsoleHandler(ILogger<ConsoleHandler> logger) : IDataHandler
{
    public ValueTask HandleAsync(DriverResult result, CancellationToken ct)
    {
        logger.LogInformation("[{DeviceId}] {Address} = {Value} ({Status})",
            result.DeviceId, result.Address, result.Value, result.Status);
        return ValueTask.CompletedTask;
    }
}
```

> 设备多时可用 `BackgroundService` + `IConfiguration` 从 appsettings.json 读取，参考下文 JSON 配置方式。

### 使用 JSON 配置

```csharp
// 绑定 Options 到 appsettings.json
builder.Services.AddPlcLibrary();
builder.Services.Configure<PoolOptions>(builder.Configuration.GetSection("DriverPool"));
builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection("Pipeline"));
```

```json
{
  "Devices": [
    {
      "Enabled": true,
      "Id": "plc-01",
      "Protocol": "S7",
      "ConnectionString": "host:192.168.1.1;port:102;rack:0;slot:1;cpu:S71200",
      "CollectionInterval": "00:00:01",
      "TagPoints": [
        { "TagId": "temp", "Address": "DB1.DBD0", "DataType": "Real" },
        { "TagId": "pressure", "Address": "DB1.DBD4", "DataType": "Real" }
      ]
    }
  ]
}
```

```csharp
internal sealed class DeviceLoader(IConfiguration config, ITaskScheduler scheduler) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var devices = config.GetSection("Devices").Get<DeviceConfiguration[]>();
        if (devices is { Length: > 0 })
            await scheduler.ApplyDevicesAsync(devices, ct);
    }
}
```

## 连接字符串

格式 `key:value;key:value`，大小写不敏感。

**S7**

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | 127.0.0.1 | PLC 地址 |
| port | 102 | 端口 |
| rack | 0 | 机架 |
| slot | 0 | 插槽 |
| timeout | 3000 | 超时 (ms) |
| cpu | S71200 | CpuType |

示例：`host:192.168.1.1;port:102;rack:0;slot:1;cpu:S71500`

## 配置

### 驱动池

```json
{
  "DriverPool": {
    "MaxConnectionsPerDevice": 2,
    "MaxRetryAttempts": 3,
    "RetryDelay": "00:00:01",
    "CircuitBreakerMinimumThroughput": 5,
    "CircuitBreakerDuration": "00:00:30",
    "OperationTimeout": "00:00:10"
  }
}
```

### 管道

```json
{
  "Pipeline": {
    "Capacity": 10000,
    "MaxHandlerParallelism": 4,
    "HandlerTimeout": "00:00:30"
  }
}
```

## API

### 核心接口

| 接口 | 说明 |
|------|------|
| `ITaskScheduler` | 推送设备配置，差量 reconcile |
| `IDataHandler` | 接收采集推送 |
| `IDeviceAccessor` | 主动读写设备 |
| `IProtocolDriver` | 协议驱动实现 |
| `IDriverFactory` | 驱动工厂（通常用 `AddDriver<T>` 替代） |
| `IDataPipeline` | 数据管道（通常不需要直接使用） |

### 主动读写

```csharp
public class MyService(IDeviceAccessor accessor)
{
    public async Task ReadDevice(DeviceConfiguration device)
    {
        var values = await accessor.ReadAsync(device, device.TagPoints);
    }

    public async Task WriteDevice(DeviceConfiguration device)
    {
        var points = new Dictionary<TagPointConfiguration, object>
        {
            [device.TagPoints[0]] = 123.45
        };
        await accessor.WriteAsync(device, points);
    }
}
```

## 自定义驱动

实现 `IProtocolDriver`、`IDisposable`、`IAsyncDisposable`：

```csharp
public sealed class ModbusDriver : IProtocolDriver, IDisposable, IAsyncDisposable
{
    public DriverStatus DriverStatus { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default) { ... }
    public Task DisconnectAsync(CancellationToken ct = default) { ... }
    public Task<bool> TryReconnectAsync(CancellationToken ct = default) { ... }
    public Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct = default) { ... }
    public Task<DriverResult[]> WriteAsync(IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct = default) { ... }
    public void Dispose() { ... }
    public ValueTask DisposeAsync() { ... }
}
```

注册：

```csharp
services.AddDriver<ModbusDriver>("Modbus");
```

如果连接 Key 需要自定义（同 IP 不同端口共用池）：

```csharp
services.AddDriver<ModbusDriver>("Modbus", cs => {
    var c = ModbusConfig.Parse(cs);
    return $"{c.Host}:{c.Port}";
});
```

## 许可证

MIT
