# PlcLibrary

PLC 数据采集库，提供连接池管理、定时采集调度、数据分发管道。

- 协议无关驱动接口，一行注册新协议
- 连接池 + Polly 弹性策略（重试、超时、断路器），**每设备独立隔离**，覆盖连接和 IO 两级
- 设备配置热更新，差量 reconcile
- Channel 管道 fan-out 到多个 `IDataHandler`
- 主动读写 + 自动采集双模式
- `System.Diagnostics.Metrics` 内置可观测性，0 依赖接入 Prometheus / Grafana

## 支持的驱动

| 驱动 | 协议 | 状态 |
|------|------|------|
| `S7Driver` | Siemens S7 (S7-200/300/400/1200/1500) | 可用 |
| `ModbusTcpDriver` | Modbus TCP | 可用 |
| `ModbusUdpDriver` | Modbus UDP | 可用 |
| `OpcUaDriver` | OPC UA | 可用 |
| `MitsubishiDriver` | Mitsubishi MC / A1E / FX | 可用 |
| `OmronDriver` | Omron FINS TCP | 可用 |
| `AllenBradleyDriver` | Allen-Bradley Logix Tag (CIP/EtherNet/IP) | 可用 |
| `BacnetDriver` | BACnet/IP | 可用 |
| `ModbusRtuDriver` | Modbus RTU | 可用 |
| `ModbusAsciiDriver` | Modbus ASCII | 可用 |

## 安装

```bash
dotnet add package PlcLibrary
```

按需添加驱动包：

```bash
dotnet add package PlcLibrary.S7
dotnet add package PlcLibrary.Modbus
dotnet add package PlcLibrary.OpcUa
dotnet add package PlcLibrary.Mitsubishi
dotnet add package PlcLibrary.Omron
dotnet add package PlcLibrary.AllenBradley
dotnet add package PlcLibrary.Bacnet
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

**Modbus 地址格式**：`前缀 + 1-based 十进制地址`（前缀标记数据类型，地址范围 1 ~ 65536，超出或前缀不符视为配置错误）：

| 前缀 | 类型 | 读写 | 示例 | 说明 |
|------|------|:--:|------|------|
| `0xxxx` | Coil | 读写 | `00001` | 线圈，布尔量 |
| `1xxxx` | Discrete Input | 只读 | `10042` | 离散输入，布尔量 |
| `3xxxx` | Input Register | 只读 | `30001` | 输入寄存器，16 位无符号整数 |
| `4xxxx` | Holding Register | 读写 | `40042` | 保持寄存器，16 位无符号整数 |

地址为 **1-based**（PLC 习惯），驱动内部自动转换为 0-based 偏移。连续地址自动合并为单次批量读写，并按 Modbus PDU 上限自动切分（读寄存器 ≤125、线圈/离散输入 ≤2000；写寄存器 ≤123、线圈 ≤1968）。

**Modbus RTU / ASCII**（串口）

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | - | 串口号（COM3） |
| baudrate | 9600 | 波特率 |
| parity | None | 校验（None/Odd/Even/Mark/Space） |
| databits | 8 | 数据位 |
| stopbits | One | 停止位（One/OnePointFive/Two） |
| timeout | 3000 | 超时 (ms) |
| slaveid | 1 | 从站 ID |

示例：`host:COM3;baudrate:19200;parity:Even;stopbits:One;slaveid:1`

> 串口传输基于 [NModbus.Serial](https://www.nuget.org/packages/NModbus.Serial)（与 NModbus 主包同版本发布），`Parity`/`StopBits` 字符串大小写不敏感。

**OPC UA**

| 字段 | 默认值 | 说明 |
|------|--------|------|
| endpoint | opc.tcp://localhost:4840 | 服务器端点 |
| username | - | 用户名（可选） |
| password | - | 密码（可选） |
| security | None | None / Sign / SignAndEncrypt（端点实际安全模式低于配置时连接失败，不静默降级） |
| timeout | 5000 | 超时 (ms) |
| publishinginterval | 1000 | 订阅发布间隔 (ms) |
| sessiontimeout | 60000 | 会话超时 (ms) |
| autoacceptcertificate | false | 自动接受证书（生产环境应设为 false） |

示例：`endpoint:opc.tcp://10.0.0.1:4840;security:None;timeout:10000`

