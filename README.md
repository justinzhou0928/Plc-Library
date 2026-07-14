# PlcLibrary

PLC 数据采集库，提供连接池管理、定时采集调度、数据分发管道。

- 协议无关驱动接口，一行注册新协议
- 连接池 + Polly 弹性策略（重试、超时、断路器），每设备独立隔离
- 设备配置热更新，差量 reconcile
- Channel 管道 fan-out 到多个 `IDataHandler`
- 主动读写 + 自动采集双模式

## 支持的驱动

| 驱动 | 协议 | 状态 |
|------|------|------|
| `S7Driver` | Siemens S7 (S7-200/300/400/1200/1500) | 可用 |
| `ModbusTcpDriver` | Modbus TCP | 可用 |
| `ModbusUdpDriver` | Modbus UDP | 可用 |
| `ModbusRtuDriver` | Modbus RTU | 待 NModbus 更新 |
| `ModbusAsciiDriver` | Modbus ASCII | 待 NModbus 更新 |

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
    .AddDriver<S7Driver>()
    .AddSingleton<IDataHandler, ConsoleHandler>();

var host = builder.Build();
await host.Services.GetRequiredService<IDeviceScheduler>().ApplyDevicesAsync(devices);
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
internal sealed class DeviceLoader(IConfiguration config, IDeviceScheduler scheduler) : BackgroundService
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

**Modbus TCP / UDP**

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | 127.0.0.1 | 设备地址 |
| port | 502 | 端口 |
| slaveid | 1 | 从站 ID |
| timeout | 3000 | 超时 (ms) |

示例：`host:10.0.0.1;port:502;slaveid:2`

**Modbus RTU / ASCII**（待 NModbus 更新）

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | - | 串口号（COM3） |
| baudrate | 9600 | 波特率 |
| parity | None | 校验（None/Odd/Even） |
| databits | 8 | 数据位 |
| stopbits | One | 停止位（One/Two） |
| slaveid | 1 | 从站 ID |

示例：`host:COM3;baudrate:19200;parity:Even;slaveid:1`

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
    "CircuitBreakerFailureRatio": 0.5,
    "OperationTimeout": "00:00:10"
  }
}
```

### 管道

```json
{
  "Pipeline": {
    "Capacity": 10000,
    "MaxHandlerParallelism": 8,
    "HandlerTimeout": "00:00:30"
  }
}
```

## API

### 核心接口

| 接口 | 说明 |
|------|------|
| `IDeviceScheduler` | 推送设备配置，差量 reconcile |
| `IDataHandler` | 接收采集推送 |
| `IDeviceAccessor` | 主动读写设备 |
| `IProtocolDriver` | 协议驱动实现（开发文档见 [DEVELOPMENT.md](./DEVELOPMENT.md)） |
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

### 健康检查

框架注入 `IHealthCheck`，标准 ASP.NET Core 健康检查端点可直接使用：

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<PlcLibraryHealthCheck>("plc-library");
```

输出 `ActiveDevices` 和 `PoolCount` 指标。

### 设备级连接超时

`DeviceConfiguration.ConnectionTimeout` 可覆盖全局 `PoolOptions.OperationTimeout`，在获取驱动时优先使用设备级配置。默认 `TimeSpan.Zero` 表示使用全局配置。

断路器状态变更（熔断/半开/恢复）通过 `ILogger` 输出 `Warning`/`Information` 级别日志，每设备独立隔离。

## 许可证

MIT
