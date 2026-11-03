# PlcLibrary 工业级质量评估报告（2026-11-03）

> **评估快照**：本报告记录 2026-11-03 对工作区（基线提交 `c839b2b` + 本轮修复）的评估与实测结果，不是长期行为依据。当前行为以 [README](../README.md) / [DEVELOPMENT](../DEVELOPMENT.md) 为准；历史快照见 [ASSESSMENT-2026-11.md](./ASSESSMENT-2026-11.md)。

| 项 | 内容 |
|---|---|
| 评估对象 | `PlcLibrary`（9 个 NuGet 包 + 测试工程） |
| 基线提交 | `c839b2b`（v1.3.4）；本次发布 `v1.3.5` |
| 评估日期 | 2026-11-03 |
| 评估方式 | 全量源码静态审查 + Debug/Release 构建 + 全量/分模块测试 + 9 包 `dotnet pack` + 依赖/分析器/格式专项检查 + 3 套独立行为探针 |
| 实测规模 | 生产代码 75 文件 / 约 6.3k 行；测试代码 58 文件 / 约 7.9k 行；7 个协议包（Modbus 含 TCP/UDP/RTU/ASCII）、7 套离线服务端 |

---

## 0. 结论速览

**总体评分：8.5 / 10（A−）**。这是一个架构分层清晰、工程规范成熟、离线验证投入明显高于同类库的项目：**7 个协议包（Modbus 含 TCP/UDP/RTU/ASCII）全部具备真实报文回环**，434 个测试在 Debug/Release 下全绿，连续 3 次运行无抖动；本轮又修复了 11 项正确性/并发/配置缺陷。剩余风险不在“能不能跑”，而在**厂商真机语义、推送看门狗边界、可观测性口径与 CI 覆盖面**。**结论：核心与轮询驱动可进入生产试用；OPC UA 建议在自定义可写节点、安全模式和长稳上补一轮真机/现场验证；RTU/ASCII 必须在真实串口环境复验。**

| 维度 | 评分 | 结论 |
|---|---|---|
| 架构与分层 | 8.8 | 契约/实现分离清晰；池/管道/调度职责边界稳定；驱动工厂与 DI 接入成本低 |
| 协议驱动完整度 | 8.6 | 7 个协议包（Modbus 含 4 种传输）均有真实报文回环；Modbus TCP/UDP、S7、Omron、Mitsubishi、AB、BACnet、OPC UA 覆盖主要数据面 |
| 数据正确性 | 8.4 | 本轮修复 OPC UA Bad 状态静默 Good、Omron 位后缀静默错值、AB 空载荷越界；S7 标量 `DataType`、BACnet 严格设备语义仍是已知边界 |
| 并发与资源管理 | 8.7 | 池回收/归还、硬超时、信号量泄漏、停机竞态均有回归；本轮补上停止后残留执行器 |
| 容错与恢复 | 8.4 | 监督重启、心跳看门狗、连接级断路、IO 硬超时成熟；IO 级仍无断路，S7 读回退会放大故障 |
| 可观测性 | 7.0 | Metrics/Activity 覆盖主路径并补齐 `PlcLibrary.Acquire`；缺 Bad 质量/重连/订阅丢弃计数 |
| 测试工程 | 9.0 | 真实协议回环、动态端口、帧/请求计数、并发/超时/背压/停机回归；缺真机与长时间浸泡 |
| 文档/发布/CI | 7.8 | README/DEVELOPMENT/CHANGELOG 维护良好；无 XML 文档、CI 仅 Ubuntu、无覆盖率/warnaserror 门禁、SourceLink 传递漏洞 |

### 本轮修复/加固一览

| ID | 类型 | 内容 | 证据 |
|---|---|---|---|
| F-01 | 配置 | `Options` 补 `ValidateDataAnnotations()`；`MaxRetryAttempts=0`、`Pipeline.Capacity=0` 在宿主启动即失败 | 2 个启动校验回归 |
| F-02 | 连接池 | 重连返回 `false` 却仍为 `Faulted`/`Disconnected` 的驱动不再交给调用方 | `DeviceSharedPool` 回归 |
| F-03 | 调度 | 停机与 `ApplyDevicesAsync` 并发时，停止后不再残留运行中的执行器 | 新并发回归 |
| F-04 | Omron | 数值点位带 `.bit`、bool 位号 >15 返回 `BadConfigError` 且不产生线上请求 | 2 个帧计数回归 |
| F-05 | OPC UA | 订阅通知的 `DataValue.StatusCode` 为 Bad 时不再以 Good 推送 | `CreatePushResult` 回归 |
| F-06 | OPC UA | 连接失败置 `Faulted`；Read/Write 的传输级异常置 `Faulted`，使池/监督可重建 | 代码路径 + 回归 |
| F-07 | OPC UA | 非法 `NodeId` 返回 `BadConfigError`（原 `BadCommFailure`） | 数据面回归 |
| F-08 | OPC UA | `StopPushingAsync` 从 Session `RemoveSubscription`；`DisconnectAsync` 解绑 KeepAlive | 代码路径 |
| F-09 | AB | CIP 应答只有 2 字节类型码、无数据时先返回失败，避免 `payload[0]` 越界 | 代码路径 |
| F-10 | 可观测性 | `DeviceSharedPool.AcquireAsync` 发出文档声明的 `PlcLibrary.Acquire` Activity | 代码路径 |
| F-11 | 中高 | S7 批量读传输故障后仍逐点回退，静默链路下 N 点 × `timeout`；地址解析失败被报成通信错误 | 传输失败整批 Bad 并置 `Faulted`；解析失败本地 `BadConfigError`；S7 回归 |