**Mitsubishi**

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | 127.0.0.1 | PLC 地址 |
| port | 6000 | 端口 |
| timeout | 3000 | 超时 (ms) |
| protocoltype | MC | MC / A1E / FX |

示例：`host:192.168.1.1;port:6000;protocoltype:MC`

地址格式：三菱标准地址字符串，取决于所选协议类型：

| 协议类型 | 地址示例 | 说明 |
|----------|----------|------|
| `MC` | `D100`、`M100`、`X10`、`Y20`、`W0` | MELSEC MC 协议 |
| `A1E` | `D100`、`M100`、`X0`、`Y0` | A-1E 协议 |
| `FX` | `D100`、`M100`、`X0`、`Y0` | FX 编程口协议 |

**Omron FINS**

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | 127.0.0.1 | PLC 地址 |
| port | 9600 | 端口 |
| timeout | 3000 | 超时 (ms)，同时作用于连接与接收 |
| localnode | 1 | ⚠️ 已解析但当前无效：底层 `FinsClient` 节点号属性为只读，无法配置 |
| destinynode | 2 | ⚠️ 同上，暂无法配置 |
| isudp | false | ⚠️ 已解析但当前无效：底层 FINS 客户端仅支持 TCP，UDP 传输暂不可用 |

地址格式：Omron 标准 FINS 地址字符串。

| 区域 | 地址示例 | 说明 |
|------|----------|------|
| DM | `D100`、`D200` | 数据存储器 |
| CIO | `CIO200`、`CIO200.5` | 通道 I/O（`.5` 表示位） |
| WR | `W100` | 工作继电器 |
| HR | `H0`、`H10` | 保持继电器 |
| AR | `A0` | 辅助继电器 |

示例：`host:192.168.1.1;port:9600;localnode:1;destinynode:2`

**Allen-Bradley / Rockwell**

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | 127.0.0.1 | PLC / 网关模块地址 |
| port | 44818 | EtherNet/IP 端口 |
| timeout | 5000 | ⚠️ 连接/IO 超时由驱动池 `OperationTimeout` 兜底，本字段暂未透传底层库 |
| path | - | 路由路径（如 `1,0` 表示背板槽位 0） |
| useconnected | false | Class 3 连接（高频轮询时推荐开启） |

地址格式：Logix 标签名。

| 地址示例 | 说明 |
|----------|------|
| `rate` | 基础标签（DINT/REAL/BOOL 等原子类型） |
| `counts[3]` | 数组元素索引 |
| `Temp[10].AnotherArray[4]` | 嵌套数组 |
| `MyUdt.enable` | UDT 结构体成员 |
| `matrix[1,2,3]` | 多维数组 |

CompactLogix 示例：`host:192.168.1.96`

ControlLogix 示例：`host:192.168.1.96;path:1,0`

**BACnet**

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | 127.0.0.1 | 目标设备 IP |
| port | 47808 | BACnet/IP 端口（本地监听 + 远端目标均使用） |
| timeout | 5000 | 超时 (ms)，同时用于 WhoIs 设备发现等待 |
| deviceinstance | 0 | 目标设备实例号；0 表示直接用 host 寻址，>0 时通过 WhoIs/IAm 广播发现实例对应的网络地址（未找到则连接失败） |
| localendpointip | - | 多网卡时指定绑定 IP |

地址格式：`TYPE:INSTANCE`（如 `AV:1` = Analog Value 1、`BI:0` = Binary Input 0）。

支持类型：`AI`/`AO`/`AV`/`BI`/`BO`/`BV`/`MI`/`MO`/`MV`。

示例：`host:192.168.1.50;port:47808;deviceinstance:12345`

## 点位数据类型 (DataType)

