# PlcLibrary

工业 PLC 数据采集库：连接池、热更新调度、数据管道、实时值监控与可观测性，一个宿主进程接入多种协议。

- 协议无关驱动接口：新增协议只需实现 `IProtocolDriver` + `AddDriver<T>()`
- 连接池 + Polly 弹性策略（连接级重试/超时/熔断；IO 级重试/超时）
- 设备配置热更新，差量 reconcile；推送型设备失败自动重建
- Channel 管道 fan-out 到多个 `IDataHandler`，支持背压与 handler 超时
- 主动读写 + 自动采集双模式；`PlcLibrary.Monitor` 提供变化订阅缓存
- `System.Diagnostics.Metrics` + `ActivitySource`，通过 OpenTelemetry 接入 Prometheus/Grafana

## 文档地图

| 文档 | 面向 | 内容 |
|---|---|---|
| **README.md**（本文） | 使用者 | 安装、配置、驱动能力、API、监控与可观测性 |
| [DEVELOPMENT.md](./DEVELOPMENT.md) | 贡献者 | 架构、运行时、驱动开发、批量读设计、测试策略、已知限制 |
| [AGENTS.md](./AGENTS.md) | AI 代理/贡献者 | 强制规则、命令、仓库地图、文档维护约定 |
| [CHANGELOG.md](./CHANGELOG.md) | 使用者 | 版本历史与不兼容变更 |
| [docs/ASSESSMENT-2026-11-03.md](./docs/ASSESSMENT-2026-11-03.md) | 评审者 | 2026-11-03 工业级评估（本轮基线） |
| [docs/ASSESSMENT-2026-11.md](./docs/ASSESSMENT-2026-11.md) | 评审者 | 2026-11-02 历史评估快照（非当前行为依据） |

> 行为与配置以 README/DEVELOPMENT 为准；评估报告是**带日期的快照**，其中的"待处理"项可能已经变化。

## 支持的驱动

| 驱动 | `Protocol` 值 | 协议 | 批量读 |
|------|---------------|------|:--:|
| `S7Driver` | `S7` | Siemens S7（S7-200/300/400/1200/1500） | ✅ |
| `ModbusTcpDriver` | `Modbus_TCP` | Modbus TCP | ✅ |
| `ModbusUdpDriver` | `Modbus_UDP` | Modbus UDP | ✅ |
| `ModbusRtuDriver` | `Modbus_RTU` | Modbus RTU（串口） | ✅ |
| `ModbusAsciiDriver` | `Modbus_ASCII` | Modbus ASCII（串口） | ✅ |
| `OpcUaDriver` | `OPC_UA` | OPC UA（客户端 + 订阅推送） | ✅ |
| `MitsubishiDriver` | `Mitsubishi` | Mitsubishi MC / A1E / A3C / FX | ✅ |
| `OmronDriver` | `Omron_FINS` | Omron FINS/TCP | ✅ |
| `AllenBradleyDriver` | `AllenBradley` | Allen-Bradley Logix Tag（CIP/EtherNet/IP） | ✅ |
| `BacnetDriver` | `BACnet` | BACnet/IP | ✅ |

