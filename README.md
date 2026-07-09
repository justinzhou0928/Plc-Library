# PlcLibrary

PLC 数据采集库，提供连接池管理、定时采集调度、数据分发管道。

- 协议无关驱动接口，一行注册新协议
- 连接池 + Polly 弹性策略（重试、超时、断路器）
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

### 1. 注册服务

```csharp
builder.Services.AddPlcLibrary(builder.Configuration);
builder.Services.AddDriver<S7Driver>("S7");
```

### 2. 配置设备

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

### 3. 推送设备到调度器

```csharp
internal sealed class DeviceLoader(
    IConfiguration config,
    ITaskScheduler scheduler) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var devices = config.GetSection("Devices").Get<DeviceConfiguration[]>();
        if (devices is { Length: > 0 })
            await scheduler.ApplyDevicesAsync(devices, ct);
    }
}

builder.Services.AddHostedService<DeviceLoader>();
```

### 4. 接收采集数据

```csharp
internal sealed class MyHandler : IDataHandler
{
    public Task HandleAsync(DriverResult result, CancellationToken ct)
    {
        Console.WriteLine($"[{result.DeviceId}] {result.Address} = {result.Value}");
        return Task.CompletedTask;
    }
}

builder.Services.AddSingleton<IDataHandler, MyHandler>();
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

## 驱动池配置

```json
{
  "DriverPool": {
    "MaxConnectionsPerDevice": 2,
    "IdleTimeout": "00:05:00",
    "MaxRetryAttempts": 3,
    "RetryDelay": "00:00:01",
    "CircuitBreakerMinimumThroughput": 5,
    "CircuitBreakerDuration": "00:00:30",
    "OperationTimeout": "00:00:10"
  }
}
```

## 管道配置

```json
{
  "Pipeline": {
    "Capacity": 10000,
    "MaxHandlerParallelism": 4
  }
}
```

## API

### 核心接口

| 接口 | 说明 |
|------|------|
| `ITaskScheduler` | 推送设备配置、启停调度 |
| `IDataHandler` | 接收采集推送 |
| `IDeviceAccessor` | 主动读写设备 |
| `IProtocolDriver` | 协议驱动实现 |
| `IAsyncDisposable` | 完成异步清理 |
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
