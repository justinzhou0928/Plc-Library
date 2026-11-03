# PlcLibrary 工业级质量评估报告

> **历史快照（2026-11-02，基线提交 `c348490`）**：本报告记录当时的评估、修复与验证状态，不是当前行为依据。当前行为以 [README](../README.md) / [DEVELOPMENT](../DEVELOPMENT.md) 为准；未修复项以 DEVELOPMENT「已知限制与技术债」为最新清单。2026-11-03 的新评估见 [ASSESSMENT-2026-11-03.md](./ASSESSMENT-2026-11-03.md)。

| 项 | 内容 |
|---|---|
| 评估对象 | `PlcLibrary`（PLC 数据采集库，9 个 NuGet 包 + 1 个测试工程） |
| 基线提交 | `c348490`（v1.2.0，工作区初始状态干净） |
| 评估日期 | 2026-11-02 |
| 评估方式 | 全量源码静态审查（75 个生产文件 / 5,239 行）+ 构建/测试实测 + 6 套协议服务端端到端验证 |
| 本次评估产出 | 本报告 + 72 个新增离线测试 + 15 项缺陷修复/加固 + 6 项性能与结构优化 |

---

## 0. 结论速览

**总体评分：8.6 / 10（A−）** —— 架构分层清晰、工程规范到位、**8 类协议（含 OPC UA）全部具备离线数据面验证**；本轮共修复/加固 15 项缺陷（含 4 处"Good + 错误数据"、3 项高危韧性/协议缺陷），并完成 Omron/Mitsubishi 批量读、Modbus 异步连接、管道/监控/池清理等性能优化。剩余风险主要是**厂商语义需真机确认**、**共享池/连接串配置边界**与**少量 P3 工程化项**。**结论：核心与 7 个协议驱动可进入生产试用；OPC UA 已具备真实服务端回环（标准节点），自定义可写节点语义建议真机确认后再用于生产。**

| 维度 | 评分 | 结论 |
|---|---|---|
| 架构与分层 | 8.7 | 契约/实现分离清晰；本轮移除全局管线注册表，`Controller` 不再依赖 `DriverPool.Engine` 内部类型 |
| 协议驱动完整度 | 8.6 | 8 类协议均有离线回环；Omron/Mitsubishi 已支持批量读 |
| **数据正确性** | 8.7 | 4 处静默错值已修复并有回归；未知 `DataType` 显式报错；Mitsubishi 位语义按规范修正（待真机确认） |
| 并发与资源管理 | 8.3 | 监控 CAS 单字典；池回收竞态已用原子标记 + 重试修复 |
| 容错与恢复 | 8.5 | 推送失败监督重启、串口失败即 Faulted、AB 超时生效、停机安全；IO 级仍无熔断 |
| 可观测性 | 6.5 | Metrics + Activity 覆盖主要路径；缺 Bad 质量计数与重连计数 |
| 测试 | 8.8 | **414 个用例、6 套真实协议服务端**（含 OPC UA）；串口物理层缺回环 |
| 文档与发布 | 8.0 | README/DEVELOPMENT 详实并标注局限；缺 XML 文档、CI 仅 Ubuntu |

### 本轮修复/优化一览

| 类型 | 内容 | 验证 |
|---|---|---|
| 数据正确性 | AB 批量读 CIP 类型码、Mitsubishi 响应键、BACnet 逐对象错误/缺项 | 回归测试 |
| 可用性 | 推送设备监督重启、宿主停机安全、串口失败即 Faulted、AB 超时、池回收竞态 | 回归测试 |
| 协议正确性 | BACnet 远端端口/读写 arrayIndex；Mitsubishi 字软元件位语义；未知 DataType 显式报错 | 回归测试 + 帧断言 |
| 性能 | Omron 字/位批量读、Mitsubishi 数组批量读、Modbus TCP 异步连接、管道快路径、监控 CAS | 请求数/帧断言 + 全量测试 |
| 精简 | 删除弹性管线全局注册表、池清理改用 `PeriodicTimer` | 全量测试 |

### 剩余 Top 5 风险

1. **S7 标量忽略 `DataType`**（`DBD` → `uint`），与 README 示例的 `Real` 期望不一致（§5 P2-4）。
2. **Mitsubishi 批量写部分成功仍全部标 Bad**：Snet 只返回整体结果，重试存在非幂等风险（§5 P2-5）。
3. **厂商语义需真机确认**：Mitsubishi 位语义、S7 字节序、BACnet 严格设备兼容性（§5 P2-13）。
4. **共享池使用首个设备配置 + 连接串拼写静默忽略**：多设备共享同一连接串时设备级超时可能被忽略（§5 P2-9/P2-10）。
5. **OPC UA 自定义节点空间未验证**：回环使用 SDK 标准节点覆盖会话/读/订阅，自定义 NodeManager 的可写节点语义仍需真实服务端确认（§4）。

---

## 1. 评估范围与方法

### 1.1 范围