> `DeviceConfiguration.Protocol` 必须与驱动上的 `[ProtocolDriverName]` **完全一致（区分大小写）**。写成 `"OPC UA"`/`"OpcUa"` 不会抛异常，该设备会被跳过并记录 `LogProtocolUnregistered`。
>
> 各驱动的离线验证等级与未覆盖项见 [评估报告](./docs/ASSESSMENT-2026-11-03.md#4-驱动完整性验证矩阵)。

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
dotnet add package PlcLibrary.Monitor
```

## 快速开始

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        TagPoints =
        [
            new TagPointConfiguration { TagId = "t1", Address = "DB21.DBX10.2" },
            new TagPointConfiguration { TagId = "t2", Address = "DB21.DBX10.0" },
        ],
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

### 从 appsettings.json 加载设备

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
        { "TagId": "temp", "Address": "DB1.DBW0" },
        { "TagId": "pressure", "Address": "DB1.DBW2" }
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

## 设备与点位配置

### `DeviceConfiguration`

| 字段 | 默认值 | 说明 |
|---|---|---|
| `Id` | 必填 | 设备唯一标识，参与监控缓存键 |
| `Name` | - | 显示名 |
| `Protocol` | 必填 | 与 `[ProtocolDriverName]` 精确匹配 |
| `ConnectionString` | 必填 | `key:value;key:value`，大小写不敏感 |
| `ConnectionTimeout` | `00:00:05` | 获取驱动（连接池借用）的超时；`00:00:00` 用全局 `OperationTimeout` |
| `CollectionInterval` | `00:00:01` | 轮询周期；推送驱动（OPC UA）无效 |
| `TagPoints` | 空 | 点位列表，`TagId` 必须唯一 |
| `Enabled` | `true` | 为 `false` 时跳过 |

### `TagPointConfiguration`

| 字段 | 默认值 | 说明 |
|---|---|---|
| `TagId` | 必填 | 点位唯一标识（设备内唯一） |
| `Name` | - | 显示名 |
| `Address` | 必填 | 协议地址（见各驱动章节） |
| `DataType` | 空 | 数据类型，空值语义由驱动决定（见下文） |
| `Length` | 0 | 字符串字节长度，仅 `DataType=string` |
| `ByteOrder` | 空 | 多字节字节序，主要供 Modbus 使用（`AB`/`BA`/`ABCD`/`CDAB`/…） |
| `SamplingInterval` | 0 | OPC UA 订阅采样间隔（ms），0 用发布间隔 |
| `QueueSize` | 1 | OPC UA 订阅队列长度 |

### 连接字符串

格式 `key:value;key:value`，大小写不敏感。未知键会被忽略（拼写错误不会报错，需自行核对）。

#### S7

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | 127.0.0.1 | PLC 地址 |
| port | 102 | 端口 |
| rack | 0 | 机架 |
| slot | 0 | 插槽 |
| timeout | 3000 | 读写超时 (ms) |
| cpu | S71200 | `CpuType`（S7200/S7300/S7400/S71200/S71500/...） |

示例：`host:192.168.1.1;port:102;rack:0;slot:1;cpu:S71500`

地址为 S7 标准地址（`DB21.DBX10.2`、`DB1.DBD0`、`M10.0`、`I1.0`、`Q2.0`、`T5`、`C10` 等）。**标量类型由地址推断，`DataType` 不参与**：`DBX`→bool、`DBB`→byte、`DBW`→ushort、`DBD`→uint（`REAL` 也会返回 `uint` 位模式，需要 `float` 时请自行 `BitConverter.UInt32BitsToSingle`）。

S7 STRING 必须带长度：`DB6000.DBB504.100` 读 DB6000 从字节 504 起的 STRING(100)，2 字节头自动解析。配置 `DataType:"string"` 但地址无长度会返回配置错误。

> S7 字符串默认 ASCII；含中文时在宿主启动时设置 `S7.Net.Types.S7String.StringEncoding = Encoding.UTF8;`。

#### Modbus TCP / UDP

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | 127.0.0.1 | 设备地址 |
| port | 502 | 端口 |
| slaveid | 1 | 从站 ID |
| timeout | 3000 | 连接 + 收发超时 (ms) |
| byteorder | - | 设备级默认字节序，点位级可覆盖 |

示例：`host:10.0.0.1;port:502;slaveid:2`

> TCP 在 `ConnectAsync` 中**异步建立**，`timeout` 约束连接与单次收发；失败/超时立即置 `Faulted` 由池重建。

#### Modbus RTU / ASCII（串口）

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | - | 串口号（如 `COM3`） |
| baudrate | 9600 | 波特率 |
| parity | None | None/Odd/Even/Mark/Space |
| databits | 8 | 数据位 |
| stopbits | One | One/OnePointFive/Two |
| timeout | 3000 | 超时 (ms) |
| slaveid | 1 | 从站 ID |
| byteorder | - | 设备级默认字节序 |

示例：`host:COM3;baudrate:19200;parity:Even;stopbits:One;slaveid:1`

串口在 `ConnectAsync` 阶段打开；端口不存在/被占用会立即失败并置 `Faulted`。

#### Modbus 地址格式

`前缀 + 1-based 十进制地址`（1 ~ 65536）：

| 前缀 | 类型 | 读写 | 示例 |
|------|------|:--:|------|
| `0xxxx` | Coil | 读写 | `00001` |
| `1xxxx` | Discrete Input | 只读 | `10042` |
| `3xxxx` | Input Register | 只读 | `30001` |
| `4xxxx` | Holding Register | 读写 | `40042` |

连续/相邻地址自动合并为单次请求，并按 PDU 上限切分（读寄存器 ≤125、线圈/离散输入 ≤2000；写寄存器 ≤123、线圈 ≤1968）。读允许重叠点位共享缓冲区，写遇重叠返回 `BadConfigError`。

`DataType` + `Length` + `ByteOrder` 支持多寄存器值：

```csharp
new TagPointConfiguration
{
    TagId = "barcode",
    Address = "42251",
    DataType = "string",
    Length = 24,          // 字节数（ASCII 下等于字符数），寄存器数 = ceil(24/2) = 12
    ByteOrder = "AB",
}

new TagPointConfiguration
{
    TagId = "temp",
    Address = "42263",
    DataType = "float",
    ByteOrder = "CDAB",   // 42251~42264 会合并为一次请求
}
```

- 每个点位只产出一条 `DriverResult`；不写 `DataType` 时逐寄存器返回 `ushort`
- `string` 必须配置 `Length`，首个 `\0` 截断；单次请求最多读 250/写 246 字节，超长返回 `BadConfigError`
- `ByteOrder` 点位级优先，其次连接串 `byteorder`，默认 `AB`

#### OPC UA

| 字段 | 默认值 | 说明 |
|------|--------|------|
| endpoint | `opc.tcp://localhost:4840` | 服务端端点 |
| username / password | - | 用户名/密码（空则匿名） |
| security | None | None / Sign / SignAndEncrypt；端点实际安全模式低于配置时连接失败，不静默降级 |
| timeout | 5000 | 操作超时 (ms) |
| publishinginterval | 1000 | 订阅发布间隔 (ms) |
| sessiontimeout | 60000 | 会话超时 (ms) |
| autoacceptcertificate | false | 自动接受证书（生产应为 false） |
| pkiownpath | `PlcLibrary.OpcUa/pki/own` | 应用证书目录 |
| pkitrustedpath | `PlcLibrary.OpcUa/pki/trusted` | 受信任对端证书目录 |
| pkiissuerpath | `PlcLibrary.OpcUa/pki/issuer` | 受信任颁发者目录（必填，不能为空） |
| pkirejectedpath | `PlcLibrary.OpcUa/pki/rejected` | 被拒绝证书目录 |

示例：`endpoint:opc.tcp://10.0.0.1:4840;security:None;timeout:10000`

OPC UA 驱动实现 `IPushProtocolDriver`：`ApplyDevicesAsync` 后建立订阅并持续推送，`CollectionInterval` 无效。`Address` 为 `NodeId` 字符串（`ns=2;s=Tag`、`ns=2;i=1234`）；`DataType` 可选，服务端返回类型透传。

> 连接前会校验三项证书存储（应用/对端/颁发者）；即使 `security:None` + 匿名，`pkiissuerpath` 也不能为空，否则连接阶段直接抛 `BadConfigurationError`。四个 PKI 目录需对运行账户可读写；相对路径基于进程工作目录。

#### Mitsubishi

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | 127.0.0.1 | PLC 地址 |
| port | 6000 | 端口 |
| timeout | 3000 | 超时 (ms) |
| protocoltype | MC | MC / A1E / A3C / FX |

示例：`host:192.168.1.1;port:6000;protocoltype:MC`

地址：`D100`、`M100`、`X10`、`Y20`、`W0`、`ZR100` 等（取决于协议类型）。

**字软元件上的 bool（`D`/`W`/`R`/`ZR`）**：按「读字 + 取 bit」实现，写入走「读字 → 改位 → 回写字」，避免 Snet 直接发位命令落到错误位：

| 配置 | 语义 |
|---|---|
| `D110` + `DataType=bool` | 等价 `D110.0`（bit 0） |
| `D110.5` + `DataType=bool` | `D110` 的 bit 5（0–15，超出返回 `BadConfigError`） |
| `M110` + `DataType=bool` | 位软元件，直接位访问 |

> 该语义自 1.3.0 起生效，属于**不兼容变更**；需要整字真值请用 `DataType=ushort` 后自行比较。

#### Omron FINS

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | 127.0.0.1 | PLC 地址 |
| port | 9600 | 端口（仅 TCP） |
| timeout | 3000 | 连接/接收超时 (ms) |
| localnode / destinynode | 1 / 2 | 已解析但当前无效（底层节点号由握手分配） |
| isudp | false | 已解析但当前无效（FINS 客户端仅支持 TCP） |

地址示例：DM `D100`；CIO `CIO200`/`CIO200.5`；WR `W100`；HR `H10`；AR `A0`。

字/位点位自动按存储区合并为少量请求；`DataType` 决定分组与读取长度。

#### Allen-Bradley / Rockwell

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | 127.0.0.1 | PLC/网关地址 |
| port | 44818 | EtherNet/IP 端口 |
| timeout | 5000 | 连接/读写超时 (ms) |
| path | - | 路由路径（`1,0` = 背板槽位 0） |
| useconnected | false | Class 3 连接（高频轮询推荐开启） |

地址为 Logix 标签名：`rate`、`counts[3]`、`Temp[10].AnotherArray[4]`、`MyUdt.enable`、`matrix[1,2,3]`。

示例：`host:192.168.1.96`、`host:192.168.1.96;path:1,0`

#### BACnet

| 字段 | 默认值 | 说明 |
|------|--------|------|
| host | 127.0.0.1 | 目标设备 IP（可写 `ip:port` 覆盖远端端口） |
| port | 47808 | BACnet/IP 端口（本地监听；远端直连时也使用） |
| timeout | 5000 | WhoIs 设备发现等待 (ms)；读/写使用底层默认超时 |
| deviceinstance | 0 | 0 = 直连 host；>0 = WhoIs/IAm 发现该实例 |
| localendpointip | - | 多网卡时指定绑定 IP |

地址：`TYPE:INSTANCE`（`AV:1`、`BI:0`）；类型 `AI/AO/AV/BI/BO/BV/MI/MO/MV`。

示例：`host:192.168.1.50;port:47808;deviceinstance:12345`

### `DataType` 支持

| 驱动 | 是否必需 | 未指定时 | 说明 |
|------|:--:|------|------|
| S7 | 否（string 除外） | 地址推断 | 标量由地址推断；STRING 需地址带长度 |
| Modbus | 否 | 地址前缀 | `0xxxx`=bool、`4xxxx`=ushort；配置后解码多寄存器值 |
| OPC UA | 否 | 服务端类型 | 值透传 |
| BACnet | 否 | 服务端类型 | `PROP_PRESENT_VALUE` 原始值 |
| AllenBradley | 推荐 | `int` | 决定 `ReadAsync<T>` 泛型；未知类型 `BadConfigError` |
| Mitsubishi | 推荐 | `int` | 映射 Snet 标量/数组类型；未知类型 `BadConfigError` |
| Omron | 推荐 | `int` | 决定字/位分组与长度；未知类型 `BadConfigError` |

支持值（大小写不敏感）：`bool`、`short`、`ushort`、`int`、`uint`、`long`、`ulong`、`float`、`double`、`string`，以及 `int16`/`uint16`/`int32`/`uint32`/`int64`/`uint64`/`real`/`lreal`/`dint`/`udint`/`lint`/`ulint`/`word`/`dword`/`lword` 等别名。

## 驱动池、管道与弹性

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
  },
  "Pipeline": {
    "Capacity": 10000,
    "MaxHandlerParallelism": 8,
    "HandlerTimeout": "00:00:30",
    "EnqueueTimeout": "00:00:05"
  }
}
```

| 级别 | 作用域 | 策略 | 触发位置 |
|---|---|---|---|
| 连接级 | `ConnectAsync` / `TryReconnectAsync` | 重试 + 超时 + 断路器（每池独立） | `DeviceSharedPool.AcquireAsync` |
| IO 级 | `ReadAsync` / `WriteAsync` | 重试 + 超时（进程级共享） | `DeviceDriverPool.ReadAsync/WriteAsync` |

- 连接级断路器默认 5 次、失败率 ≥50%、冷却 30s；状态变更走 `ILogger`
- 驱动捕获传输级故障时置 `Faulted`，池归还时丢弃并按需重建
- 推送驱动失败由调度器监督循环按指数退避重建（5s 扫描，最长 60s 退避）
- **卡死看门狗**：轮询采集超过 `max(4 × 采集周期, 30s)` 无进展、推送采集 5 分钟无数据时，监督循环会停止并按退避重建执行器；健康状态返回 `Faulted`。注意：推送驱动的“无数据”包含“点位长期无变化”，稳定工况可能被误判为卡死（见「已知限制」）
- **IO 硬超时**：Polly 超时之后还有 2s 兜底；底层调用忽略取消令牌时也会返回，驱动被丢弃而不是放回池
- **管道硬超时**：handler 超时是硬上限，忽略取消也不会卡住分发；写入队列最多等待 `EnqueueTimeout`（默认 5s），消费者不前进时丢弃该点并记录 `plc.pipeline.dropped`。handler 不应在返回 `ValueTask` 之前同步阻塞（否则会占住消费者，需自行改为异步）
- IO 级只对**抛异常**的读写重试；驱动通常把点位错误转为 `DriverResult.Bad`，不触发重试
- `PoolIdleTimeout`：空置池超过该时长且无在途借用时销毁；`00:00:00` 禁用
- `MaxRetryAttempts` 必须 ≥ 1；`PoolOptions` / `PipelineOptions` / `MonitorOptions` 的 DataAnnotations 在宿主启动时校验，非法值直接阻止启动（不再等到首次下发设备才抛 Polly 异常）

## 批量读能力

| 驱动 | 说明 |
|------|------|
| Modbus | 连续地址合并 + PDU 上限自动切分（组级隔离） |
| S7 | `ReadMultipleVarsAsync` 批量 + 逐点回退 |
| OPC UA | 单请求批量 Read；订阅逐点回调 |
| Mitsubishi | 同设备/同类型连续地址合并为一次数组读（`Int16Array`/`FloatArray`/…）；字软元件 bool 按字数组 + 取位；位软元件逐点 |
| Omron | 同存储区连续字合并为一次 `Read`、连续位合并为一次 `ReadBit`（组级隔离） |
| AllenBradley | `ReadMultipleAsync` 原始字节批量 + CIP 小端解码（string 逐点） |
| BACnet | `ReadPropertyMultipleAsync` 按对象分组 |

## 实时值监控（PlcLibrary.Monitor）

```bash
dotnet add package PlcLibrary.Monitor
```

```csharp
builder.Services
    .AddPlcLibrary()
    .AddDriver<S7Driver>()
    .AddPlcMonitor();   // 或 AddPlcMonitor(builder.Configuration) 绑定 "Monitor" 节