### 剩余 Top 5 风险

1. **推送看门狗把“无变化”当“无进展”**：OPC UA 点位 5 分钟无变化会被重建订阅，稳定工况可能周期性抖动（P1-2）。
2. **共享池配置取首个设备 + 连接串静默忽略未知键/截断 `;`**：多设备同连接串时设备级超时可能失效，密码含分号会被截断（P2-1/P2-2）。
3. **可观测性口径不完整**：`plc.read.errors` 只统计异常，不统计协议 `Bad`；订阅 `DropOldest` 无逐点计数；缺重连计数（P2-4）。
4. **未做真机/长稳**：Mitsubishi 位语义、S7 字节序、BACnet 严格设备、OPC UA 自定义可写节点与 RTU/ASCII 物理层仍需现场验证（P2-5）。
5. **S7 标量 `DataType` / BACnet 读写超时**：仍按地址推断/底层默认，用户需自行转换或接受慢设备延迟（P2-6/P2-8）。

---

## 1. 范围与方法

### 1.1 范围

- 核心：`PlcLibrary` 的驱动契约、连接池、Polly 弹性、调度/采集器、数据管道、配置、Metrics/Activity。
- 驱动：Modbus（TCP/UDP/RTU/ASCII）、S7、OPC UA、Mitsubishi、Omron、Allen-Bradley、BACnet、Monitor。
- 测试工程、离线服务端、CI、打包与依赖。

### 1.2 方法

1. **逐文件静态审查**：核心 46 文件 + 驱动 29 文件，重点检查状态机、异常路径、取消传播、资源释放、协议编解码、配置消费闭环。
2. **构建/测试实测**：Debug、Release 分别构建；全量 3 次重复；按模块过滤统计。
3. **真实协议回环**：NModbus TCP/UDP 从站、自建 S7comm、MC 3E、EtherNet/IP+CIP、BACnet/IP、NewLife FINS 服务端、真实 OPC UA 标准服务端。
4. **独立探针**：在不修改生产代码的前提下，用临时测试验证 Mitsubishi 帧分组、Omron `FinsAddress` 位语义、BACnet 多驱动并发、Mitsubishi 未知写类型；探针已删除，未留在仓库。
5. **专项检查**：`dotnet list package --vulnerable`、`--outdated`；`AnalysisMode=All`；`dotnet format --verify-no-changes`；nupkg 解包检查。

### 1.3 明确局限

- **没有真实 PLC**：模拟器按规范/第三方库行为实现，能证明“线格式与驱动解码正确”，不能证明具体厂商固件一致。
- **OPC UA 标准节点空间**：覆盖会话、批量读、只读节点写失败、订阅推送；未实现自定义 NodeManager 的可写节点。
- **串口物理层**：RTU/ASCII 只覆盖参数映射与“端口不可用即 Faulted”，未做 RS-485 帧回环。
- **未做 24h 浸泡/断网混沌**：只有加速并发/故障浸泡（数量级为秒到分钟）。

---

## 2. 构建与测试实测

| 项 | 结果 |
|---|---|
| `dotnet build PlcLibrary.slnx -c Debug` | **0 警告 / 0 错误** |
| `dotnet build PlcLibrary.slnx -c Release` | **0 警告 / 0 错误** |
| `dotnet test ... -c Debug` | **434 通过 / 0 失败 / 0 跳过**，约 5–8s |
| `dotnet test ... -c Release` | **434 通过 / 0 失败** |
| 连续 3 次 Debug 全量 | 434/434/434，约 7–8s，无抖动 |
| 分模块 | Modbus 155、S7 33、Omron 15、Mitsubishi 26、AB 17、BACnet 16、OPC UA 18、Monitor 18、Pipeline 11、DriverPool 28、Controller 25、Integration 6、General 17、DriverDomain 20、Drivers 29 |
| `dotnet pack` | 9 个 `1.3.5` nupkg 成功生成 |
| `dotnet list package --vulnerable` | 仅 `Microsoft.Build.Tasks.Git 8.0.0`（SourceLink 传递、构建期，Moderate） |
| `AnalysisMode=All` | 689 条建议级告警，0 错误（非默认门禁；集中在测试命名、`ConfigureAwait`、宽泛 `catch`、null 参数校验） |
| `dotnet format --verify-no-changes` | 未通过（大量 IMPORTS/WHITESPACE 风格差异），CI 未设格式门禁 |