部分驱动需要 `TagPointConfiguration.DataType` 指定点位的数据类型，以正确解析 PLC 返回值：

| 驱动 | DataType 是否必需 | 未指定时的默认值 | 说明 |
|------|:--:|------|------|
| S7 | 否（string 除外） | 地址推断 | 标量由地址推断（`DBX`=bool, `DBW`=word）；**字符串需地址带长度**（`DB6000.DBB504.100` 读 S7 STRING(100)） |
| Modbus | 否 | 地址前缀推断 | `0xxxx`=bool, `4xxxx`=ushort |
| OPC UA | 否 | 服务端返回 | 服务端告知类型，值透传 |
| BACnet | 否 | 服务端返回 | `PROP_PRESENT_VALUE` 返回原始值 |
| AllenBradley | **推荐** | `int` | 用于确定 `ReadAsync<T>` 的泛型类型 |
| Mitsubishi | **推荐** | `Int32` | 映射为 Snet `DataType` 枚举 |
| Omron | **推荐** | `int` | 分发到 `ReadInt16Async`/`ReadFloatAsync` 等方法 |

支持的 DataType 值（大小写不敏感）：

| 简写 | 完整名称 | C# 类型 |
|------|----------|--------|
| `bool` | `System.Boolean` | `bool` |
| `short` | `System.Int16` | `short` |
| `ushort` | `System.UInt16` | `ushort` |
| `int` | `System.Int32` | `int` |
| `uint` | `System.UInt32` | `uint` |
| `long` | `System.Int64` | `long` |
| `ulong` | `System.UInt64` | `ulong` |
| `float` | `System.Single` | `float` |
| `double` | `System.Double` | `double` |
| `string` | `System.String` | `string` |

```csharp
// Omron D100 为 32 位浮点数
new TagPointConfiguration { TagId = "temp", Address = "D100", DataType = "float" }

// AllenBradley tag 为布尔量
new TagPointConfiguration { TagId = "run", Address = "MotorRun", DataType = "bool" }
```

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
    "OperationTimeout": "00:00:10",
    "PoolIdleTimeout": "00:10:00"
  }
}
```

- `PoolIdleTimeout`：空闲连接池回收阈值。设备热更新移除后，其连接池空置超过该时长且无在途借用时被自动销毁（释放连接与弹性管线），避免长期运行累积。`00:00:00` 表示禁用自动回收。

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

### 弹性策略

Polly 弹性分为两级（连接级每设备独立隔离；IO 级为全局共享管线）：

| 级别 | 作用域 | 策略 | 触发位置 |
|---|---|---|---|
| **连接级** | `ConnectAsync` / `TryReconnectAsync` | 重试（指数退避） + 超时 + 断路器 | `DeviceSharedPool.AcquireAsync` |
| **IO 级** | `ReadAsync` / `WriteAsync` | 重试（指数退避） + 超时 | `DeviceDriverPool.ReadAsync` / `WriteAsync` |

- **连接级断路器**：连续失败达阈值后进入熔断（默认 5 次、失败率 ≥ 50%、冷却 30s），熔断/半开/恢复均有 `ILogger` 日志。
- **断线自动重连**：驱动在捕获到传输级故障（socket 断开、IO 超时等）时自动将自身状态置为 `Faulted`；连接池归还时丢弃坏驱动，下次采集用新连接重建（配合连接级断路器防止重连风暴）。
- **IO 级重试**：仅当驱动 `ReadAsync`/`WriteAsync` **抛出异常**时自动重试（默认 3 次），排除 `OperationCanceledException` 和 `TimeoutRejectedException`。驱动通常将点位级失败转为 `DriverResult.Bad` 返回（不抛异常），此时由断线重连机制兜底。
- 两级策略均复用 `DriverPool` 配置节中的 `MaxRetryAttempts`、`RetryDelay`、`OperationTimeout`。

### 批量读支持

| 驱动 | 批量读 | 说明 |
|------|:--:|------|
| Modbus | ✅ | 连续地址合并 + PDU 上限自动切分 |
| S7 | ✅ | `ReadMultipleVarsAsync` 批量 + 逐点回退 |
| OPC UA | ✅ | 单请求批量 Read；订阅推送逐点回调 |
| Mitsubishi | ✅ | `WriteAsync` 字典批量 |
| AllenBradley | ✅ | `ReadMultipleAsync` 原始字节批量 + CIP 小端本地解码（string 标签逐点） |
| BACnet | ✅ | `ReadPropertyMultipleAsync` 按对象分组批量 |
| Omron | ⚠️ 待定 | 底层 `BatchReadAsync` 存在，但 FINS 字节序/长度单位无法离线验证，为避免静默数据错误暂未启用，接入真实设备后可开启 |

## 可观测性

PlcLibrary 通过 `System.Diagnostics.Metrics`（.NET 6+ 内置，零 NuGet 依赖）暴露以下指标：

> 读/写/获取相关指标均带 `device.id`、`device.protocol` 标签（按设备维度计数），管道指标带 `device.id` 标签。

### Meter: `PlcLibrary`

| 指标名 | 类型 | 单位 | 说明 |
|---|---|---|---|
| `plc.reads.total` | `Counter<long>` | points | 累计采集点位总数 |
| `plc.read.duration` | `Histogram<double>` | s | ReadAsync 完整耗时（含 IO 重试） |
| `plc.read.errors` | `Counter<long>` | errors | ReadAsync 最终失败次数（含重试耗尽） |
| `plc.write.total` | `Counter<long>` | ops | 累计写操作数 |
| `plc.acquire.duration` | `Histogram<double>` | s | 连接池获取驱动耗时（含 connect） |
| `plc.pipeline.dispatched` | `Counter<long>` | points | 管道分发点数 |
| `plc.pipeline.dropped` | `Counter<long>` | points | 订阅通道溢出丢弃点数 |

### 接入方式

**OpenTelemetry Collector**（推荐）:

```bash
dotnet add package OpenTelemetry.Exporter.Prometheus.AspNetCore
```

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("PlcLibrary")
        .AddPrometheusExporter())
    .WithTracing(t => t
        .AddSource("PlcLibrary")
        .AddConsoleExporter()); // 或 Jaeger/OTLP
```