```

`PlcMonitor` 以 `IDataHandler` 挂接管道，按 `(DeviceId, TagId)` 缓存最新值，只推送值或质量状态变化的点位：

```csharp
public sealed class MyService(IPlcMonitor monitor)
{
    public DriverResult? Read(string deviceId, string tagId) => monitor.Get(deviceId, tagId);

    public async Task WatchAsync(CancellationToken ct)
    {
        await foreach (var r in monitor.SubscribeAsync("plc-001", "t1", ct))
            Console.WriteLine($"[{r.DeviceId}] {r.Address} = {r.Value}");
    }

    public async Task WatchDeviceAsync(CancellationToken ct)
    {
        await foreach (var r in monitor.SubscribeDeviceAsync("plc-001", ct))
            Console.WriteLine($"[{r.TagId}] {r.Address} = {r.Value}");
    }
}
```

- 缓存键 `DeviceId + TagId`：不同设备同 TagId/同地址互不干扰
- 订阅先返回当前快照，之后只在变化时产出
- 订阅通道容量 1（latest-wins）：消费慢时只保证最新值，中间值可能跳过
- `Monitor.EntryIdleTimeout`（默认 5 分钟，`00:00:00` 禁用）清理长期无数据的条目
- 指标：`plc.monitor.updates`、`plc.monitor.changes`（共用 Meter `PlcLibrary`）

## 可观测性

Meter `PlcLibrary`，读/写/获取指标带 `device.id`、`device.protocol`，管道指标带 `device.id`：

| 指标 | 类型 | 说明 |
|---|---|---|
| `plc.reads.total` | Counter\<long\> | 累计读取点位数 |
| `plc.read.duration` | Histogram\<double\> | ReadAsync 耗时（含 IO 重试） |
| `plc.read.errors` | Counter\<long\> | ReadAsync 最终失败次数（仅异常，不含 Bad 结果） |
| `plc.write.total` | Counter\<long\> | 写操作数 |
| `plc.acquire.duration` | Histogram\<double\> | 获取驱动耗时（含连接） |
| `plc.pipeline.dispatched` | Counter\<long\> | 管道分发点数 |
| `plc.pipeline.dropped` | Counter\<long\> | 管道写入队列超时丢弃点数（订阅通道 `DropOldest` 不做逐点计数） |

`ActivitySource("PlcLibrary")` 的 Span：`PlcLibrary.ReadAsync`、`PlcLibrary.WriteAsync`、`PlcLibrary.Acquire`、`PlcLibrary.Dispatch`（标签见 DEVELOPMENT）。

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("PlcLibrary").AddPrometheusExporter())
    .WithTracing(t => t.AddSource("PlcLibrary").AddOtlpExporter());
```

