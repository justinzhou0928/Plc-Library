# 开发文档

## 架构总览

```mermaid
%%{init: {'theme': 'neutral'}}%%
flowchart LR
    应用层 -->|ApplyDevicesAsync| 调度层
    调度层 -->|ReadAsync| 连接池层
    连接池层 -->|创建/复用| 驱动层
    调度层 -.->|写入结果| 管道层
    管道层 ==>|fan-out| 应用层

    应用层:::layer
    调度层:::layer
    连接池层:::layer
    驱动层:::layer
    管道层:::layer

    classDef layer fill:none,stroke:#666,stroke-width:1px
```

| 层 | 核心类 | 职责 |
|------|------|------|
| 应用层 | `IDeviceScheduler` `IDataHandler` | 推送设备配置，接收采集数据 |
| 调度层 | `TaskScheduler` `TaskActuator` `TaskSchedulerHost` | 差量 reconcile，定时采集；`TaskSchedulerHost : BackgroundService` 薄封装接管生命周期 |
| 连接池层 | `DeviceDriverPool` `DeviceSharedPool` | 连接复用，Polly 弹性策略 |
| 驱动层 | `IProtocolDriver`（S7 / Modbus / ...） | 协议通信 |
| 管道层 | `DriverResultPipeline` `PipelineHost` | Channel 消费，fan-out 到 Handler；`PipelineHost : BackgroundService` 薄封装接管生命周期 |

分层原则：上层依赖下层，下层不感知上层。`Channel<DriverResult>` 解耦调度层与管道层。

## 运行时序列

```mermaid
%%{init: {'theme': 'neutral'}}%%
sequenceDiagram
    participant App as 应用
    participant S as TaskScheduler
    participant A as TaskActuator
    participant P as DeviceDriverPool<br/>DeviceSharedPool
    participant D as IProtocolDriver
    participant PL as DriverResultPipeline

    Note over App,PL: 启动
    App->>S: ApplyDevicesAsync(devices)
    S->>A: 新建 + StartAsync()

    Note over A,PL: 采集循环
    loop 每个 CollectionInterval
        A->>P: ReadAsync()
        P->>D: Acquire → ReadAsync
        D-->>A: DriverResult[]
        A->>PL: HandleAsync() [Channel.WriteAsync]
    end

    Note over PL: 消费
    PL->>PL: Channel.ReadAllAsync [循环消费]
    PL->>App: IDataHandler.HandleAsync()
```

## 分层职责

### 调度层

`TaskScheduler` 实现 `IDeviceScheduler`，纯业务类，不继承框架基类。`TaskSchedulerHost : BackgroundService` 是薄封装，仅接管宿主生命周期：

```csharp
internal sealed class TaskScheduler : IDeviceScheduler
{
    public async Task ApplyDevicesAsync(...) { /* 差量 reconcile */ }
    internal async Task StopSchedulerAsync() { /* 停止所有 TaskActuator */ }
    internal void DisposeResources() { /* 释放 SemaphoreSlim */ }
}

internal sealed class TaskSchedulerHost(IDeviceScheduler scheduler) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;

    public override async Task StopAsync(CancellationToken ct)
    {
        await ((TaskScheduler)_scheduler).StopSchedulerAsync();
        await base.StopAsync(ct);
    }
}
```

- `ApplyDevicesAsync` — 差量 reconcile：对比当前与目标设备列表，新增/更新/移除对应的 `TaskActuator`
- `TaskSchedulerHost.StopAsync` — 宿主关闭时停止所有 `TaskActuator`

`TaskActuator` 每个设备一个实例，内部 `while + Task.Delay` 采集循环：

```csharp
// 伪代码
while (!ct.IsCancellationRequested)
{
    await Task.Delay(interval, ct);
    var results = await accessor.ReadAsync(device, points, ct);
    await pipeline.HandleAsync(result, ct);
}
```

### 连接池层

`DeviceDriverPool` 实现 `IDeviceAccessor`，对外提供 `ReadAsync` / `WriteAsync`。

`DeviceSharedPool` 按连接键分组，每组维护：