- 核心：`PlcLibrary`（驱动契约、连接池、弹性策略、调度、数据管道、配置解析、可观测性）。
- 扩展包：`Modbus`（TCP/UDP/RTU/ASCII）、`S7`、`OpcUa`、`Mitsubishi`、`Omron`、`AllenBradley`、`Bacnet`、`Monitor`。
- 测试工程与 CI/打包配置。

### 1.2 方法

1. **静态审查**：逐文件阅读全部生产代码，重点检查并发、状态机、异常路径、资源释放、协议编解码、配置消费闭环。
2. **构建/测试基线**：`dotnet build`、`dotnet test`、`dotnet pack` 实测。
3. **协议模拟器端到端验证**（全部离线、动态端口）：
   - Modbus TCP/UDP：NModbus 真实从站；RTU/ASCII 覆盖失败路径。
   - S7：自建 ISO-on-TCP + COTP + S7comm 从站（Setup Communication / Read Var / Write Var）。
   - Omron FINS：`NewLife.Omron.FinsServer`（协议库自带真实服务端）。
   - Mitsubishi MC：自建 MC 3E 二进制从站（0x0401/0x1401，D 字 / M 位）。
   - Allen-Bradley：自建 EtherNet/IP 封装 + CIP Logix Tag 从站（含 Forward_Open）。
   - BACnet/IP：`System.IO.BACnet.BacnetClient` 服务端模式（RMP/ReadProperty/WriteProperty + WhoIs/IAm）。
   - 通过"抓包 → 实现应答 → 断言"确认第三方库的真实线格式，而非按文档猜测。
4. **并行隔离**：Mitsubishi / Allen-Bradley / BACnet 模拟器在独立 `git worktree` 中开发验证，完成后在主工作区复跑。
5. **反向验证**：每个缺陷先写复现测试，再修复；修复后的测试断言正确行为，并以帧/值级证据锁定。

### 1.3 明确的局限

- 全部为**回环/模拟器**验证，未经真实 PLC 联调；能证明"驱动按库与规范的语义工作"，不能替代真机确认（尤其字节序、位地址、厂商扩展）。
- OPC UA 回环使用 SDK **标准节点空间**（`ServerStatus`/`CurrentTime`），覆盖会话、读、订阅与只读节点写失败；自定义 NodeManager 的可写节点语义未覆盖。
- Modbus RTU/ASCII 未做物理层回环（需真实/虚拟串口），仅覆盖参数映射与失败路径。
- 未做长时间稳定性/压力浸泡（仅现有并发、背压、10k 管道压力用例）。

---

## 2. 构建与测试实测

| 项 | 结果 |
|---|---|
| Debug 构建 | **0 警告 / 0 错误**（多次复跑） |
| 测试基线（改动前） | 342 通过 / 0 失败 / 9.3s |
| 测试现状（改动后） | **414 通过 / 0 失败 / ~7s**（+72） |
| 新增离线服务端 | 6 套（S7 / MC / EtherNet-IP / BACnet / Modbus 回环 / OPC UA） |
| `dotnet pack` | 成功产出 9 个 nupkg |
| CI 配置 | Ubuntu-only；`restore → build(Release) → test → pack` |

**评价**：测试"快、全离线、无固定端口"是核心资产；缺口是 CI 无 `-warnaserror`、无覆盖率门禁、无 Windows job（串口/注册表路径不会被 CI 覆盖）。

---

## 3. 架构评估

### 3.1 模块划分（优秀）

```
PlcLibrary（核心）
├── DriverDomain     IProtocolDriver / IPushProtocolDriver / IDeviceAccessor / DriverResult / QualityCode
├── DriverPool       DeviceDriverPool → DeviceSharedPool（按 协议|连接串 共享，自持 Polly 管线）
├── Pipeline         DriverResultPipeline（有界 Channel + 多 IDataHandler 扇出）
├── Controller       TaskScheduler（热更新 reconcile + 监督重启）→ TaskActuator → Polling/PushCollector
└── General          配置模型、Metrics、ActivitySource、日志
PlcLibrary.<Protocol>  每个协议一个包，依赖注入 AddDriver<T>()
PlcLibrary.Monitor     缓存最新值 + 变化订阅（IDataHandler 挂件）
```

**优点**

- **契约稳定**：`IProtocolDriver` 只要求 `Connect/Disconnect/TryReconnect/Read/Write`，新增协议只需 `[ProtocolDriverName]` + `AddDriver<T>()`。
- **推送与轮询分离**：`IPushProtocolDriver` + `SupportsPush`，调度器按工厂能力分流。
- **单一实例多接口转发**：符合 AGENTS.md 约定，避免多实例。
- **弹性策略分层**：连接级（重试+超时+熔断，每池独立）与 IO 级（重试+超时）职责分明。
- **热更新 reconcile**：`_applyLock` 串行化，按 `Id` 增量启停，配置变更检测覆盖关键字段。
- **本轮新增**：采集任务监督循环（失败重启 + 指数退避），把"驱动失败"从"设备僵死"降级为"可自愈"。

### 3.2 架构层面的主要短板

