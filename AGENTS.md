# AGENTS.md

本文件面向在此仓库工作的 AI 代理与贡献者，定义必须遵守的规则、命令与文档维护约定。

> 优先级：用户当前指令 > 本文件 > README/DEVELOPMENT。若文档与代码冲突，以代码和测试为准，并立即修正文档。

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
5. **不得留下过期或矛盾文档**：删除/更正旧说法，不在旧内容后追加“已更新”。

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
| `PlcLibrary/` | 核心：`DriverDomain`、`Application`（`IPlcGateway`）、`DriverPool`、`Pipeline`、`Controller`、`General` |
| `PlcLibrary.<Protocol>/` | 协议驱动：Modbus / S7 / OpcUa / Mitsubishi / Omron / AllenBradley / Bacnet |
| `PlcLibrary.Monitor/` | 最新值缓存与变化订阅扩展 |
| `PlcLibrary.Tests/` | xUnit 测试 + `Simulators/` 离线协议服务端 |
| `README.md` | 使用者文档（安装/配置/API/行为） |
| `DEVELOPMENT.md` | 架构、设计决策、驱动开发、测试策略、已知限制 |
| `docs/SCOPE.md` | 库职责 vs SCADA 职责边界 |
| `CHANGELOG.md` | 版本历史（含不兼容变更） |
| `docs/RELEASING.md` | 发布提交、CHANGELOG、annotated tag 规范 |
| `docs/ASSESSMENT-*.md` | 带日期的评估快照，不是当前行为依据 |
| `.github/workflows/ci.yml` | CI（Ubuntu + Windows，restore/build/test/pack） |

## 通用 .NET 规范

### 基本原则

- 遵循现有架构，优先复用已有实现
- 只修改与当前任务相关的代码，不顺手重构或格式化无关代码
- 使用项目目标框架和构建配置实际支持的语法/API
- 未经授权不引入新的 NuGet 包
- 不手工修改自动生成文件，除非任务明确要求且生成流程允许

### 异步

- 异步方法以 `Async` 结尾；接口实现、重写成员和框架入口遵循原签名
- 通用类库代码优先使用 `ConfigureAwait(false)`；宿主/UI 代码按上下文决定
- 不使用 `.Result`、`.Wait()`、`.GetAwaiter().GetResult()` 阻塞异步调用
- 除事件处理器外不使用 `async void`
- 支持取消的异步操作接收 `CancellationToken` 并向下传递，通常放在参数最后
- I/O 优先使用真正的异步 API；不用 `Task.Run` 包装同步 I/O 冒充异步

### 注释

- 新增注释使用简洁中文，末尾不使用中文句号
- 新增/修改的 public/protected 类型和成员按项目要求提供 XML 注释，说明用途、参数和返回值
- 实现接口或重写成员时可用 `<inheritdoc/>`
- 只注释非直观实现和关键约束，重点解释原因，不逐行复述代码
- 修改代码时同步更新相关注释，删除过期历史注释

### 文件编码

- 优先遵循 `.editorconfig` 和工具链要求
- 新建文本文件使用 UTF-8；JSON 默认 UTF-8（无 BOM）
- 修改已有文件保持原编码和换行风格
- 不对无关文件批量转换编码或换行符

### 命名与代码风格

- 类型、方法、属性、事件和常量使用 PascalCase；参数和局部变量用 camelCase
- 私有实例字段用 `_camelCase`；接口名以 `I` 开头
- 启用可空引用类型时正确标注可空性，不滥用 `!`/`null!`
- 方法保持单一职责，优先可读性，不机械拆分
- 短字符串组合优先内插；日志使用结构化消息模板，不用字符串内插
- 不静默忽略未处理异常，不用默认值把失败伪装成成功；重新抛出用 `throw;`，不用 `throw ex;`

### 验证

- 行为变更补充或更新测试
- 使用项目已有命令执行构建、测试和格式检查，不擅自引入新验证工具
- 无法执行验证时明确说明未执行项及原因，不声称验证通过

## 项目代码约定