| 组件 | 作用 |
|------|------|
| `SemaphoreSlim` | 限制并发连接数（`MaxConnectionsPerDevice`） |
| `ConcurrentQueue<IProtocolDriver>` | 空闲连接队列，先取后建 |
| `ResiliencePipeline` | Polly 弹性管线（重试 + 超时 + 断路器），每设备独立 |

连接池获取流程：

```mermaid
%%{init: {'theme': 'neutral'}}%%
flowchart TD
    A[AcquireAsync] --> B{空闲队列?}
    B -->|有| C[复用]
    B -->|无| D[CreateAsync]
    D --> E{已连接?}
    E -->|否| F[ConnectAsync<br/>Polly 重试/超时/熔断]
    E -->|是| G[返回]
    F --> G
    C --> G
```

### 管道层

`DriverResultPipeline` 实现 `IDataPipeline`，纯业务类。`PipelineHost : BackgroundService` 是薄封装：

```csharp
internal sealed class DriverResultPipeline : IDataPipeline
{
    public ValueTask HandleAsync(DriverResult result, CancellationToken ct);
    internal async Task ConsumeAsync(CancellationToken ct) { /* await foreach channel */ }
    internal void StopConsuming() { /* channel.Writer.TryComplete */ }
}

internal sealed class PipelineHost(IDataPipeline pipeline) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
        => await ((DriverResultPipeline)pipeline).ConsumeAsync(ct);
}
```

`ConsumeAsync` 内 `await foreach` 消费 `Channel<DriverResult>`，fan-out 到所有 `IDataHandler`。

管道消费：

```mermaid
%%{init: {'theme': 'neutral'}}%%
flowchart LR
    W[TaskActuator] -->|WriteAsync| CH{{BoundedChannel}}
    CH -->|ReadAllAsync| D[DispatchAsync]
    D -->|并行| H1[Handler 1]
    D -->|并行| H2[Handler 2]
```

特性：
- 背压控制：`BoundedChannelFullMode.Wait`，消费者跟不上则阻塞生产者
- Handler 并行度：`SemaphoreSlim(MaxHandlerParallelism)` 限制并发
- Handler 超时：每个 Handler 独立 `CancellationTokenSource(HandlerTimeout)`

## 编写自定义驱动

### 1. 连接字符串绑定

连接字符串 `key:value;key:value` → `ConnectionStringBinder.Bind<T>()`：

```mermaid
%%{init: {'theme': 'neutral'}}%%
flowchart LR
    A["host:192.168.1.1;port:502"] --> B["Parse() → Dictionary"]
    B --> C["Get&lt;T&gt;() → 实例"]
```

```csharp
using PlcLibrary.DriverDomain.Parser;

public sealed record ModbusDriverConfig
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 502;
    public int Timeout { get; init; } = 3000;
    public byte SlaveId { get; init; } = 1;

    public static ModbusDriverConfig Parse(string connectionString)
        => ConnectionStringBinder.Bind<ModbusDriverConfig>(connectionString);
}
```

### 2. 实现 IProtocolDriver

```csharp
using PlcLibrary.DriverDomain.Attributes;
using PlcLibrary.DriverDomain.Enums;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;

[ProtocolDriverName("Modbus")]
public sealed class ModbusDriver : IProtocolDriver
{
    private readonly ModbusDriverConfig _config;

    public ModbusDriver(DeviceConfiguration device)
    {
        _config = ModbusDriverConfig.Parse(device.ConnectionString);
    }

    public DriverStatus DriverStatus { get; private set; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        DriverStatus = DriverStatus.Connecting;
        // 建立连接...
        DriverStatus = DriverStatus.Connected;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        // 断开连接...
        DriverStatus = DriverStatus.Disconnected;
        return Task.CompletedTask;
    }

    public async Task<bool> TryReconnectAsync(CancellationToken ct = default)
    {
        try
        {
            await DisconnectAsync(ct);
            await ConnectAsync(ct);
            return true;
        }
        catch
        {
            DriverStatus = DriverStatus.Faulted;
            return false;
        }
    }

    public async Task<DriverResult[]> ReadAsync(TagPointConfiguration[] points, CancellationToken ct)
    {
        var results = new DriverResult[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            try
            {
                var value = await ReadPointAsync(points[i].Address, ct);
                results[i] = DriverResult.Good(points[i].Address, value);
            }
            catch (Exception ex)
            {
                results[i] = DriverResult.Bad(points[i].Address, QualityCode.BadCommFailure, ex.Message);
            }
        }
        return results;
    }

    public async Task<DriverResult[]> WriteAsync(
        IReadOnlyDictionary<TagPointConfiguration, object> values, CancellationToken ct)
    {
        var results = new DriverResult[values.Count];
        var i = 0;
        foreach (var (point, value) in values)
        {
            try
            {
                await WritePointAsync(point.Address, value, ct);
                results[i] = DriverResult.Good(point.Address, null);
            }
            catch (Exception ex)
            {
                results[i] = DriverResult.Bad(point.Address, QualityCode.BadCommFailure, ex.Message);
            }
            i++;
        }
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
```