Grafana 查询示例：

```promql
rate(plc_reads_total[1m])
histogram_quantile(0.99, rate(plc_read_duration_seconds_bucket[1m]))
rate(plc_read_errors_total[1m]) / rate(plc_reads_total[1m])
rate(plc_pipeline_dropped_total[1m])
```

## API

| 接口 | 说明 |
|------|------|
| `IDeviceScheduler` | 推送设备配置（差量 reconcile）、查询健康状态 |
| `IDataHandler` | 接收采集结果 |
| `IDeviceAccessor` | 主动读写设备 |
| `IProtocolDriver` | 协议驱动实现 |
| `IPushProtocolDriver` | 推送型驱动（订阅/回调） |
| `IDriverFactory` | 驱动工厂（通常由 `AddDriver<T>` 生成） |
| `IDataPipeline` | 数据管道（一般无需直接使用） |
| `IPlcMonitor` | 实时值缓存与变化订阅（Monitor 包） |

```csharp
public class MyService(IDeviceAccessor accessor)
{
    public async Task ReadDevice(DeviceConfiguration device)
        => await accessor.ReadAsync(device, device.TagPoints);

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

### 健康状态

```csharp
var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
var health = await scheduler.GetDeviceHealthAsync();

foreach (var d in health)
    Console.WriteLine($"{d.DeviceId} [{d.Protocol}] {(d.IsRunning ? "OK" : d.Error)}");