1. **缺乏驱动基类**：5 个驱动重复实现同一套状态机（`_stateLock`/`SetState`/`GetXOrThrow`/`MarkFaultedIfTransport`/`TryReconnectAsync`），共 53 处 `_stateLock` 引用、20 处 `SetState`、20 处 `MarkFaultedIfTransport`；语义漂移的温床。
2. **驱动错误分类不统一**：同类错误在不同驱动映射不同 `QualityCode`（BACnet 无效地址 → `BadCommFailure`，Modbus → `BadConfigError`）。
3. **宿主包装仍是"伪后台服务"**：`TaskSchedulerHost` 现在承载监督循环（已改善），但 `PipelineHost` 仍以覆写 `StopAsync/Dispose` 的方式管理生命周期。
4. **配置绑定对拼写错误零反馈**：`ConnectionStringBinder` 忽略未知键（§5 P2-12）。

---

## 4. 驱动完整性验证矩阵

图例：**A** = 关键路径有真实报文端到端验证；**B** = 主要路径已验证、个别分支缺失；**C** = 仅配置/失败路径；**D** = 仅静态检查。

| 驱动 | 协议 | 等级 | 已离线验证 | 未验证/缺口 |
|---|---|---|---|---|
| `ModbusTcpDriver` | Modbus TCP | **A** | 连接、批量读、多寄存器类型/字节序、批量写、重叠拒绝、错误隔离、断链重连、真实回环读写 | 大批量 PDU 切分（单测覆盖）、慢设备超时 |
| `ModbusUdpDriver` | Modbus UDP | **A−** | 真实 UDP 回环读 + 写回从站 | 断线重连、丢包重试 |
| `ModbusRtuDriver`/`AsciiDriver` | 串口 | **C** | 参数映射、端口不可用即失败（本轮修复） | RS-485 帧/CRC 端到端（需硬件/虚拟串口） |
| `S7Driver` | S7comm | **A−** | 握手、批量读（单请求）、int/real/bit/string 解码、单点/批量写回、非法地址隔离（写也有回归）、断链→`Faulted`→重连恢复 | 大 PDU 分批、`DataType` 未生效（标量按地址推断） |
| `OmronDriver` | FINS/TCP | **A−** | 握手、**字/位批量读（8 点 → 2 请求）**、int16/uint16/float/bool 读、写回、PLC 错误码映射、断开行为、配置项生效性 | UDP 不支持（README 已标注）、内部 `AutoReconnect` 与池重试叠加 |
| `MitsubishiDriver` | MC 3E | **A−** | 连接/断开、**数组批量读（5 连续 short / 3 连续 float 各 1 请求）**、int16/uint16/float/bool 读、字/位写、越界与设备错误隔离、键匹配回归、**字软元件 bool 位语义** | A1E/A3C/FX 未覆盖、部分写失败语义、真机位语义确认 |
| `AllenBradleyDriver` | EtherNet/IP+CIP | **A−** | RegisterSession、CIP 批量读（类型码）、逐点回退、写、字符串、**timeout 生效**、Connected 模式 | 数组/UDT/分片、非空 backplane path |
| `BacnetDriver` | BACnet/IP | **B** | WhoIs/IAm 发现、RMP 批量读、写、逐对象错误/缺项降级、**远端端口/arrayIndex** | 严格设备兼容性、COV 订阅、BV 真实编码（枚举） |
| `OpcUaDriver` | OPC UA | **B+** | **真实服务端回环**：会话、批量读（标准节点 Good / 未知节点 Bad）、只读节点写 → Bad、订阅推送回调；配置/证书/订阅构造 | 自定义 NodeManager 可写节点、重连风暴、长稳 |

### 4.1 关键线格式证据（摘录）

- **S7**：首帧 `03 00 00 16 | 11 E0 ...`（TPKT+COTP CR），随后 Setup Communication `F0`；2 个非字符串点位合并为 **1 次** Read Var（`ReadRequestCount == 1`）。
- **Mitsubishi**：MC 3E 头 `50 00 | 00 | FF | FF 00 | 00 | <len> 00 | 0A 00`，命令 `04 01`/`14 01`，子命令 `00 00`（字）/`00 01`（位），3 字节 head + 设备码（D=0xA8、M=0x90）+ 2 字节点数。字软元件 bool 读 = `… 04 01 00 00 6E 00 00 A8 01 00`（字读），写 = `… 14 01 00 00 6E 00 00 A8 01 00 03 00`（读改写后的字写）。**连接/断开不发送任何帧**。
- **Allen-Bradley**：批量读为 `SendRRData(0x6F)` 内嵌 CIP `0x0A`，子请求 `4C 05 91 <len> <tag> 01 00`；成功应答 `8A 00 00 00 … CC 00 00 00 <CIP 类型码 2 字节> <数据>`（类型码偏移是 P0 根因）。
- **BACnet**：RMP 请求尾部 `… 1E 09 55 19 FF FF FF FF`（本轮改为 `BACNET_ARRAY_ALL`）；直连地址 `ip:port` 生效，模拟器绑定 127.0.0.2 的临时端口即可被访问。
- **Omron**：握手 20 字节 `FINS 00 00 00 0C 00000000 00000000 00000000`，服务端回 24 字节；随后每点一帧 FINS `01 01`。

