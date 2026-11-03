# AGENTS.md

本文件面向在此仓库工作的 AI 代理与贡献者，定义**必须遵守**的规则、命令与文档维护约定。

> 优先级：用户当前指令 > 本文件 > README/DEVELOPMENT。若文档与代码冲突，以代码与测试为准，并立即修正文档。

## 强制规则

1. **构建 0 警告 0 错误**：
   ```bash
   dotnet build PlcLibrary.slnx -c Debug
   ```
2. **所有测试必须通过**（每次新增/修改代码后）：
   ```bash
   dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug
   ```
3. **新增功能必须补测试**；修复缺陷先写复现用例，再修复。
4. **修改公开 API / 配置项 / 行为**，必须同步更新 `README.md`、`DEVELOPMENT.md`、`CHANGELOG.md`。
5. **不得留下过期或矛盾文档**：删除/更正旧说法，而不是在旧内容后追加"已更新"。

## 常用命令

```bash
# 构建 / 测试
dotnet build PlcLibrary.slnx -c Debug
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug

# 只跑某协议/模块
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~PlcLibrary.Tests.S7"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~PlcLibrary.Tests.Monitor"

# 打包（验证 nupkg 元数据；产物在 artifacts/，不要提交）
dotnet pack PlcLibrary.slnx -c Release -o ./artifacts/pkg
```

## 仓库地图

| 路径 | 内容 |
|---|---|
| `PlcLibrary/` | 核心：`DriverDomain`（契约/模型）、`DriverPool`、`Pipeline`、`Controller`、`General` |
| `PlcLibrary.<Protocol>/` | 协议驱动：Modbus / S7 / OpcUa / Mitsubishi / Omron / AllenBradley / Bacnet |
| `PlcLibrary.Monitor/` | 最新值缓存与变化订阅扩展 |
| `PlcLibrary.Tests/` | xUnit 测试 + `Simulators/` 离线协议服务端 |
| `README.md` | 使用者文档（安装/配置/API/行为） |
| `DEVELOPMENT.md` | 架构、设计决策、驱动开发、测试策略、已知限制 |
| `CHANGELOG.md` | 版本历史（含不兼容变更） |
| `docs/RELEASING.md` | 发布提交、CHANGELOG、annotated tag 的格式规范 |
| `docs/ASSESSMENT-2026-11-03.md` | 2026-11-03 评估快照（当前基线） |
| `docs/ASSESSMENT-2026-11.md` | 2026-11-02 **历史评估快照**，不是当前行为依据 |
| `.github/workflows/ci.yml` | CI（Ubuntu，restore/build/test/pack） |

## 代码约定

- `net8.0`，遵循 `.editorconfig`（4 空格、CRLF、块命名空间、显式 using）。
- 简单构造用主构造函数；复杂初始化用普通构造函数。
- 公开 API（接口/模型/配置）用 `///` XML 注释；内部/私有实现用简洁 `//`。
- 公开契约放 `Interfaces`/`Models`，内部实现放 `Engine`，用顶级 `internal` 类型（不嵌套 `private` 类）。
- 短同步临界区用 `lock`；异步临界区用 `SemaphoreSlim`；周期任务用 `PeriodicTimer`（不用 `System.Threading.Timer`）。
- 单一实例实现多个接口时用「注册实现 + 转发接口」，不要重复注册出多实例。
- 日志只记录错误/异常；热路径不写 Info 日志。
- 传输级故障置 `Faulted`（`TransportFailureDetector`）；业务/点位错误返回 `DriverResult.Bad`，不抛异常。
- `ReadAsync` 结果必须与输入点位一一对应；不支持的类型返回 `BadConfigError`，不要静默回退。
- 只为可能变化或测试/生产不一致的值建立可覆盖来源（配置默认值放各自 `*DriverConfig`/`*Options`，单类算法常量用类内 `private const`）；固定且单次使用的值（协议名、OPC UA 应用名/证书主题）直接内联，不建常量类；Meter/ActivitySource 标识统一由 `PlcMetrics.Meter` 提供（名称 + 程序集版本），`PlcActivity` 复用其 `Name`/`Version`。