```

`DeviceHealthInfo` 包含 `DeviceId`、`Protocol`、`IsRunning`、`Error`、`UpdatedAt`；`Error` 来自执行器最近一次异常。

ASP.NET Core HealthChecks 适配（宿主实现，约 10 行）：

```csharp
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

builder.Services.AddHealthChecks().AddCheck<PlcHealthCheck>("plc");
app.MapHealthChecks("/healthz");
```

### 为什么 OPC UA 订阅不走连接池

轮询驱动按次借用连接；OPC UA 订阅状态（MonitoredItem、发布队列）绑定驱动实例，池化会破坏订阅语义。因此 `PushCollector` 为每台设备创建独立驱动与连接级弹性管线，随采集器释放。详见 [DEVELOPMENT.md](./DEVELOPMENT.md)。

## 已知限制与后续计划

| 限制 | 说明 |
|---|---|
| OPC UA 可写节点 | 离线回环使用 SDK 标准节点（读/订阅已覆盖）；自定义 NodeManager 的可写语义需真实服务端确认 |
| S7 标量 `DataType` | 不参与解码，`DBD` 返回 `uint`（`REAL` 需自行按位转换） |
| Mitsubishi 批量写 | 底层库只返回整体结果，部分点失败时整批标 `Bad` |
| BACnet 读/写超时 | `timeout` 仅用于 WhoIs 发现，读写依赖底层默认 |
| 共享池配置 | 同连接串多设备共享池，池按首个设备的 `ConnectionTimeout` 创建 |
| 推送看门狗 | OPC UA 点位 5 分钟无变化会被判为“无进展”并重建订阅；稳定工况需评估该抖动 |
| 串口物理层 | 离线仅覆盖参数映射与失败路径，RTU/ASCII 帧需真机/虚拟串口对 |
| 厂商语义 | 字节序、位地址等以模拟器 + 规范为准，建议上线前做一次真机联调 |

未修复项、优先级与验证矩阵见 [docs/ASSESSMENT-2026-11-03.md](./docs/ASSESSMENT-2026-11-03.md)；工程决策与测试策略见 [DEVELOPMENT.md](./DEVELOPMENT.md)。

## 致谢

| 库 | 用途 |
|----|------|
| [S7netplus](https://github.com/S7NetPlus/s7netplus) | Siemens S7 |
| [NModbus](https://github.com/NModbus/NModbus) | Modbus |
| [OPCFoundation.NetStandard.Opc.Ua.Client](https://github.com/OPCFoundation/UA-.NETStandard) | OPC UA |
| [Snet.Mitsubishi](https://www.nuget.org/packages/Snet.Mitsubishi) | Mitsubishi MC/A1E/A3C/FX |
| [NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) | Omron FINS |
| [EthernetIPSharp](https://github.com/CristianMori/EthernetIpSharp) | Allen-Bradley EtherNet/IP |
| [BACnet](https://github.com/ela-compil/BACnet) | BACnet 协议栈 |

## 许可证

MIT