启动后访问 `/metrics` 即可被 Prometheus 抓取，Grafana 中查询 `plc_read_duration_seconds_bucket` 等指标。

### 分布式追踪（ActivitySource）

基础库在关键路径埋点 `ActivitySource("PlcLibrary")`，宿主接入 OpenTelemetry 后即可串联"调度 → 连接池 → 驱动 → 管道 → handler"的完整链路：

| Span 名称 | 埋点位置 | 标签 |
|-----------|---------|------|
| `PlcLibrary.ReadAsync` | `DeviceDriverPool.ReadAsync` | device.id / device.protocol / point.count |
| `PlcLibrary.WriteAsync` | `DeviceDriverPool.WriteAsync` | device.id / device.protocol / point.count |
| `PlcLibrary.Acquire` | `DeviceSharedPool.AcquireAsync` | device.id / device.protocol |
| `PlcLibrary.Dispatch` | `DriverResultPipeline.DispatchAsync` | device.id / tag.id / status |

无监听器时 `StartActivity` 返回 null，埋点开销可忽略；库不依赖任何 OTel 包。

**dotnet-counters**（本地调试）:

```bash
dotnet-counters monitor -n MyApp --counters PlcLibrary
```

### Grafana 面板示例

```
# 采集吞吐 (points/s)
rate(plc_reads_total[1m])

# 读延迟 p50 / p99
histogram_quantile(0.50, rate(plc_read_duration_seconds_bucket[1m]))
histogram_quantile(0.99, rate(plc_read_duration_seconds_bucket[1m]))

# 错误率
rate(plc_read_errors_total[1m]) / rate(plc_reads_total[1m])

# 通道丢弃率
rate(plc_pipeline_dropped_total[1m])
```

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
| `IDeviceScheduler` | 推送设备配置，差量 reconcile；`GetDeviceHealthAsync()` 查询运行状态 |
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

