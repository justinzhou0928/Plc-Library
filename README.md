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
| `OpcUaDriver` | OPC UA | 可用 |
| `MitsubishiDriver` | Mitsubishi MC / A1E / FX | 可用 |
| `OmronDriver` | Omron FINS TCP | 可用 |
| `AllenBradleyDriver` | Allen-Bradley Logix Tag (CIP/EtherNet/IP) | 可用 |
| `BacnetDriver` | BACnet/IP | 可用 |
| `ModbusRtuDriver` | Modbus RTU | 待 NModbus 更新 |
| `ModbusAsciiDriver` | Modbus ASCII | 待 NModbus 更新 |

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

**Modbus 地址格式**：使用 5 位十进制数字前缀标记数据类型：

| 前缀 | 类型 | 读写 | 示例 | 说明 |
|------|------|:--:|------|------|
| `0xxxx` | Coil | 读写 | `00001` | 线圈，布尔量 |
| `1xxxx` | Discrete Input | 只读 | `10042` | 离散输入，布尔量 |
| `3xxxx` | Input Register | 只读 | `30001` | 输入寄存器，16 位无符号整数 |
| `4xxxx` | Holding Register | 读写 | `40042` | 保持寄存器，16 位无符号整数 |

地址为 **1-based**（PLC 习惯），驱动内部自动转换为 0-based 偏移。连续地址自动合并为单次批量读写。

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

**OPC UA**

| 字段 | 默认值 | 说明 |
|------|--------|------|
| endpoint | opc.tcp://localhost:4840 | 服务器端点 |
| username | - | 用户名（可选） |
| password | - | 密码（可选） |
| security | None | None / Sign / SignAndEncrypt |
| timeout | 5000 | 超时 (ms) |
| publishinginterval | 1000 | 订阅发布间隔 (ms) |
| sessiontimeout | 60000 | 会话超时 (ms) |
| autoacceptcertificate | true | 自动接受证书 |

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
| timeout | 3000 | 超时 (ms) |
| localnode | 1 | 本机 FINS 节点号 |
| destinynode | 2 | 目标 FINS 节点号 |
| isudp | false | 使用 UDP 传输 |

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
| timeout | 5000 | 超时 (ms) |
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
| port | 47808 | BACnet/IP 端口 (0xBAC0) |
| timeout | 5000 | 超时 (ms) |
| deviceinstance | 0 | 目标设备实例号（0 表示使用 IP 地址通信） |
| localendpointip | - | 多网卡时指定绑定 IP |

地址格式：`TYPE:INSTANCE`（如 `AV:1` = Analog Value 1、`BI:0` = Binary Input 0）。

支持类型：`AI`/`AO`/`AV`/`BI`/`BO`/`BV`/`MI`/`MO`/`MV`。

示例：`host:192.168.1.50;port:47808;deviceinstance:12345`

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

`DeviceConfiguration.ConnectionTimeout` 可覆盖全局 `PoolOptions.OperationTimeout`，在获取驱动时优先使用设备级配置。默认 `TimeSpan.Zero` 表示使用全局配置。

断路器状态变更（熔断/半开/恢复）通过 `ILogger` 输出 `Warning`/`Information` 级别日志，每设备独立隔离。

### 健康状态

```csharp
var scheduler = host.Services.GetRequiredService<IDeviceScheduler>();
var health = await scheduler.GetDeviceHealthAsync();

foreach (var d in health)
    Console.WriteLine($"{d.DeviceId} [{d.Protocol}] {(d.IsRunning ? "OK" : d.Error)}");
```

返回 `IReadOnlyList<DeviceHealthInfo>`，每个设备包含 `DeviceId`、`Protocol`、`IsRunning`、`Error` 和 `UpdatedAt`。

## 致谢

本项目基于以下优秀的开源库构建：

| 库 | 用途 | GitHub |
|----|------|--------|
| [S7netplus](https://www.nuget.org/packages/S7netplus) | Siemens S7 通信 | [github.com/S7NetPlus/s7netplus](https://github.com/S7NetPlus/s7netplus) |
| [NModbus](https://www.nuget.org/packages/NModbus) | Modbus 协议 | [github.com/NModbus/NModbus](https://github.com/NModbus/NModbus) |
| [OPCFoundation.NetStandard.Opc.Ua.Client](https://www.nuget.org/packages/OPCFoundation.NetStandard.Opc.Ua.Client) | OPC UA 客户端 | [github.com/OPCFoundation/UA-.NETStandard](https://github.com/OPCFoundation/UA-.NETStandard) |
| [Snet.Mitsubishi](https://www.nuget.org/packages/Snet.Mitsubishi) | 三菱 MC/A1E/FX 协议 | [github.com/shunnet](https://github.com/shunnet) |
| [NewLife.Omron](https://www.nuget.org/packages/NewLife.Omron) | 欧姆龙 FINS/HostLink 协议 | [github.com/NewLifeX/NewLife.Omron](https://github.com/NewLifeX/NewLife.Omron) |
| [EthernetIPSharp](https://www.nuget.org/packages/EthernetIPSharp) | Allen-Bradley EtherNet/IP | [github.com/martin-kw/EthernetIPSharp](https://github.com/martin-kw/EthernetIPSharp) |
| [BACnet](https://www.nuget.org/packages/BACnet) | BACnet 协议栈 | [github.com/ela-compil/BACnet](https://github.com/ela-compil/BACnet) |

## 许可证

MIT