---

## 5. 缺陷与风险清单

> 状态：**已修复** = 本轮提交代码 + 回归测试；**待处理** = 保留复现/说明，列入路线图。

### 5.1 已修复（本轮）

| ID | 严重度 | 问题 | 修复与证据 |
|---|---|---|---|
| P0-1 | 高 | **AB 批量读整体错位**：应答含 2 字节 CIP 类型码，原实现从 byte0 解码 → DINT/REAL 错位、BOOL 恒 true，仍标 `Good` | 跳过并校验类型码，类型不匹配返回 Bad；`ReadAsync_NumericBatch_DecodesCipTypeCodeAndReturnsCorrectValues`、`..._BatchDataTypeMismatch_...` |
| P0-2 | 高 | **Mitsubishi 串点**：先按 TagId 查字典，TagId 与另一点位地址同名时返回别的点位值（`Good`） | Address 优先 + 冲突守卫；`ReadAsync_TagIdEqualsAnotherPointsAddress_DoesNotCrossWireValues` |
| P0-3 | 高 | **BACnet 逐对象错误/缺项**：`BacnetError` 与缺失对象都返回 `Good`（缺项甚至是 `Address=null` 的默认值） | 逐对象错误降级 Bad、缺项 `BadCommFailure("No response for object")`；两条回归 |
| P1-1 | 高 | **推送设备 Connect 失败**：执行器永久僵死、不重试；`StopAsync` 抛异常导致宿主停机失败 | `TaskActuator` 全异常捕获 + `LastError`；调度器监督循环指数退避重建；停机先取消监督；3 条回归（恢复/持续重试/停机干净） |
| P1-2 | 高 | **BACnet 远端端口被忽略**：`BacnetAddress(IP,address,network)` 第三参数是网络号，传 port 无效，永远连 47808 | 端口编码进地址（`ip:port`），测试改用正常 host+port 字段 |
| P1-3 | 高 | **BACnet 写把 timeout 当 arrayIndex**（参数顺序 `invokeId, priority, arrayIndex, ct`） | 传 `BACNET_ARRAY_ALL`；`WriteAsync_UsesArrayAllInsteadOfTimeout` |
| P2-14 | 中 | BACnet 读显式传 array index 0 | 传 `BACNET_ARRAY_ALL`；`ReadAsync_UsesArrayAllInsteadOfZero` |
| P1-5 | 高 | **串口端口打不开时驱动停在 Connected**（异常不属于传输级，池不重建） | RTU/ASCII 在 `ConnectAsync` 即打开串口 → 失败即 `Faulted`；RTU/ASCII 各一条回归 |
| P1-4 | 中高 | **Mitsubishi 字软元件 bool 落到错误位**：`D110+b ool` 写入按 head=110 的位命令（规范等价 D6.14） | 读=字+取 bit，写=补全 `D110.bit` 走读改写；位号 0–15 校验；3 条回归（读/写保位/非法位） |
| P2-2 | 中高 | **Omron / Mitsubishi 逐点请求**（N 点 = N 次往返） | Omron：连续字/位合并（8 点 → 2 请求，计数代理断言）；Mitsubishi：Snet 数组类型批量读（5 short / 3 float 各 1 请求，帧断言） |
| P2-3 | 中 | **未知 `DataType` 静默回退 `int`**（AB/Omron/Mitsubishi） | 改用 `DataTypeMapper.TryResolve`；未知类型 `BadConfigError` 且**不产生线上请求**（AB/Omron 回归） |
| P2-6 | 中 | **AB `timeout` 配置未使用**：对端接受连接但不响应时会挂起 | Connect/Read/Write 逐操作 `CancelAfter(timeout)`，超时转 `TimeoutException` 并 `Faulted`；模拟器"停止回包"回归 |
| P2-7 | 中 | **S7 批量写失败后逐点重写全部点位**（重复写风险） | 先逐点校验 DataItem；单点直接逐点写；传输级失败不再逐点重写；写路径新增非法地址隔离回归 |
| P2-8 | 中 | **池回收与借用竞态**：空置池销毁时并发 Acquire 可能撞已释放信号量 | `TryBeginRecycle` 原子标记 + `PoolRecycledException` + 上层重取池重试；`AcquireAsync_WhileRecycling_...` 回归 |

**行为变更提示**：Mitsubishi `D/W/R/ZR + bool` 的语义在本次变更（`D110` ≡ `D110.0`，读写均按 bit），已在 CHANGELOG 标注为**不兼容变更**；需要整字真值判断请用 `DataType=ushort`。

### 5.2 待处理（中危，建议排期）