**评价**：测试“快、全离线、无固定端口”仍是核心资产；本轮新增 8 个回归用例（426 → 434），覆盖配置启动校验、池重连失败、停机竞态、Omron 位后缀、OPC UA Bad 状态与非法地址。缺口是 CI 仍只跑 Ubuntu、无 `-warnaserror`、无覆盖率、无 Windows（串口/回环）作业。

---

## 3. 架构评估

### 3.1 分层与契约（优秀）

```
PlcLibrary（核心）
├── DriverDomain   IProtocolDriver / IPushProtocolDriver / IDeviceAccessor / DriverResult / QualityCode
├── DriverPool     DeviceDriverPool → DeviceSharedPool（按 协议|连接串 共享，自持 Polly 管线）
├── Pipeline       DriverResultPipeline（有界 Channel + 多 IDataHandler 扇出）
├── Controller     TaskScheduler（热更新 reconcile + 监督重启）→ TaskActuator → Polling/PushCollector
└── General        配置、Metrics、ActivitySource、日志
PlcLibrary.<Protocol>  每协议一个包，AddDriver<T>() 注入
PlcLibrary.Monitor     缓存最新值 + 变化订阅
```

**优点**

- **契约稳定**：`IProtocolDriver` 只要求连接/断开/重连/读写，新增协议只需 `[ProtocolDriverName]` + `AddDriver<T>()`。
- **轮询/推送分轨**：`IPushProtocolDriver` + `SupportsPush`，订阅状态绑定驱动实例，避免池化破坏订阅语义。
- **单一实例多接口**：`PlcMonitor` 同时注册 `IPlcMonitor`/`IDataHandler` 并转发，符合 AGENTS 约定。
- **弹性分层**：连接级重试/超时/熔断（每池）与 IO 级重试/超时（进程级）职责清楚。
- **热更新 reconcile**：按 `Id` 差量启停；本轮补上“停机与 Apply 并发”后，停止语义更可靠。
- **有界失败**：池借用、IO、handler、采集器停止都有硬上限，避免单点卡死拖垮宿主。

### 3.2 架构短板

1. **没有驱动基类**：S7/Omron/Mitsubishi/AB/BACnet 各自重复状态机、`Faulted` 标记、重连样板，语义漂移风险仍在（例如本轮修 OPC UA 前，它没有统一 `MarkFaultedIfTransport`）。
2. **错误分类不统一**：同类错误在不同驱动映射不同 `QualityCode`（S7 读非法地址 `BadCommFailure`，写却是 `BadConfigError`；Mitsubishi 写未知类型也是 `BadCommFailure`）。
3. **`TaskScheduler` 是手工编排的“业务宿主”**：`TaskSchedulerHost` 只包一层 `BackgroundService`，生命周期仍依赖内部 `_stopping`/锁约定；本轮补了竞态，但结构上仍不如标准 `IHostedService` + `IAsyncDisposable` 清晰。
4. **配置绑定对拼写错误零反馈**：`ConnectionStringBinder` 忽略未知键（README 已声明），现场拼错只能靠现象排查。

---

## 4. 驱动完整性验证矩阵

图例：**A** = 关键路径有真实报文端到端验证；**B** = 主要路径已验证、个别分支缺失；**C** = 仅配置/失败路径；**D** = 仅静态检查。