## 测试要求

- 优先**离线协议回环**：本机起真实协议服务端（`Simulators/`），动态端口，`IAsyncDisposable` 清理，不依赖硬件。
- 新增驱动至少覆盖：连接/断开/重连、读取类型与顺序、写入回读、非法地址隔离、批量请求次数、错误码映射。
- 推送驱动覆盖订阅回调；调度改动覆盖监督重启与停机安全；池改动覆盖并发借用/回收。
- 修改池/管道/采集器时补稳定性回归：取消/超时被忽略、信号量泄漏、回收与归还竞态、handler 卡死、队列反压；可用短超时 + 假驱动做加速浸泡，不要只测正常路径。
- 测试专用依赖（如 `OPCFoundation.NetStandard.Opc.Ua.Server`）只能加在测试工程。
- 不要硬编码端口/文件路径；避免依赖执行顺序或真实时间（必要时用轮询等待 + 超时）。
- 断言"错误行为"的测试必须命名为 `..._DocumentsCurrentBehavior` 并注释原因；修复后改为断言正确行为。
- 测试数量会变化，**文档不要写死总数**；以 `dotnet test` 输出为准。

## 文档维护约定

| 文档 | 写什么 | 不写什么 |
|---|---|---|
| `README.md` | 使用者需要的安装、配置、驱动能力、API、已知限制 | 内部实现细节、逐文件测试清单 |
| `DEVELOPMENT.md` | 架构、生命周期、设计决策、驱动开发、测试策略、技术债 | 重复 README 的配置字段大全 |
| `AGENTS.md` | 规则、命令、仓库地图、维护约定 | 复制大段实现说明 |
| `CHANGELOG.md` | 每个版本的新增/修复/变更/测试 | 未发布的冗长过程记录 |
| `docs/RELEASING.md` | 发布步骤、提交格式、annotated tag 模板与检查清单 | 具体版本的历史变更明细 |
| `docs/ASSESSMENT-*.md` | 某次评估的快照、证据、评分、未修复项 | 当作"当前行为"依据 |

规则：

1. **一个事实只写一处**，其它文档链接过去。
2. 避免"最新/当前/暂未"等易过期措辞；用具体版本、日期或"截至 vX.Y"。
3. 行为变更（尤其不兼容）必须同时出现在 README 对应章节与 CHANGELOG。
4. 修复完成后，删除 README/DEVELOPMENT 中对应的"已知限制/待处理"条目；不要把已修项留在限制列表。
5. 新评估另建 `docs/ASSESSMENT-YYYY-MM.md`，旧文件保留并标注"历史快照"。
6. 提交前检查：无过期数字（测试数、版本）、无失效链接、无"已废弃但仍在正文"的内容。
7. 发布 tag 必须 annotated，message 含版本/日期/摘要/分类变更/验证结果，禁止 lightweight tag；具体格式见 [docs/RELEASING.md](./docs/RELEASING.md)。

## 当前限制

不要在本文件复制限制清单；统一维护在：

- [DEVELOPMENT.md「已知限制与技术债」](./DEVELOPMENT.md#已知限制与技术债)
- [当前评估 §5](./docs/ASSESSMENT-2026-11-03.md#5-缺陷与风险清单)
- [历史评估 §5](./docs/ASSESSMENT-2026-11.md#5-缺陷与风险清单)（历史证据）

## 提交前清单

- [ ] `dotnet build PlcLibrary.slnx -c Debug` 0 警告 0 错误
- [ ] `dotnet test ...` 全绿（新增/修改用例已覆盖）
- [ ] 公开 API/配置/行为变更已更新 README/DEVELOPMENT/CHANGELOG
- [ ] 已删除或更正文档中的过期说法
- [ ] 未引入测试专用依赖到生产包；未提交 `artifacts/`、`bin/`、`obj/`
