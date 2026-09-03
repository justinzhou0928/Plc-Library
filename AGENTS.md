# AGENTS.md

本文件面向在此仓库工作的 AI 编码代理与贡献者，定义**必须遵守**的规则与常用命令。

## 强制规则

1. **每次新增代码或完成一项任务后，必须运行测试并确保全部通过**，否则不得视为完成：
   ```bash
   dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug
   ```
2. 构建必须 **0 警告 0 错误**：
   ```bash
   dotnet build PlcLibrary.slnx -c Debug
   ```
3. 新增功能必须同时补充对应的单元/集成测试。
4. 修改公开 API 或新增配置项，必须同步更新 README.md / DEVELOPMENT.md / CHANGELOG.md。

## 常用命令

```bash
# 构建整个解决方案
dotnet build PlcLibrary.slnx -c Debug

# 运行全部测试
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug

# 只跑监控扩展包相关测试
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~PlcLibrary.Tests.Monitor"
```

## 代码约定

- 目标框架 `net8.0`，遵循 `.editorconfig`（4 空格缩进、CRLF、块命名空间、显式 using）。
- **主构造函数优先**（构造逻辑简单时）；复杂初始化用普通构造函数。
- **公开 API**（接口/模型/配置）用 `///` XML 注释；**内部/私有实现**用简洁 `//` 注释，禁止装饰性分隔线（`// =====`）与复述代码的废话注释。
- 公开契约放 `Interfaces`/`Models` 命名空间，内部实现放 `Engine`，且用**顶级 `internal` 类型**（不嵌套 `private` 类）。
- 同步短临界区用 `lock`；异步临界区用 `SemaphoreSlim`；周期任务用 `PeriodicTimer`（**不用** `System.Threading.Timer`）。
- 单一实例实现多个接口时，用「注册实现 + 转发接口」模式，不要 `AddSingleton<TInterface, TImpl>()` 多次注册产生多实例。
- 日志只记录错误/异常，**不要在热路径打 Info 日志**。

## 测试目录

可离线执行（无硬件依赖）：

| 测试文件 | 覆盖 |
|---|---|
| `PlcLibrary.Tests/Monitor/PlcMonitorTests.cs` | 缓存读取/快照、复合键跨设备隔离、去重、质量变化、并发（1000 次同值只通知一次）、取消、TTL 淘汰 |
| `PlcLibrary.Tests/Monitor/MonitorIntegrationTests.cs` | 宿主 DI 装配端到端、去重（不变值不重复推送） |
| `PlcLibrary.Tests/Monitor/ModbusLoopbackIntegrationTests.cs` | 真实 Modbus TCP 回环（本机起 NModbus 从站）：单点变化、设备级订阅、多设备同地址隔离 |

无法离线执行（需真实设备/服务端，标记为后续接入真实设备后补）：

- S7 / OPC UA 真实回环：S7 无官方服务端，OPC UA 起服务端较重。