| 驱动 | 协议 | 等级 | 已离线验证 | 未验证/缺口 |
|---|---|---|---|---|
| `ModbusTcpDriver` | Modbus TCP | **A** | 真实 NModbus 回环读写、批量读/写、多寄存器类型/字节序、重叠写拒绝、错误隔离、连接超时 | 大规模 PDU 边界、慢设备/半包 |
| `ModbusUdpDriver` | Modbus UDP | **A−** | 真实 UDP 回环读 + 写回从站 | 断线重连、丢包重试 |
| `ModbusRtuDriver`/`AsciiDriver` | 串口 | **C** | 参数映射、端口不可用即 `Faulted` | RS-485 帧/CRC 端到端（需硬件/虚拟串口） |
| `S7Driver` | S7comm | **A−** | TPKT/COTP 握手、批量读、int/real/bit/string 解码、单点/批量写回、非法地址隔离、断链→`Faulted`→重连、传输失败快速退批 | 大 PDU 分批、标量 `DataType` 生效 |
| `OmronDriver` | FINS/TCP | **A−** | 握手、字/位批量读（8 点 → 2 请求）、int16/uint16/float/bool、写回、PLC 错误码映射 | UDP、真机字节序；本轮补位后缀校验 |
| `MitsubishiDriver` | MC 3E | **A−** | 连接、同类型连续数组批量读、int16/uint16/float/bool、字/位写、越界/设备错误隔离、TagId 串点回归、字软元件位语义 | A1E/A3C/FX、部分写失败语义、真机位语义 |
| `AllenBradleyDriver` | EtherNet/IP+CIP | **A−** | RegisterSession、CIP 批量读类型码、逐点回退、写、字符串、超时、Connected 模式 | 数组/UDT/分片、非空 backplane path |
| `BacnetDriver` | BACnet/IP | **B+** | WhoIs/IAm、RMP 批量读、写、逐对象错误/缺项降级、远端端口/arrayIndex | 严格设备兼容性、COV 订阅、BV 真实枚举编码 |
| `OpcUaDriver` | OPC UA | **A−** | 真实服务端：会话、批量读（标准节点 Good/未知节点 Bad）、只读节点写 Bad、订阅推送；本轮补 Bad 状态/非法地址/断连状态 | 自定义 NodeManager 可写节点、重连风暴、长稳 |

### 4.1 关键线格式/行为证据（摘录）

- **S7**：`ReadMultipleVarsAsync` 将 2 个非字符串点位合并为 **1 次** Read Var（`ReadRequestCount == 1`）；断链后状态为 `Faulted`，重连恢复。
- **Mitsubishi**：5 个连续 `short` 为 **1 次** MC 数组读，帧 `... 04 00 00 64 00 00 A8 05 00`；3 个连续 `float` 也为 1 次；字软元件 bool 读共用一条字读，位软元件 `M110` 单独位读。
- **Omron**：8 个点位（5 连续字 + 3 连续位）经代理计数为 **2 个数据帧**；FINS 握手 20 字节，服务端回 24 字节。
- **Allen-Bradley**：批量读为 `SendRRData(0x6F)` 内嵌 CIP `0x0A`；每个嵌入 Read Tag 应答前 2 字节为 CIP 类型码（DINT `C4 00`、REAL `CA 00`、BOOL `C1 00`），驱动跳过并校验；拒绝批量时逐点回退 3 次 `0x4C`。
- **BACnet**：RMP 请求 `PROP_PRESENT_VALUE` 的 arrayIndex 为 `0xFFFFFFFF`；写请求 arrayIndex 同样为 `BACNET_ARRAY_ALL`，不再把 timeout 编码进报文。
- **OPC UA**：真实 `Opc.Ua.Server` 标准节点空间；`ServerStatus`/`CurrentTime` 返回 Good，未知 NodeId 返回 Bad，只读节点写返回 Bad；订阅 `CurrentTime` 连续收到通知。

---

## 5. 缺陷与风险清单

> 状态：**本轮已修复** = 本轮代码 + 回归/代码证据；**待处理** = 仍存在，列入路线图。

### 5.1 本轮已修复

| ID | 严重度 | 问题 | 修复与证据 |
|---|---|---|---|
| F-01 | 中高 | Options 的 DataAnnotations 从未生效：`MaxRetryAttempts=0`、`Pipeline.Capacity=0` 能通过启动，运行期才抛 Polly 异常 | `ValidateDataAnnotations()` + `ValidateOnStart()`；2 个启动校验测试 |
| F-02 | 高 | `TryReconnectAsync` 返回 `false` 后，池仍把 `Faulted`/`Disconnected` 驱动交给调用方 | 连接后校验 `DriverStatus == Connected`；失败销毁并抛出；池回归 |
| F-03 | 高 | 停机与 `ApplyDevicesAsync` 并发：停止返回后仍可能启动执行器，造成“停机后还在采集” | 启动后检查 `_stopping` 并立即停止；新并发回归 |
| F-04 | 中高 | Omron 数值点位带 `.bit` 会静默读到整字；bool 位号 >15 被底层接受 | 本地校验 `BitOffset 0–15` / 数值点位禁止位后缀；2 个帧计数回归 |
| F-05 | 高 | OPC UA 订阅 `DataValue.StatusCode=Bad` 但 Value 非空时按 Good 推送（静默坏数据） | `CreatePushResult` 校验状态码；2 个单元回归 |
| F-06 | 中高 | OPC UA 连接失败停在 `Connecting`；Read/Write 传输异常不置 `Faulted` | `ConnectAsync` catch → `Faulted`；`MarkFaultedIfTransport`；代码路径 + 回归 |
| F-07 | 中 | OPC UA 非法 `NodeId` 返回 `BadCommFailure`，与其它驱动配置错误语义不一致 | 改为 `BadConfigError`；数据面回归 |
| F-08 | 中 | OPC UA 停止订阅只 `Dispose`，不从 Session 移除；KeepAlive 匿名处理器无法解绑 | `RemoveSubscription` + `-=`；代码路径 |
| F-09 | 低 | AB CIP 应答只有类型码、无 payload 时 `payload[0]` 越界 | 空 payload 先返回 false；代码路径 |
| F-10 | 低 | 文档声明 `PlcLibrary.Acquire` span，但代码未创建 | 在 `DeviceSharedPool.AcquireAsync` 创建 Activity；代码路径 |
| F-11 | 中高 | S7 批量读传输故障后仍逐点回退；地址解析失败被报成 `BadCommFailure` | 传输失败整批 Bad 并置 `Faulted`；解析失败本地 `BadConfigError`；S7 回归 |