### 设备级连接超时

`DeviceConfiguration.ConnectionTimeout` 可覆盖全局 `PoolOptions.OperationTimeout`，在获取驱动时优先使用设备级配置。默认 `00:00:05`；设为 `00:00:00`（`TimeSpan.Zero`）时使用全局配置。

断路器状态变更（熔断/半开/恢复）通过 `ILogger` 输出 `Warning`/`Information` 级别日志，每设备独立隔离。

### 健康状态

```csharp
var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
var health = await scheduler.GetDeviceHealthAsync();

foreach (var d in health)
    Console.WriteLine($"{d.DeviceId} [{d.Protocol}] {(d.IsRunning ? "OK" : d.Error)}");
```

返回 `IReadOnlyList<DeviceHealthInfo>`，每个设备包含 `DeviceId`、`Protocol`、`IsRunning`、`Error` 和 `UpdatedAt`。

### 接入 ASP.NET Core HealthChecks（宿主侧适配示例）

基础库只提供健康数据源，`IHealthCheck` 适配由宿主实现（约 10 行）：

```csharp
// 宿主项目：dotnet add package Microsoft.Extensions.Diagnostics.HealthChecks
public sealed class PlcHealthCheck(IDeviceScheduler scheduler) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var health = await scheduler.GetDeviceHealthAsync(ct);
        var down = health.Where(d => !d.IsRunning).ToList();
        return down.Count == 0
            ? HealthCheckResult.Healthy($"devices: {health.Count}")
            : HealthCheckResult.Degraded($"devices down: {string.Join(",", down.Select(d => d.DeviceId))}");
    }
}

// Program.cs
builder.Services.AddHealthChecks().AddCheck<PlcHealthCheck>("plc");
app.MapHealthChecks("/healthz");
```

### 采集推送（OPC UA 订阅）为何不走连接池

轮询类驱动（S7/Modbus/AB/...）的读操作经连接池借用/归还；而 OPC UA 订阅（`IPushProtocolDriver`）使用**专用长连接**——订阅状态（MonitoredItem、发布队列）绑定在驱动实例上，池化借用会破坏订阅语义。因此 `PushCollector` 为每个设备创建独立驱动实例，并拥有自己的连接级弹性管线（`Push:{deviceId}`，含重试/超时/断路器）；该管线随采集器销毁而移除。

## 致谢

本项目基于以下优秀的开源库构建：

| 库 | 用途 | GitHub |
|----|------|--------|
| [S7netplus](https://www.nuget.org/packages/S7netplus) | Siemens S7 通信 | [github.com/S7NetPlus/s7netplus](https://github.com/S7NetPlus/s7netplus) |
| [NModbus](https://www.nuget.org/packages/NModbus) | Modbus 协议 | [github.com/NModbus/NModbus](https://github.com/NModbus/NModbus) |
| [OPCFoundation.NetStandard.Opc.Ua.Client](https://www.nuget.org/packages/OPCFoundation.NetStandard.Opc.Ua.Client) | OPC UA 客户端 | [github.com/OPCFoundation/UA-.NETStandard](https://github.com/OPCFoundation/UA-.NETStandard) |
| [Snet.Mitsubishi](https://www.nuget.org/packages/Snet.Mitsubishi) | 三菱 MC/A1E/FX 协议 | [github.com/shunnet](https://github.com/shunnet)（组织） |
| [NewLife.Omron](https://www.nuget.org/packages/NewLife.Omron) | 欧姆龙 FINS/HostLink 协议 | [github.com/NewLifeX/NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) |
| [EthernetIPSharp](https://www.nuget.org/packages/EthernetIPSharp) | Allen-Bradley EtherNet/IP | [github.com/CristianMori/EthernetIpSharp](https://github.com/CristianMori/EthernetIpSharp) |
| [BACnet](https://www.nuget.org/packages/BACnet) | BACnet 协议栈 | [github.com/ela-compil/BACnet](https://github.com/ela-compil/BACnet) |

## 许可证

MIT