- `net8.0` / `net10.0` 多目标，遵循 `.editorconfig`（4 空格、CRLF、块命名空间、显式 using）
- 简单构造用主构造函数；复杂初始化用普通构造函数
- 公开契约放 `Interfaces`/`Models`，内部实现放 `Engine`，用顶级 `internal` 类型
- 驱动统一继承 `ProtocolDriverBase<TClient>`；`Connect/Disconnect/Read/Write` 保留协议差异
- 短同步临界区用 `lock`；异步临界区用 `SemaphoreSlim`；周期任务用 `PeriodicTimer`
- 单一实例实现多个接口时用“注册实现 + 转发接口”，不要重复注册出多实例
- 日志只记录错误/异常；热路径不写 Info 日志
- 传输级故障置 `Faulted`（`TransportFailureDetector`）；业务/点位错误返回 `DriverResult.Bad`，不抛异常
- `ReadAsync` 结果必须与输入点位一一对应；不支持的类型返回 `BadConfigError`，不静默回退
- 配置默认值放各自 `*DriverConfig`/`*Options`；单类算法常量用 `private const`；固定且单次使用的值直接内联
- 枚举型连接串值必须 fail-fast：非法字符串在 `*DriverConfig.Parse` 抛 `InvalidOperationException`，空值才使用默认值
- Meter/ActivitySource 标识统一由 `PlcMetrics.Meter` 提供，`PlcActivity` 复用其 `Name`/`Version`

## 测试要求

- 优先**离线协议回环**：本机起真实协议服务端（`Simulators/`），动态端口，`IAsyncDisposable` 清理，不依赖硬件
- 新增驱动至少覆盖：连接/断开/重连、读取类型与顺序、写入回读、非法地址隔离、批量请求次数、错误码映射
- 推送驱动覆盖订阅回调；调度改动覆盖监督重启与停机安全；池改动覆盖并发借用/回收
- 修改池/管道/采集器时补稳定性回归：取消/超时被忽略、信号量泄漏、回收与归还竞态、handler 卡死、队列反压
- 测试专用依赖（如 `OPCFoundation.NetStandard.Opc.Ua.Server`）只能加在测试工程
- 不硬编码端口/文件路径；避免依赖执行顺序或真实时间，必要时轮询等待 + 超时
- 断言“错误行为”的测试必须命名为 `..._DocumentsCurrentBehavior` 并注释原因；修复后改为断言正确行为
- 测试数量会变化，文档不要写死总数；以 `dotnet test` 输出为准

## 文档维护约定

| 文档 | 写什么 | 不写什么 |
|---|---|---|
| `README.md` | 安装、配置、驱动能力、`IPlcGateway` API、已知限制 | 内部实现细节、逐文件测试清单 |
| `DEVELOPMENT.md` | 架构、生命周期、设计决策、驱动开发、测试策略、技术债 | 重复 README 的配置字段大全 |
| `AGENTS.md` | 规则、命令、仓库地图、维护约定 | 复制大段实现说明 |
| `CHANGELOG.md` | 每个版本的新增/修复/变更/测试 | 未发布的冗长过程记录 |
| `docs/RELEASING.md` | 发布步骤、提交格式、annotated tag 模板与检查清单 | 具体版本的历史变更明细 |
| `docs/ASSESSMENT-*.md` | 某次评估的快照、证据、评分、未修复项 | 当作“当前行为”依据 |

规则：

1. 一个事实只写一处，其它文档链接过去
2. 避免“最新/当前/暂未”等易过期措辞；用具体版本、日期或“截至 vX.Y”
3. 行为变更（尤其不兼容）必须同时出现在 README 对应章节与 CHANGELOG
4. 修复完成后删除 README/DEVELOPMENT 中对应的“已知限制/待处理”条目
5. 新评估另建 `docs/ASSESSMENT-YYYY-MM.md`，旧文件保留并标注“历史快照”
6. 提交前检查：无过期数字、无失效链接、无“已废弃但仍在正文”的内容
7. 发布 tag 必须 annotated，message 含版本/日期/摘要/分类变更/验证结果

## 当前限制

不在本文件复制限制清单；统一维护在：

- [DEVELOPMENT.md「已知限制与技术债」](./DEVELOPMENT.md#已知限制与技术债)
- [CHANGELOG.md](./CHANGELOG.md)（2.x 修复与行为变更）
- [历史评估 §5](./docs/ASSESSMENT-2026-11-03.md#5-缺陷与风险清单)

## 提交前清单

- [ ] `dotnet build PlcLibrary.slnx -c Debug` 0 警告 0 错误
- [ ] `dotnet test ...` 全绿（新增/修改用例已覆盖）
- [ ] 公开 API/配置/行为变更已更新 README/DEVELOPMENT/CHANGELOG
- [ ] 已删除或更正文档中的过期说法
- [ ] 未引入测试专用依赖到生产包；未提交 `artifacts/`、`bin/`、`obj/`