**行为变更提示**：`OMRON` 非法位后缀、OPC UA 非法 NodeId 现在返回 `BadConfigError`；依赖原 `BadCommFailure` 的上层如做错误分类需同步调整。`MaxRetryAttempts=0` 现在是启动期配置错误，不再延迟到首次设备下发。

### 5.2 待处理（建议排期）

| ID | 严重度 | 问题 | 证据/位置 | 影响 | 建议 |
|---|---|---|---|---|---|
| P1-2 | 中高 | 推送看门狗把“5 分钟无数据”当“无进展” | `PushCollector.cs` 心跳仅在数据回调更新；`StallTimeout=5min`；`TaskScheduler` 用 `IsStalled` | 稳定工况下健康订阅被周期性重建，产生抖动/丢点 | 用驱动 KeepAlive/连接状态刷新心跳，或让阈值可配/按设备类型区分 |
| P2-1 | 中 | 同连接串多设备共享池，池取首个设备的 `ConnectionTimeout` | `DeviceDriverPool.PoolKey` | 第二台设备设备级超时被忽略 | 池键纳入影响连接的配置，或文档明确“共享池使用首个设备超时” |
| P2-2 | 中 | 连接串未知键静默忽略、值含 `;` 截断、值被 Trim | `KeyValueConnectionString.cs` | 拼错键用默认值；密码首尾空格/分号丢失 | 绑定后校验已知键；值不 Trim；支持转义/引号 |
| P2-3 | 中 | OPC UA 推送非法地址只记日志，不产生 Bad 结果 | `OpcUaDriver.StartPushingAsync` 的 `vpCount` 过滤 | 配置错误不会进入数据流，只能查日志 | 对非法点调用 `onData(BadConfigError)` 或直接拒绝订阅 |
| P2-4 | 中 | IO 级无熔断；`plc.read.errors` 只统计异常，不统计协议 Bad；订阅 `DropOldest` 无逐点计数 | `DeviceDriverPool`、`PlcMetrics`、`DriverResultPipeline` | 坏设备每周期重试；指标看不到 Bad/丢弃 | 复用连接级断路器；增加 `plc.points.bad`、`plc.reconnects`、订阅丢弃计数 |
| P2-5 | 中 | 厂商语义/物理层未真机确认 | — | 模拟器正确不等于真实 PLC 正确 | 安排 Mitsubishi/S7/BACnet/OPC UA 真机联调；RTU/ASCII 用虚拟串口对 |
| P2-6 | 中 | S7 标量忽略 `DataType`（`DBD`→`uint`） | `S7Driver.CreateDataItem`；README 已说明 | 用户按 `float` 配置仍拿 `uint` | 明确文档或按 `DataType` 覆盖 `VarType` |
| P2-7 | 中 | Mitsubishi 批量写部分成功仍整批 Bad | Snet 只返回整体结果；已有 `_DocumentsCurrentBehavior` 测试 | 已写入点被报 Bad，重试非幂等 | 拆单点写或回读校验 |
| P2-8 | 中 | BACnet 读/写超时仍依赖底层默认 | `BacnetDriver` 只用 `timeout` 做 WhoIs | 慢设备可能拖长采集 | 为读写套链接 CTS/超时 |

### 5.3 低危/规范性（P3）