| ID | 问题 | 证据/位置 | 影响 | 建议 |
|---|---|---|---|---|
| P2-1 | `MaxRetryAttempts=0` 通过 Options 校验，首次下发设备时 Polly 抛异常 | `PoolOptions.cs:14` + `PoolOptionsValidationTests` | 配置层"允许"的值运行期崩溃 | 改为 `Range(1,10)` 或启动期显式校验 |
| P2-4 | S7 标量忽略 `DataType`（DBD→`uint`），README 示例却写 `Real` | `S7Driver.cs:309`、README:118 vs 155 | 用户按示例配 float 会拿到 `uint` | 文档明确说明，或按 `DataType` 覆盖 VarType |
| P2-5 | Mitsubishi 批量写部分成功时全部标 Bad | `MitsubishiDriver.cs` 写路径 | 已写入的点被报 Bad，重试有非幂等风险 | 拆单点写或回读校验 |
| P2-9 | 同连接串多设备共享池，但池用首个设备配置 | `DeviceDriverPool.PoolKey` | 第二台设备的设备级超时被忽略 | 池键包含影响连接的配置，或文档明确 |
| P2-10 | 连接串解析：未知键静默忽略、值含 `;` 截断、值被 Trim | `KeyValueConnectionString.cs:13` | 拼错键静默用默认值；密码首尾空格被吞 | 绑定后校验已知键；值不 Trim |
| P2-11 | OPC UA 订阅停止不从 Session 移除、KeepAlive 未解绑 | `OpcUaDriver.cs:243/329` | 热更新反复订阅可能累积 | `RemoveSubscription` + `-=` |
| P2-12 | 指标口径：无 Bad 质量计数；`plc.read.errors` 只统计异常 | `PlcMetrics.cs` | 协议错误（转 Bad）在指标上不可见 | 增加 `plc.points.bad` 等 |
| P2-13 | 厂商语义未真机确认（Mitsubishi 位、S7 字节序、BACnet 严格设备） | — | 模拟器按规范实现，真实设备可能有差异 | 安排一次真机联调 |

### 5.3 低危/规范性（P3）

| 问题 | 位置 |
|---|---|
| `internal sealed class TaskScheduler` 与 `System.Threading.Tasks.TaskScheduler` 同名 | `TaskScheduler.cs:21` |
| `QualityCode.Uncertain` 从未使用；错误统一 `BadCommFailure` | `QualityCode.cs` |
| `GetDeviceHealthAsync` 只反映循环状态（本轮已补 `LastError`） | `TaskScheduler.cs:114` |
| `DeviceSharedPool.AcquireAsync/Return` 在 internal 类型上 public | `DeviceSharedPool.cs:42/89` |
| `PlcMetrics` Meter 版本硬编码 `1.2.0` | `PlcMetrics.cs:8` |
| 包缺 XML 文档；CI 无 `-warnaserror`、无覆盖率、仅 Ubuntu | 各 csproj / `ci.yml` |
| `ModbusType.cs` 无用 using；`Disconnect(ct)` 忽略 ct | 多处 |
| Monitor 通道容量 1 + DropOldest 丢弃无指标 | `PlcMonitor.FanOut` |

---

## 6. 核心组件评估（含本轮改进）

### 6.1 连接池（`DeviceDriverPool` + `DeviceSharedPool`）

**现状**：按 `协议|连接串` 共享、`SemaphoreSlim` 限流、Faulted 驱动丢弃重建、空置池 TTL 回收、`_inUse` 计数保护在途借用；连接阶段走带熔断的每池管线。

**本轮改进**：

- 管线改为 `DeviceSharedPool` **自持**，删除全局 `ManagedResiliencePipelineRegistry` 与 `ResiliencePipelineKeys`：少一个全局可变字典、少一类热更新漏删风险，`Controller` 不再依赖 `DriverPool.Engine` 内部类型。
- 清理定时器从 `System.Threading.Timer`（发射后不管、异常无人观察）改为 `PeriodicTimer` 循环，异常按周期记录并重试，停机时等待循环退出。

**仍存在**：回收竞态（P2-8）、共享池配置取首个设备（P2-9）、IO 级无熔断。

### 6.2 数据管道（`DriverResultPipeline`）

**现状**：有界 Channel + `Wait` 背压 + 单消费者串行分发；handler 异常隔离、逐 handler 超时、可选订阅者；压力测试覆盖 10k 写入无丢失、慢 handler 不阻塞写者。

**本轮改进**：单 handler（最常见）直接调用，去掉每点 `Parallel.ForEachAsync` + 链接 CTS 的分配；多 handler 且并行度 >1 时才走并行路径；超时/异常隔离语义保持不变。

**仍存在**：`_handlers` 只在 `ConsumeAsync` 启动时快照（启动后注册的 handler 不生效）；订阅者 `DropOldest` 无独立指标。

### 6.3 调度与执行（`TaskScheduler` / `TaskActuator` / Collectors）

**本轮改进**：

- `TaskActuator` 捕获全部异常并记录 `LastError`，不再产生未观察任务异常，`StopAsync` 不抛。
- 新增监督循环：每 5s 扫描，`IsRunning == false` 的执行器按指数退避（50ms→60s）重建采集器/驱动；恢复后计数清零。
- `TaskSchedulerHost.StopAsync` 先取消并等待监督循环，再停止执行器，避免停机中被重启。
- `GetDeviceHealthAsync` 返回 `LastError`，健康信息更有诊断价值。