### 3. 注册

```csharp
// 协议名从 [ProtocolDriverName] 属性自动获取
builder.Services.AddDriver<ModbusDriver>();

// 自定义连接池分组键（同 IP 不同端口可共用池）
builder.Services.AddDriver<ModbusDriver>(cs =>
{
    var c = ModbusDriverConfig.Parse(cs);
    return $"{c.Host}:{c.Port}";
});
```

## 接口参考

### IProtocolDriver

| 成员 | 返回 | 说明 |
|------|------|------|
| `DriverStatus` | `DriverStatus` | Disconnected / Connecting / Connected / Faulted |
| `ConnectAsync(ct)` | `Task` | 建立物理连接，框架通过 Polly 保护调用 |
| `DisconnectAsync(ct)` | `Task` | 断开连接，不应抛异常 |
| `TryReconnectAsync(ct)` | `Task<bool>` | 断开后重连，失败置 Faulted 并返回 false |
| `ReadAsync(points, ct)` | `Task<DriverResult[]>` | 批量读，结果顺序须与输入一致 |
| `WriteAsync(values, ct)` | `Task<DriverResult[]>` | 批量写，结果顺序与输入一致 |
| `DisposeAsync()` | `ValueTask` | 释放资源（`IAsyncDisposable`） |

### DriverResult

```csharp
public readonly record struct DriverResult
{
    public string DeviceId { get; init; }   // 框架自动注入
    public string TagId { get; init; }      // 框架自动注入
    public string Address { get; init; }    // 驱动填写
    public object? Value { get; init; }     // 驱动填写
    public QualityCode Status { get; init; } // 驱动填写
    public string? ErrorMessage { get; init; }
    public DateTime Timestamp { get; init; } // 框架设定 UTC

    public static DriverResult Good(string address, object? value);
    public static DriverResult Bad(string address, QualityCode status, string error);
}
```

驱动只需填写 `Address`、`Value`、`Status`，`DeviceId` 和 `TagId` 由 `TaskActuator` 通过 `with` 表达式注入。

### QualityCode

| 枚举 | 值 | 场景 |
|------|------|------|
| `Good` | 0x00 | 读取正常 |
| `Uncertain` | 0x40 | 数据可信度低 |
| `BadTimeout` | 0x80 | 操作超时 |
| `BadCommFailure` | 0x81 | 通信失败 |
| `BadConfigError` | 0x82 | 配置错误 |
| `BadDeviceFault` | 0x83 | 设备故障 |
| `BadOutOfService` | 0x84 | 设备停用 |
| `Offline` | 0xC0 | 离线 |
| `Initializing` | 0xC1 | 初始化中 |

## 连接池弹性策略

每个 `DeviceSharedPool` 独立维护一条 Polly `ResiliencePipeline`，配置：

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

策略层级：

策略层级：

```mermaid
%%{init: {'theme': 'neutral'}}%%
flowchart LR
    重试[指数退避重试] --> 超时[OperationTimeout]
    超时 --> CB{断路器}
    CB -->|关闭| OK[执行]
    CB -->|熔断| REJ[拒绝]
```

- **重试**：`OperationCanceledException` 不重试，其他异常指数退避重试最多 3 次
- **超时**：单次连接操作不超过 10 秒
- **断路器**：连续 5 次操作中失败率 ≥ 50% 触发熔断 30 秒

每设备独立的 Pipeline 键为 `Pool:{device.Id}`，故障隔离。