| 问题 | 位置/证据 |
|---|---|
| S7 读非法地址返回 `BadCommFailure`，写却返回 `BadConfigError` | `S7Driver.ReadAsync` fallback；现有测试断言当前行为 |
| Mitsubishi 写未知 `DataType` 返回 `BadCommFailure`（虽未发线上请求） | 独立探针 `decimal` → Snet 拒绝 |
| `MitsubishiDriver` 完全不消费 `ct`（Connect/Read/Write 均由 Snet 内部超时兜底） | `AnalysisMode=All` 的 CA2016 |
| `QualityCode.Uncertain` 未使用；错误大多统一 `BadCommFailure` | `QualityCode.cs` |
| `PlcMetrics.PipelineDropped` 文档曾称含订阅丢弃，实际 `DropOldest` 不计数 | 已在 README/DEVELOPMENT 更正 |
| `DeviceConfiguration.TagPoints` 公开可变数组（CA1819） | 配置模型 |
| 多个驱动 `DisconnectAsync` 同步 `Close/Dispose`（CA1849） | S7/Omron/Mitsubishi/BACnet/Modbus |
| 包无 XML 文档；`GenerateDocumentationFile` 未开启 | 各 csproj / nupkg |
| CI 仅 Ubuntu；无 Windows、`-warnaserror`、覆盖率门禁 | `.github/workflows/ci.yml` |
| `Microsoft.SourceLink.GitHub 8.0.0` 传递 `Microsoft.Build.Tasks.Git 8.0.0`（CVE-2026-62900，构建期） | `dotnet list package --vulnerable` |
| OPC UA 1.5.378.156、BACnet 4.0.0-beta.4、Snet 26.197.1、NewLife 1.1.2026.609 均有更新 | `dotnet list package --outdated` |

---

## 6. 核心组件评估

### 6.1 连接池（`DeviceDriverPool` + `DeviceSharedPool`）

**现状**：按 `协议|连接串` 共享、`SemaphoreSlim` 限流、Faulted 驱动丢弃重建、空置池 TTL 回收、`_inUse` 保护在途借用；连接级 Polly 重试/超时/熔断；IO 级重试/超时 + 2s 硬兜底。

**本轮改进**：

- 连接/重连后校验最终状态（F-02），避免“借到未连接驱动”。
- 补 `PlcLibrary.Acquire` Activity（F-10）。
- 全量稳定性测试仍覆盖：借用超时、连接忽略取消、Dispose 卡死、回收/归还并发、信号量泄漏。

**仍存在**：回收与“检查 `_recycling` 后自增 `_inUse`”之间的极窄窗口（测试难以稳定复现）；共享池配置取首个设备；IO 级无熔断。

### 6.2 数据管道（`DriverResultPipeline`）

**现状**：有界 Channel、`Wait` 背压、单消费者串行分发；单 handler 快路径；多 handler 并行 + 每 handler 硬超时；订阅通道 1000、`DropOldest`。

**验证**：10k 写入无丢失、慢 handler 不阻塞写者、handler 忽略取消、队列写入超时、并发停止幂等。

**仍存在**：`_handlers` 只在 `ConsumeAsync` 启动时快照；订阅 `DropOldest` 丢弃无法逐点计数；`ChannelClosedException` 在停机边界由上层捕获。

### 6.3 调度与执行（`TaskScheduler` / `TaskActuator` / Collectors）

**现状**：`_applyLock` 串行化 apply/重启；`TaskActuator` 捕获全部异常并记录 `LastError`；监督循环 5s 扫描，失败/心跳超时按指数退避重建；`TaskSchedulerHost.StopAsync` 先停监督再停执行器。

**本轮改进**：Apply 启动后检查 `_stopping`，消除“停机后残留执行器”（F-03）。

**仍存在**：P1-2 推送看门狗误判；`_restarts` 不随设备移除清理；健康状态只覆盖当前执行器字典。

### 6.4 监控缓存（`PlcMonitor`）

**现状**：单字典 + CAS；值/质量变化才通知；订阅先快照、后变化；单订阅通道容量 1（latest-wins）；按 `EntryIdleTimeout` 淘汰。

**验证**：同 Tag 跨设备隔离、并发同值只通知一次、质量变化发布、取消传播、真实 Modbus 回环。

**仍存在**：快照与首个通道元素在极端并发下可能产生一次旧值（边界去重只比较值/质量）；订阅通道丢弃无指标；未枚举的订阅对象会占用注册表直到释放。

### 6.5 配置与可观测性

- 配置模型用 `required` + DataAnnotations；`DeviceConfiguration.Validate` 校验嵌套点位与 TagId 唯一性。
- **本轮修复**：`PoolOptions`/`PipelineOptions`/`MonitorOptions` 真正执行 DataAnnotations，启动期失败（F-01）。
- Metrics 覆盖读/写/获取/管道/监控；Activity 覆盖读/写/Acquire/分发。
- 仍缺：Bad 质量计数、重连计数、订阅丢弃计数；`plc.read.errors` 只统计异常而非 `DriverResult.Bad`。