**仍存在**：`HasSignificantChange` 不比较「影响池的」全部字段（与 P2-9 相关）；轮询采集器的失败仍只记录日志、依赖自身循环继续。

### 6.4 监控缓存（`PlcMonitor`）

**本轮改进**：结果与最后活跃时间合并为单字典 `ConcurrentDictionary<PlcPointId, Entry>`，用 CAS 循环替代全局锁：内存减半、无全局锁竞争、同一变化仍只通知一次；淘汰仍用「值匹配删除」避免误删并发更新；通道 `SingleWriter` 声明修正为 `false`。

**仍存在**：通道容量恒为 1（latest-wins，README 已说明）；丢弃无指标。

### 6.5 配置与可观测性

- 配置模型用 `required` + DataAnnotations + `TimeSpanMinimum`，`DeviceConfiguration.Validate` 校验嵌套点位与 TagId 唯一性。
- `PoolOptions`/`PipelineOptions`/`MonitorOptions` 均 `ValidateOnStart`，但与 Polly 约束不一致（P2-1）。
- Metrics 覆盖读/写/获取/管道/监控，ActivitySource 覆盖读/写/分发；缺 Bad 质量与重连计数。

---

## 7. 可简化/精简评估（回答"能否精简且高效运行"）

**结论：可以，且本轮已落地一部分。** 本轮删除/精简约 **120 行**生产代码（注册表、键、定时器逻辑），热路径分配明显下降；剩余可精简约 **300–450 行（6%–9%）**。优先级仍低于性能与真机验证。

### 7.1 本轮已完成

| ID | 项目 | 结果 |
|---|---|---|
| S2 | 删除 `ManagedResiliencePipelineRegistry` + `ResiliencePipelineKeys` | −2 个文件（约 40 行）+ 3 处调用点，消除全局可变状态与 `Controller→DriverPool.Engine` 依赖 |
| S4 | 管道单 handler 快路径 | 每点少 2–4 个分配（`ParallelOptions`、链接 CTS、任务状态机） |
| S5 | Monitor 单字典 + CAS | 内存约减半，去掉全局锁；代码 −20 行 |
| P3-1 | 池清理 `PeriodicTimer` | 去掉 `Timer` 回调与未观察异常，代码更直观 |
| P2-2 | Omron/Mitsubishi 批量读 | 8 点 → 2 请求（Omron）、5 连续 short → 1 请求（Mitsubishi），大点位规模下采集周期显著缩短 |
| P2-6 | Modbus TCP 异步连接 | 不再阻塞线程池线程；`timeout` 真正约束连接建立 |
| P2-8 | 池回收竞态 | 原子标记 + 重试，消除并发 Acquire 撞已释放信号量的窗口 |

### 7.2 仍建议精简（按性价比排序）

| ID | 项目 | 预估收益 | 风险 |
|---|---|---|---|
| S1 | **驱动状态机基类**：`Connect/Disconnect/TryReconnect` + `_stateLock` + `SetState` + `GetXOrThrow` + `MarkFaultedIfTransport` 抽到 `ProtocolDriverBase` | −250~300 行，Faulted 语义统一，新驱动接入成本减半 | 中：需逐驱动回归（现有 414 测试可兜底） |
| S3 | **合并 10 个 `*Log.cs`**（329 行）为每程序集一个 `[LoggerMessage]` 类或放进驱动文件 | −200 行、文件数 −9 | 低 |
| S6 | **Modbus 批处理规划器**抽取（分组/合并/重叠检测） | 结构更清晰、可脱离 master 单测 | 低 |
| S7 | **清理未消费配置**：Omron `IsUdp/LocalNode/DestinyNode`、`QualityCode.Uncertain` | −10 行或补齐功能 | 低 |
| S8 | **去掉 Omron 内部自动重连**（`AutoReconnect=false`），重试统一交给池 | 行数不变；延迟可控、日志不重复 | 低（需补连接失败用例） |
| S9 | 宿主包装改 `IHostedService` + `IAsyncDisposable` | −30 行，生命周期语义更标准 | 低 |
| S10 | 测试模拟器基类（TCP accept/帧日志样板） | 测试代码 −80~120 行 | 低 |

**不建议"精简"的部分**：`ModbusValueCodec`（字节序矩阵是正确性资产）、按区间合并的批处理、Polly 分层、Monitor 去重语义。

---

## 8. 高效运行建议（性能）

按实际吞吐/延迟影响排序：

1. **协议层批量能力（已完成）**：Omron 连续字/位合并、Mitsubishi 数组批量读；大点位规模下请求数下降一个数量级。
2. **连接建立非阻塞化（已完成）**：Modbus TCP 改为异步连接并受 `timeout` 约束；AB 各操作也套上超时。
3. **IO 级按设备熔断**（剩余）：当前 IO 重试无断路器，单个坏设备每周期重试 3 次（指数退避），拖长采集周期；可复用 `AddPoolStrategies`。
4. **剩余热路径**：多 handler 并行路径仍每次分配 `ParallelOptions`；Monitor 订阅扇出每次遍历全部订阅者（可用按设备索引）。
5. **监控/指标**：增加 Bad 质量计数与重连/丢弃指标，便于容量规划。

---

## 9. 改进路线图

### 已完成（本轮）

- [x] P0-1/P0-2/P0-3：三处静默错值修复（AB / Mitsubishi / BACnet）
- [x] P1-1：推送执行器监督重启 + 停机安全
- [x] P1-2/P1-3/P2-14：BACnet 远端端口与 arrayIndex
- [x] P1-4：Mitsubishi 字软元件 bool 位语义（按 MC 规范）
- [x] P1-5：串口不可用即 Faulted
- [x] P2-2：Omron/Mitsubishi 批量读（请求数下降一个数量级）
- [x] P2-3：未知 `DataType` 显式 `BadConfigError`（AB/Omron/Mitsubishi）
- [x] P2-6/P2-7：AB 超时生效、S7 写回退去重
- [x] P2-8：池回收竞态（原子标记 + 重试）
- [x] OPC UA 数据面回环（真实服务端：会话/读/订阅/只读写 Bad）
- [x] S2/S4/S5 + P3-1：注册表删除、管道快路径、监控 CAS、池清理 PeriodicTimer、Modbus TCP 异步连接

### 第 1 阶段（0–2 周）

- [ ] P2-1：`MaxRetryAttempts` 校验与 Polly 对齐
- [ ] P2-5：Mitsubishi 批量写部分成功语义（拆单点写或回读校验）
- [ ] P2-11：OPC UA 订阅生命周期（RemoveSubscription / KeepAlive 解绑）
- [ ] P2-4：S7 文档/示例与 `DataType` 行为对齐
- [ ] 安排一次 Mitsubishi/S7 真机联调（位语义、字节序、批量边界）

### 第 2 阶段（2–6 周）

- [ ] P2-9/P2-10：共享池配置与连接串严格校验
- [ ] OPC UA 自定义可写节点空间回环（自定义 NodeManager）
- [ ] CI：Windows job（串口/回环）、`-warnaserror`、覆盖率门槛

### 第 3 阶段（6–12 周）

- [ ] S1 驱动基类重构（配合 414 测试逐步迁移）
- [ ] S3/S6/S9 结构与日志精简（约 −300 行）
- [ ] 打包质量：`GenerateDocumentationFile`、包描述/Tags、中央包版本、确定性构建
- [ ] 负载与稳定性：10k 点位/50 设备/24h 浸泡；断网恢复混沌测试
- [ ] 可观测性：Bad 质量、重连、丢弃指标

### 验收门槛（建议写入 CI）

1. `dotnet build -warnaserror` 0 警告；
2. 全量测试通过 + 覆盖率 ≥ 70%（核心包 ≥ 80%）；
3. 每个驱动至少一个真实协议回环用例（现有 6 套服务端可作模板）；
4. 任何"Good + 空/异常值"路径必须有测试禁止。

---

## 10. 附录

### 10.1 本轮变更清单

**生产修复/优化**

| 文件 | 变更 |
|---|---|
| `PlcLibrary.AllenBradley/AllenBradleyDriver.cs` | 批量读跳过并校验 CIP 类型码；显式小端解码；严格 `DataType`；Connect/Read/Write 超时 |
| `PlcLibrary.Mitsubishi/MitsubishiDriver.cs` | 响应值 Address 优先 + TagId 冲突守卫；字软元件 bool 读字取位、写读改写；位号校验；数组批量读（`Int16Array`/`FloatArray`/…）；严格 `DataType` |
| `PlcLibrary.Omron/OmronDriver.cs` | 字/位分组批量读（Transform 解码）；严格 `DataType`；移除反射 Read |
| `PlcLibrary.Bacnet/BacnetDriver.cs` | 逐对象 `BacnetError`/缺项降级 Bad；远端端口写入地址；读写使用 `BACNET_ARRAY_ALL` |
| `PlcLibrary.S7/S7Driver.cs` | 写路径重构：先校验 DataItem、单点直写、传输级失败不回退重写、非法地址隔离 |
| `PlcLibrary.Modbus/ModbusDriverBase.cs`、`ModbusTcpDriver.cs` | 异步工厂重载；TCP 异步连接 + `timeout`；同步重载保留兼容 |
| `PlcLibrary/Controller/Engine/TaskActuator.cs` | 全异常捕获、`LastError`、停机不抛 |
| `PlcLibrary/Controller/Engine/TaskScheduler.cs` | 监督循环 + 指数退避重建；停机先取消监督；健康信息带 `LastError` |
| `PlcLibrary/Controller/Collectors/PushCollector.cs` | 自持弹性管线，移除注册表依赖 |
| `PlcLibrary/DriverPool/Engine/DeviceDriverPool.cs` | 移除注册表；清理改 `PeriodicTimer`；回收竞态重试；停机等待清理循环 |
| `PlcLibrary/DriverPool/Engine/DeviceSharedPool.cs` | 自持管线；回收标志 + `PoolRecycledException` |
| `PlcLibrary/DriverPool/Engine/PoolRecycledException.cs`（新增） | 池回收信号 |
| `PlcLibrary/DriverPool/Engine/ManagedResiliencePipelineRegistry.cs`（删除） | — |
| `PlcLibrary/DriverPool/Models/ResiliencePipelineKeys.cs`（删除） | — |
| `PlcLibrary/Pipeline/Engine/DriverResultPipeline.cs` | 单 handler 快路径；handler 调用抽取 |
| `PlcLibrary.Monitor/Engine/PlcMonitor.cs` | 单字典 + CAS；`SingleWriter=false` |
| `PlcLibrary.Modbus/ModbusRtuDriver.cs`、`ModbusAsciiDriver.cs` | 连接阶段打开串口，失败即 Faulted |
| `PlcLibrary/General/GatewayLog/ControllerLog.cs`、`PoolLog.cs` | 新增重启/清理日志 |
| `PlcLibrary/Extensions/ServiceCollectionExtensions.cs` | 移除注册表注册 |