---

## 7. 测试质量评估

**强项**

- **真实协议回环**：不是 mock 协议，S7/ MC / EtherNet-IP / BACnet / FINS / OPC UA 都走真实线格式；动态端口、`IAsyncDisposable` 清理。
- **帧/请求断言**：MC 帧十六进制、Omron 代理帧数、AB CIP service、BACnet arrayIndex、S7 批量请求数。
- **韧性回归**：硬超时、取消被忽略、Dispose 卡死、信号量泄漏、handler 卡死、队列反压、监督重启、停机竞态。
- **错误隔离**：单点地址/类型错误不污染同批其他点；AB/Mitsubishi/BACnet 均有回归。
- **本轮新增**：8 个新用例（配置启动校验 1、池 1、停机竞态 1、Omron 2、OPC UA 3），并改造 1 个原 PoolOptions 用例（426 → 434）。

**缺口**

- 无真实 PLC；无 RS-485 物理层；OPC UA 自定义可写 NodeManager 未覆盖。
- 无 24h 长稳/断网混沌；S7 静默链路多点多回退未做加速测试。
- 无覆盖率门禁；`dotnet format` 不干净；CI 不跑 Windows。
- 缺少“Good + 空/异常值”全局静态断言（已有针对 AB/BACnet/Mitsubishi/OPC UA 的点级回归）。

**测试建议**：把 P1-2 做成加速回归（Push 假驱动 + 短心跳阈值），再把覆盖率与 `dotnet format` 纳入 CI；S7 传输失败退批已由现有断链回归覆盖，但建议补“多点 + 静默链路”用例量化收益。

---

## 8. CI、打包与依赖

| 项 | 现状 | 评价 |
|---|---|---|
| CI | Ubuntu，restore/build(Release)/test/pack；失败上传 TRX 并 annotation | 基础可靠；缺 Windows/覆盖率/warnaserror |
| 包 | 9 个 1.3.5 nupkg，README/icon/SourceLink 齐全 | 元数据合格；缺 XML 文档、Tags、中央包版本 |
| 运行时依赖 | Hosting/Logging/Options.DataAnnotations 10.0.9、Polly 8.7.0 | 版本较新；net8.0 库引用 10.x 需确认下游兼容 |
| 协议依赖 | OPC UA 1.5.378.156、BACnet 4.0.0-beta.4、Snet 26.197.1、NewLife 1.1.2026.609 | 均可升级；OPC UA 2.0 是 major，需单独兼容验证 |
| 漏洞 | SourceLink 传递 `Microsoft.Build.Tasks.Git 8.0.0`（Moderate，构建期） | 不进运行时；建议升级 SourceLink |
| 版本 | 本次发布 1.3.5（`Directory.Build.props` 唯一版本源） | 与 CHANGELOG、annotated tag 保持一致 |

---

## 9. 改进路线图

### 第 1 阶段（0–2 周，建议优先）

- [ ] P1-2：推送看门狗改为连接/KeepAlive 心跳，或提供可配置阈值。
- [ ] P2-3：OPC UA 推送非法地址产出 `BadConfigError`。
- [ ] 统一 Mitsubishi 写未知类型的 `BadConfigError` 语义。
- [ ] 升级 SourceLink，消除构建期漏洞告警。

### 第 2 阶段（2–6 周）

- [ ] P2-1/P2-2：共享池键与连接串严格校验（未知键、`;` 转义、空值）。
- [ ] IO 级断路器；Bad/重连/订阅丢弃指标。
- [ ] OPC UA 自定义可写 NodeManager 回环；安全模式（Sign/SignAndEncrypt）回环。
- [ ] CI：Windows job（串口/回环）、`-warnaserror`、覆盖率门槛、`dotnet format`。
- [ ] XML 文档与包元数据（Tags、中央包版本、确定性构建）。

### 第 3 阶段（6–12 周）

- [ ] 驱动基类重构（状态机/重连/Faulted 统一），配合现有 434 测试逐步迁移。
- [ ] 10k 点位/50 设备/24h 浸泡，断网/恢复混沌。
- [ ] 真机联调：Mitsubishi 位语义、S7 字节序/大 PDU、BACnet 严格设备、RTU/ASCII。
- [ ] 精简重复日志/模拟器样板（评估报告旧建议 S1/S3/S10）。

### 验收门槛（建议写入 CI）

1. `dotnet build -warnaserror` 0 警告；
2. 全量测试通过 + 覆盖率 ≥ 70%（核心 ≥ 80%）；
3. 每个驱动至少一个真实协议回环用例；
4. 任何“Good + 空/异常值”路径必须有测试禁止；
5. `dotnet format --verify-no-changes` 通过。