**新增/更新测试（72 个用例 / 15 个文件）**

| 文件 | 用例 |
|---|---|
| `PlcLibrary.Tests/S7/S7DriverSimulatorTests.cs` + `Simulators/S7SimServer.cs` | 7 |
| `PlcLibrary.Tests/Omron/OmronDriverSimulatorTests.cs` + `Simulators/FinsFrameCounterProxy.cs` | 8 |
| `PlcLibrary.Tests/Modbus/ModbusTransportLoopbackTests.cs` | 5（含 2 个串口 Theory 分支 + TCP 连接超时） |
| `PlcLibrary.Tests/Mitsubishi/MitsubishiDriverSimulatorTests.cs` + `Simulators/McProtocolSimServer.cs` | 21 |
| `PlcLibrary.Tests/AllenBradley/AllenBradleyDriverSimulatorTests.cs` + `Simulators/EtherNetIpSimServer.cs` | 12 |
| `PlcLibrary.Tests/Bacnet/BacnetDriverSimulatorTests.cs` + `Simulators/BacnetSimServer.cs` | 11 |
| `PlcLibrary.Tests/OpcUa/OpcUaDriverDataPlaneTests.cs` + `Simulators/OpcUaTestServer.cs` | 3 |
| `PlcLibrary.Tests/Controller/PushDeviceFailureTests.cs` | 3 |
| `PlcLibrary.Tests/DriverPool/PoolOptionsValidationTests.cs` | 1 |
| `PlcLibrary.Tests/DriverPool/DeviceSharedPoolTests.cs`（新增回收竞态用例） | 1 |

**文档**：`README.md`（Mitsubishi 位语义、BACnet 端口、监督、批量表修正）、`DEVELOPMENT.md`（池生命周期/监督循环）、`AGENTS.md`（测试矩阵）、`CHANGELOG.md`（Unreleased：修复/不兼容变更/性能/测试）。

### 10.2 复现命令

```bash
dotnet build PlcLibrary.slnx -c Debug
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug

# 单协议模拟器
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~S7DriverSimulatorTests"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~OmronDriverSimulatorTests"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~MitsubishiDriverSimulatorTests"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~AllenBradleyDriverSimulatorTests"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~BacnetDriverSimulatorTests"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~OpcUaDriverDataPlaneTests"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~PushDeviceFailureTests"
```

### 10.3 验证覆盖与未覆盖（一页看清）

| 领域 | 已覆盖 | 未覆盖 |
|---|---|---|
| Modbus | TCP/UDP 真实回环、批量读写、字节序、错误隔离、重连、串口失败、TCP 连接超时 | 串口物理层、PDU 边界极端值 |
| S7 | 握手/批量读/写/位/string/断链重连/写非法地址隔离 | 大 PDU 分批、`DataType` 生效 |
| Omron | 握手/字位批量读/写/错误码/配置生效性 | UDP、真机字节序 |
| Mitsubishi | MC 3E 数组批量读/写/位/越界/键匹配/字软元件位语义 | A1E/A3C/FX、真机位语义、部分写失败 |
| Allen-Bradley | 批量读（修复后）/回退/写/字符串/超时/Connected | 数组/UDT/分片、非空 path、真机 |
| BACnet | 发现/RMP 读/写/错误降级/非标准端口/arrayIndex | 严格设备、COV 订阅、BV 枚举 |
| OPC UA | 真实服务端：会话/批量读/只读写 Bad/订阅推送；配置/证书/订阅构造 | 自定义可写节点空间、重连风暴、长稳 |
| 核心 | 池/管道/调度/监控/配置/并发/背压/监督重启/池回收竞态 | 长时间浸泡 |

---

*本报告基于提交 `c348490` 的代码与本次新增测试；所有测试均在离线环境通过（414/414），构建 0 警告 0 错误。Mitsubishi 字软元件 bool 语义为不兼容变更，升级前请核对现有点位配置。*