---

## 10. 附录

### 10.1 复现命令

```bash
dotnet build PlcLibrary.slnx -c Debug
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug

# 单协议/模块
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~PlcLibrary.Tests.Modbus"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~PlcLibrary.Tests.S7"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~PlcLibrary.Tests.Omron"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~PlcLibrary.Tests.Mitsubishi"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~PlcLibrary.Tests.AllenBradley"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~PlcLibrary.Tests.Bacnet"
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug --filter "FullyQualifiedName~PlcLibrary.Tests.OpcUa"

# 专项
dotnet test ... --filter "FullyQualifiedName~SchedulerShutdownRaceTests"
dotnet test ... --filter "FullyQualifiedName~PoolOptionsValidationTests"
dotnet pack PlcLibrary.slnx -c Release -o ./artifacts/pkg
dotnet list PlcLibrary.slnx package --vulnerable --include-transitive
```

### 10.2 本轮变更文件

**生产**

| 文件 | 变更 |
|---|---|
| `PlcLibrary/Extensions/ServiceCollectionExtensions.cs` | `PoolOptions`/`PipelineOptions` 增加 `ValidateDataAnnotations()` |
| `PlcLibrary.Monitor/Extensions/ServiceCollectionExtensions.cs` | `MonitorOptions` 增加 `ValidateDataAnnotations()` |
| `PlcLibrary/DriverPool/Models/PoolOptions.cs` | `MaxRetryAttempts` 范围改为 1–10 |
| `PlcLibrary/DriverPool/Engine/DeviceSharedPool.cs` | 连接后状态校验；`PlcLibrary.Acquire` Activity |
| `PlcLibrary/Controller/Engine/TaskScheduler.cs` | 停止后不再启动/保留执行器 |
| `PlcLibrary.Omron/OmronDriver.cs` | 位后缀/位号校验 |
| `PlcLibrary.OpcUa/OpcUaDriver.cs` | Bad 状态、连接/读写 Faulted、非法 NodeId、订阅清理、KeepAlive 解绑 |
| `PlcLibrary.AllenBradley/AllenBradleyDriver.cs` | 空 CIP payload 防护 |
| `PlcLibrary/PlcLibrary.csproj` | 新增 `Microsoft.Extensions.Options.DataAnnotations` |

**测试**

| 文件 | 变更 |
|---|---|
| `PlcLibrary.Tests/Controller/SchedulerShutdownRaceTests.cs`（新增） | 停机/Apply 并发回归 |
| `PlcLibrary.Tests/DriverPool/DeviceSharedPoolTests.cs` | 重连失败 + mock 状态推进 |
| `PlcLibrary.Tests/DriverPool/PoolOptionsValidationTests.cs` | 启动期 Pool/Pipeline 校验 |
| `PlcLibrary.Tests/DriverPool/DriverPoolStabilityTests.cs` | 适配“借用后置 Faulted” |
| `PlcLibrary.Tests/Omron/OmronDriverSimulatorTests.cs` | 2 个位后缀回归 |
| `PlcLibrary.Tests/OpcUa/OpcUaDriverSubscriptionTests.cs` | 2 个 `CreatePushResult` 回归 |
| `PlcLibrary.Tests/OpcUa/OpcUaDriverDataPlaneTests.cs` | 非法 NodeId 回归 |
| `PlcLibrary.Tests/Controller/PushDeviceFailureTests.cs` | 适配 `RetryDelay` 启动校验 |
| `PlcLibrary.Tests/Drivers/DriverConfigConsumptionTests.cs` | 移除过期的 AB Timeout 例外 |

**文档**

`README.md`、`DEVELOPMENT.md`、`AGENTS.md`、`CHANGELOG.md`、本报告。

### 10.3 独立探针结论（已删除，不留在测试工程）

| 探针 | 结论 |
|---|---|
| Mitsubishi `D100/D101/D102/M110` | 同类型连续点合并；跨类型（short/ushort/float）分组，实际 4 次请求，符合当前设计 |
| Omron `FinsAddress.Parse("D100.5")` / `"D100.16"` | `BitOffset` 分别为 5/16；数值点位 `.5` 会静默读整字 → 已修 |
| BACnet 两个驱动共用同模拟器 | 两个驱动均 Connected/Good（本机 shared UDP 模式），未发现本地端口冲突 |
| Mitsubishi `DataType=decimal` 写入 | 当前 `BadCommFailure` 且无线上请求；错误分类待统一 |

---

*本报告基于 2026-11-03 工作区实测：Debug/Release 构建 0 警告 0 错误，434/434 测试通过（连续 3 次），9 个 nupkg 生成成功。*
