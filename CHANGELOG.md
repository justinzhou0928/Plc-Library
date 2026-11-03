# Changelog

本项目遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 新增

- `DriverResult.SourceTimestamp` / `DriverResult.ServerTimestamp`：透传协议返回的设备侧/服务器侧时间戳；OPC UA 读与订阅填充，其他协议保持 `null`，`Timestamp` 仍为库采集时间
- 指标 `plc.points.bad`（按 `device.id`/`quality`）、`plc.reconnects`、`plc.reconnect.failures`，补齐 Bad 质量与重连可观测性
- 标签浏览：可选驱动接口 `ITagBrowser` + 设备访问器 `IDeviceBrowser`，`OpcUaDriver` 支持浏览 Objects 文件夹/指定 NodeId 的直接子节点，返回 `TagDescriptor`（地址/名称/类型/可写/是否文件夹）
- `ProtocolDriverBase<TClient>` 驱动基类：统一 `DriverStatus`/状态锁、客户端取用/分离、`Faulted` 标记、`TryReconnectAsync` 与释放模板；S7/Omron/Mitsubishi/Allen-Bradley/BACnet/OPC UA 已迁移，Modbus 保留专用基类

### 修复

- **推送采集看门狗误判**：`PushCollector` 在订阅存活但值长期不变时不再因“5 分钟无数据”被重建；改为 30s 健康检查刷新心跳，并在驱动 `Faulted`/`Disconnected` 时主动结束、交由监督循环重建
- **共享池键**：`DeviceDriverPool` 的连接池键纳入 `ConnectionTimeout`，同连接串但借用超时不同的设备不再共用按首设备创建的池
- **BACnet 读写超时**：`timeout` 不再只用于 WhoIs；ReadPropertyMultiple/WriteProperty 均套时间上限，慢设备/不响应会置 `Faulted` 而不是长时间挂起
- **连接串未知键静默忽略**：`ConnectionStringBinder` 现在校验键名（配置类型公开属性 + 显式别名），拼写错误在构造驱动配置时抛出并列出允许键
- **连接串值截断/空格丢失**：值支持单/双引号包裹，保留 `;`、冒号和首尾空格；引号内同种引号双写转义；未加引号的值仍 Trim
- **IO 级无断路器**：`ReadAsync`/`WriteAsync` 增加按设备独立的断路器，坏设备在连续失败后快速失败，不再每周期重试到超时
- **OPC UA 推送非法地址**：`StartPushingAsync` 对非法 `NodeId` 产出 `BadConfigError`，不再只写日志后静默丢弃

### 变更（不兼容）

- **S7 标量 `DataType` 生效**：配置 `DBW`+`short`、`DBD`+`float` 等类型时按配置解码，不再按地址推断；未配置时保持原行为。依赖 `DBD` 恒为 `uint` 的调用方需检查点位配置；`long`/`ulong` 等无对应 S7.Net 标量 `VarType` 的类型返回 `BadConfigError`
- **连接串未知键不再静默忽略**：未知键会抛 `InvalidOperationException`，并列出允许键；自定义驱动如使用非属性名键，需通过 `ConnectionStringBinder.Bind<T>(cs, "alias")` 显式声明

### 文档

- 新增 [docs/SCOPE.md](docs/SCOPE.md)：库职责与 SCADA 职责边界、本轮落地项与剩余补齐优先级
- README/DEVELOPMENT 同步时间戳、指标、推送心跳与已知限制

## [1.3.5] - 2026-11-03

### 修复

- **配置校验在启动期生效**：`PoolOptions` / `PipelineOptions` / `MonitorOptions` 的 DataAnnotations 之前只调用了 `ValidateOnStart`、没有 `ValidateDataAnnotations`，非法值（如 `MaxRetryAttempts=0`、`Pipeline.Capacity=0`）会通过启动校验并在运行期才抛异常；现在宿主启动即失败并给出字段名
- **连接池把未连接驱动交给调用方**：`TryReconnectAsync` 返回 `false` 时驱动仍可能是 `Faulted`/`Disconnected`，`DeviceSharedPool.AcquireAsync` 现在校验最终状态，失败即销毁驱动并抛出，避免每次读写都在驱动内部失败
- **停机与热更新竞态**：`ApplyDevicesAsync` 在创建推送驱动时若与 `StopSchedulerAsync` 并发，停止后仍可能启动一个无人回收的执行器；现在启动后检查 `_stopping` 并立即停止，新增回归用例
- **Omron 位后缀静默错值**：数值点位（如 `D100.5` + `short`）之前会忽略 `.5` 并读到整字；bool 位号 >15 也会被底层接受。现在均返回 `BadConfigError` 且不产生线上请求
- **OPC UA 订阅 Bad 质量被当作 Good**：`DataValue.StatusCode` 为 Bad 但 `Value` 非 null 时，之前会以 `Good` 推送；现在校验 StatusCode，Bad 状态进入数据流为 `BadCommFailure`
- **OPC UA 连接/读写故障状态**：`ConnectAsync` 失败现在置 `Faulted`；Read/Write 的传输级异常也置 `Faulted`，使池/监督循环能重建；非法 `NodeId` 返回 `BadConfigError`
- **OPC UA 订阅生命周期**：`StopPushingAsync` 从 Session `RemoveSubscription`，`DisconnectAsync` 解绑 KeepAlive 处理器，避免热更新反复订阅累积
- **AB 空 CIP 载荷越界**：`TryDecodeCipValue` 在只有 2 字节类型码、无数据时先返回失败，避免 `payload[0]` 越界
- **S7 批量读失败回退**：传输级批量读失败后不再逐点重读（避免 N×`timeout`），整批标 Bad 并置 `Faulted`；地址解析失败改为本地 `BadConfigError`，不再伪装成通信错误
- **可观测性补齐**：`DeviceSharedPool.AcquireAsync` 现在发出文档中声明的 `PlcLibrary.Acquire` Activity

### 变更（不兼容）

- 非法 Omron 位后缀（数值点位 `.bit` / bool 位号 >15）、S7 读地址解析失败、OPC UA 非法 `NodeId` 现在返回 `BadConfigError`（原先分别是静默值/`BadCommFailure`/`BadCommFailure`）；依赖原错误码分类的上层需同步调整
- `PoolOptions.MaxRetryAttempts=0`、`Pipeline.Capacity=0` 等非法 Options 现在在宿主启动时直接失败，不再延迟到首次设备下发或管道初始化

### 测试

- 新增回归：池重连失败、停机/Apply 竞态、Omron 位后缀、OPC UA Bad 状态与非法地址、Options 启动校验；全量测试通过（数量以 `dotnet test` 为准）

### 构建

- 核心包新增 `Microsoft.Extensions.Options.DataAnnotations` 依赖，用于启动期 Options 校验；生产包不引入测试专用依赖
- CI 增加 Windows job 与 `-warnaserror` 构建，覆盖 Linux/Windows 双平台

### 文档

- 新增 `docs/ASSESSMENT-2026-11-03.md` 评估报告；README/DEVELOPMENT 更新 Options 校验、`plc.pipeline.dropped` 口径、S7 批量读回退与推送看门狗限制

## [1.3.4] - 2026-11-02

### 构建

- CI 的可选 NuGet.org 发布失败改为 warning 输出，不再产生 failure annotation；GitHub Packages 发布保持成功

## [1.3.3] - 2026-11-02

### 构建

- NuGet.org 发布改为尽力而为：`NUGET` 密钥存在但推送失败时记录 warning，不再让 tag CI 失败；GitHub Packages 仍作为发布源

## [1.3.2] - 2026-11-02

### 构建

- **NuGet.org 发布改为按需执行**：只有配置 `NUGET` secret 时才执行 `dotnet nuget push`，未配置时给出 notice 并跳过；避免 tag CI 因缺失发布密钥失败。GitHub Packages 发布保持自动执行

## [1.3.1] - 2026-11-02

### 修复

- **Linux CI 下 BACnet 测试模拟器端口冲突**：模拟器改用 SDK 的 shared + exclusive UDP 端口模式（shared `0.0.0.0:Port` + exclusive `127.0.0.2:Port`），不再用 `useExclusivePort:true`，避免 Ubuntu 上 `Address already in use`；11 个 BACnet 模拟器用例在 Ubuntu 通过

### 构建

- CI 测试失败时上传 TRX，并把失败测试名/错误写入 GitHub annotation，便于定位平台差异

## [1.3.0] - 2026-11-02

### 修复

- **长期运行后采集卡死（现场问题）**：连接池的 IO/连接超时现在都有硬兜底（Polly 超时后再等 2s），底层调用忽略取消令牌时也会返回；超时驱动不再放回池，直接丢弃并释放槽位。修复 `Return` 与空闲回收并发时可能泄漏信号量/驱动的问题，以及驱动 `DisposeAsync` 卡住会拖住归还/清理的问题（改为有界释放 + 后台释放）
- **采集器心跳与卡死重启**：`PollingCollector`/`PushCollector` 记录最后进展时间；轮询超过 `max(4×采集周期, 30s)`、推送 5 分钟无进展时，监督循环按退避重建执行器；`TaskActuator.StopAsync` 有界等待，循环不响应取消时强制释放采集器并放弃旧循环，避免监督/停机永久等待
- **管道/处理器卡死**：handler 超时改为硬上限（忽略取消也不会卡住分发）；`PipelineOptions.EnqueueTimeout`（默认 5s）限制写入队列的等待，消费者不前进时丢弃该点并计入 `plc.pipeline.dropped`，采集循环不会被反压永久卡住
- **监督循环自愈**：单次监督异常不再导致监督循环退出；健康状态会把“运行但无进展”的设备报为 `Faulted`
- **AllenBradley 批量读静默错值（严重）**：`TagClient.ReadMultipleAsync` 返回的每个嵌入式 Read Tag 应答以 2 字节 CIP 数据类型码开头，`AllenBradleyDriver.TryDecodeCipValue` 原先从第 0 字节开始解码，导致 DINT/REAL 整体错位、BOOL 恒为 `true`，且质量仍为 `Good`。现改为跳过并校验 2 字节类型码（BOOL/SINT/INT/DINT/LINT/USINT/UINT/UDINT/ULINT/REAL/LREAL），类型不匹配时返回 `BadCommFailure` 而不是解出垃圾值
- **Mitsubishi 响应键串点（严重）**：Snet 结果字典以 `AddressName` 为键，原实现先按 `TagId` 查找，TagId 与另一点位地址同名时会返回**另一个点位**的值（`Good`）。现优先按 `Address` 命中，仅在没有任何点位把 `TagId` 当作地址时才回退到 `TagId`；同时修复 TagId 与 Address 仅大小写不同时误报 `Point not found in response`
- **BACnet 逐对象错误与响应缺项被当成 Good（严重）**：RMP-ACK 中逐对象的 `BacnetError` 现在降级为 `BadCommFailure`；响应省略某个请求对象时不再返回数组默认值（`Status=Good`、`Address/Value=null`），而是返回带地址的 `BadCommFailure("No response for object")`
- **BACnet 远端端口被忽略**：`BacnetAddress(IP, address, network)` 的第三个参数是网络号而非端口，原实现把 `port` 传进去导致远端永远连 47808。现把端口编码进地址字符串（`ip:port`），并补齐非标准端口回归测试
- **BACnet 写/读的参数错位**：`WritePropertyAsync` 的参数顺序是 `invokeId, priority, arrayIndex, ct`，原实现把配置的 `timeout` 当作 `arrayIndex` 发送；`ReadPropertyMultiple` 也显式传了 array index 0。现统一使用 `BACNET_ARRAY_ALL (0xFFFFFFFF)`
- **推送设备连接失败后永久僵死、且拖垮宿主停机（高危）**：`TaskActuator` 现在捕获全部异常并记录 `LastError`，`StopAsync` 不再向上抛；`TaskScheduler` 新增监督循环（默认 5s），对已停止的执行器按指数退避（50ms→60s）重建采集器与驱动；`TaskSchedulerHost.StopAsync` 先取消监督循环再停止执行器
- **串口 Modbus 端口打不开时驱动长期停在 Connected**：`ModbusRtuDriver`/`ModbusAsciiDriver` 在 `ConnectAsync` 阶段即打开串口，端口不存在/被占用会立即失败并置 `Faulted`，连接池据此重建，而不是复用永远失败的连接
- **Mitsubishi 字软元件 bool 访问落到错误位**：Snet 对 `D110` + bool 发 MC 位写命令（head=110，按规范等价 `D6.14`），读取则按整字转换。驱动层现在统一「读字 + 取 bit」，写入补全为 `D110.<bit>` 走读改写（read-modify-write）；位号 0–15 之外返回 `BadConfigError`
- **AB `timeout` 配置不生效**：`AllenBradleyDriver` 现在为 Connect/Read/Write 的每次会话操作套上配置的 `timeout`，超时转换为 `TimeoutException`（传输级）并置 `Faulted`；对端"接受连接但不响应"不再无限等待
- **未知 `DataType` 静默回退 `int`**：Allen-Bradley / Omron / Mitsubishi 改用 `DataTypeMapper.TryResolve`，未知类型返回 `BadConfigError`（不再按 Int32 误读）
- **池回收与并发借用竞态**：`DeviceSharedPool` 增加原子「回收中」标记 + `PoolRecycledException`，`DeviceDriverPool.AcquireAsync` 捕获后重新取池重试；清理线程不会在借用期间销毁信号量/驱动队列
- **S7 批量写回退可能重复写**：先逐点构造 `DataItem`（非法地址只标 `BadConfigError`，不进入批量）；单点非 string 直接逐点写；批量失败时传输级故障不再逐点重写，仅协议/数据级失败才逐点隔离（绝对值写入幂等）

### 变更（不兼容）

- **Mitsubishi `D`/`W`/`R`/`ZR` 上的 bool 点位语义变化**：`D110 + DataType=bool` 现在等价 `D110.0`（bit 0），读写都按 bit 语义；1.2 及以前由底层库按整字处理，读写语义不一致且写会落到错误位。需要整字真值判断请改用 `DataType=ushort` 并自行比较。升级前请检查现有 Mitsubishi 布尔点位配置

### 性能与精简

- **Omron FINS 批量读**：同存储区连续字合并为一次 `Read`、连续位合并为一次 `ReadBit`（空隙容忍 64 位，单请求上限 480 字/256 位），按组隔离错误；8 个点位由 8 次请求降为 2 次
- **Mitsubishi 批量读**：同设备/同类型连续地址合并为一次 Snet 数组读（`Int16Array`/`UInt16Array`/`Int32Array`/`FloatArray`/`Int64Array`/`DoubleArray`），5 个连续 short 由 5 次请求降为 1 次；字软元件 bool 按字数组读 + 取位，位软元件逐点
- **Modbus TCP 异步连接**：`ModbusDriverBase` 新增异步工厂重载（保留原同步重载以兼容既有子类）；TCP 在 `ConnectAsync` 中异步连接并由 `timeout` 约束，不再阻塞线程池线程等待系统默认超时
- **管道分发快路径**：单 `IDataHandler`（最常见）直接调用，去掉每点位 `Parallel.ForEachAsync` + 链接 `CancellationTokenSource` 的分配；handler 超时与异常隔离语义不变
- **监控缓存无锁化**：`PlcMonitor` 由「结果 + 最后活跃时间」两个字典 + 全局锁改为单字典 + CAS 循环，内存减半、去掉全局锁竞争；淘汰仍用值匹配删除，避免误删并发更新
- **移除弹性管线全局注册表**：`ManagedResiliencePipelineRegistry` / `ResiliencePipelineKeys` 删除，管线由 `DeviceSharedPool`/`PushCollector` 自持，消除跨设备可变全局状态与热更新漏删风险（`Controller` 不再依赖 `DriverPool.Engine` 内部类型）
- **池清理改用 `PeriodicTimer`**：`DeviceDriverPool` 不再使用 `System.Threading.Timer` 与"发射后不管"的任务；清理异常按周期记录并重试，停机时等待清理循环退出
- **Modbus 编解码改用 BCL `TypeCode`**：删除自定义 `ModbusValueKind`，`TryGetKind` 直接返回 `Type.GetTypeCode(type)`（仍是 `TypeCode` 子集）；字长/编解码逻辑与拒绝 `byte`/`sbyte` 的行为不变
- **常量策略收敛**：不再为协议名等固定且单次使用的值建常量类（`[ProtocolDriverName("S7")]` 等直接内联）；配置默认值留在各自 `*DriverConfig`/`*Options`；单类算法常量用类内 `private const`；Meter/ActivitySource 标识统一由 `PlcMetrics.Meter` 提供（`ActivitySource` 复用其 `Name`/`Version`），不再保留独立常量类。默认值与行为不变

### 测试

- 新增多套离线协议服务端与端到端测试，覆盖 Modbus TCP/UDP/串口失败路径、S7、Omron FINS、Mitsubishi MC 3E、Allen-Bradley EtherNet/IP+CIP、BACnet/IP、OPC UA 真实服务端数据面（会话/读/订阅）；另补充推送驱动失败与监督重启、`PoolOptions` 与 Polly 约束一致性、池回收竞态、AB 超时回归
- 新增池/管道/采集器稳定性用例：驱动/连接/释放忽略取消时的硬超时与槽位恢复、回收与归还并发、handler 忽略取消、队列写入超时、执行器卡死停止，以及带周期传输故障的加速浸泡；全量测试在本次变更后通过（以 `dotnet test` 输出为准）

### 文档

- `README.md`：补充 Mitsubishi 字软元件 bool 语义（不兼容说明）、BACnet 远端端口、采集任务监督、Modbus TCP 异步连接、批量读能力表（Omron/Mitsubishi 已支持批量）
- `DEVELOPMENT.md` / `AGENTS.md`：更新池生命周期（管线自持、PeriodicTimer、回收竞态）、采集任务监督、批量读设计、离线测试矩阵与「无法离线验证」范围

## [1.2.0] - 2026-10-21

### 新增

- **Modbus 多寄存器类型解码/编码**：`TagPointConfiguration` 新增 `Length`、`ByteOrder`，`ModbusDriverBase` 按 `DataType` 计算 WordCount（short/ushort/bool=1，int/uint/float=2，long/ulong/double=4，string=ceil(Length/2)），一个点位只产出一条 `DriverResult`（`string`/`float`/`int`/...）。读写共用 `ModbusValueCodec`，支持 16/32/64 位及任意合法字节排列（AB/BA/ABCD/CDAB/BADC/DCBA/ABCDEFGH/GHEFCDAB...）；字符串支持 AB/BA、奇数长度、首个 `\0` 截断
- **Modbus 批量分组按区间合并**：相邻、重叠、包含的多寄存器点位合并为单次请求（如 42251 的 24 字符条码 + 42263 的 float 合并为 14 个寄存器），仍按读 125 / 写 123 寄存器上限切分；写路径检测重叠点位并返回 `BadConfigError`
- `ModbusDriverConfig.ByteOrder` 设备级默认字节序（连接串 `byteorder:CDAB;`），点位级覆盖；位宽不匹配时回退自然序
- `DataTypeMapper.TryResolve` 严格类型解析与 IEC 别名（`int16`/`int32`/`real`/`lreal`/`dint`/`udint`/`word`/`dword` 等）；`Resolve` 保持原有回退语义

### 变更

- **Modbus 开始消费 `DataType`**：此前被忽略的 `DataType` 配置升级后将生效。`DataType = "float"` 从返回 `ushort` 变为 `float` 并占用 2 个寄存器；`DataType = "int"` 占用 2 个寄存器；`DataType = "string"` 必须配置 `Length`，否则返回 `BadConfigError`。不写 `DataType` 的点位行为不变（逐寄存器 `ushort`）
- Modbus 字符串按 ASCII 解码/编码；非 ASCII 写入返回 `BadConfigError`（后续可扩展编码配置）

### 测试

- 342 个测试用例（239 → +103）：`ModbusValueCodec` 的 16/32/64 位字节序、字符串 AB/BA/奇数长度/NUL 截断、编解码往返；批量读的条码单请求、相邻 string+float 合并、重叠 float+ushort、设备级字节序、无效配置；批量写的 float/string 编码、相邻多寄存器合并、重叠写拒绝、超长/非 ASCII；`DataTypeMapper` 别名与严格解析；`ModbusDriverConfig.ByteOrder` 绑定

## [1.1.1] - 2026-10-20

### 修复

- **OPC UA 连接阶段 `BadConfigurationError`**：`CreateApplicationConfig()` 补全 `TrustedIssuerCertificates`，并新增配置项 `PkiIssuerPath`（默认 `PlcLibrary.OpcUa/pki/issuer`）。此前缺少该项，`ValidateAsync(ApplicationType.Client)` 会在任何网络请求之前抛 `TrustedIssuerCertificates StorePath must be specified.`，导致 `ApplyDevicesAsync` 的采集与 `IDeviceAccessor.ReadAsync` 全部失败；`security:None` + 匿名登录也无法规避
- **OPC UA 推送订阅启动即抛 `ArgumentNullException`**：`Opc.Ua.Client 1.5.378.156` 中 `new Subscription(null!, false)` / `new MonitoredItem(null!, false, false)` 会命中“从 template 复制”的重载并抛 `ArgumentNullException (template)`。改用 `SubscriptionOptions` / `MonitoredItemOptions` 构造，`StartPushingAsync` 可正常建立订阅并推送数据
- OPC UA 客户端配置构建抽为 `internal static CreateApplicationConfig(OpcUaDriverConfig)` 并开启 `InternalsVisibleTo`，使证书存储配置可离线校验

### 文档

- README 补充 OPC UA 完整可运行控制台 demo：宿主注册、设备配置、`IDataHandler` 推送处理、`IDeviceAccessor` 主动读，以及 `Protocol = "OPC_UA"` 的精确匹配说明
- README 驱动表补充每个驱动的 `Protocol` 注册名（精确匹配、区分大小写），并补充 OPC UA 完整设备配置示例、四个 PKI 路径字段以及“配置校验先于网络连接”的说明

### 测试

- 新增 OPC UA 离线配置校验测试：断言四项证书存储路径均已绑定，并直接调用 `ValidateAsync(ApplicationType.Client)`，无需真实服务端即可覆盖“配置项漏填”类缺陷
- 新增 OPC UA 订阅构造回归测试：`CreateSubscription` / `CreateMonitoredItem` 使用 options 重载，覆盖采样间隔、队列长度与 `DiscardOldest`
- 测试总数 233 → 239（新增 2 个配置校验 + 1 个 PKI 解析 + 3 个订阅构造用例）

## [1.1.0] - 2026-08

### 新增

- **`PlcLibrary.Monitor` 扩展包**：实时值监控缓存。以 `IDataHandler` 挂接采集管道，按 `(DeviceId, TagId)` 缓存最新值，仅向订阅方推送「值或质量状态发生变化」的点位，屏蔽轮询产生的重复（干扰）消息；支持单点订阅（先返回当前快照、再推变化）与设备级订阅；空闲条目按 `EntryIdleTimeout` 自动清理，避免设备热更新下线后缓存残留
- 点位唯一性采用 `DeviceId` + `TagId` 复合键：同型号 PLC（仅 IP 不同）即使配置相同 `TagId`/协议地址也互不干扰

### 修复

- 新增主键重复校验：`DeviceConfiguration.Validate` 检测同设备内重复 `TagId`；`TaskScheduler.ApplyDevicesAsync` 检测跨设备重复 `DeviceId`（首个生效、后续跳过并记日志），避免以 `DeviceId`/`(DeviceId, TagId)` 为主键的组件静默碰撞

### 测试

- 233 个测试用例（原 213 → +20），新增：缓存读取/快照、复合键跨设备隔离、单点订阅去重、质量状态变化推送、设备级订阅、空闲条目回收、宿主集成（含「不变值不再重复推送」端到端断言）、并发同值只通知一次、取消传播、真实 Modbus TCP 回环（单点/设备级/多设备同地址隔离）、重复 TagId/DeviceId 校验

## [1.0.4] - 2026-08

### 修复

- `DriverResultPipeline.DispatchAsync` 不再原地修改共享的 `ParallelOptions.CancellationToken`，改为每次分发构造局部实例：消除潜在的并发分发令牌竞争（此前依赖单消费者循环隐式保证安全）

### 测试

- 新增消费者取消令牌向 handler 传播的回归测试（213 个用例，原 212 → +1）

## [1.0.3] - 2026-08

### 新增

- **S7 字符串读取/写入**：`S7Driver` 识别带长度的地址（`DB6000.DBB504.100`）与 `DataType="string"` 点位，构造 `DataItem{S7String}` 融入现有批量读/写管道（s7netplus 原生解析 2 字节头，读 `len+2` 字节）；字符串地址必须带长度，`DataType="string"` 且地址无长度时明确报配置错误
- S7 项目开启 `InternalsVisibleTo`（驱动内部逻辑可离线测试）

### 测试

- 212 个测试用例（原 192 → +20），新增：S7 字符串地址解析（合法/非法）、`IsStringPoint`/`CreateDataItem` 构造行为、`S7String` 头格式往返验证

## [1.0.2] - 2026-08

> 注：1.0.1 未发布（推送前版本号直接提升至 1.0.2），下述变更全部属于本版本。

### 修复

- **Modbus 批量读结果 Address 恒为空**（P0 数据缺陷）：成功读取的点位不再丢失地址
- **断线永不重连**：驱动捕获传输级故障（Socket/IO/超时等）时自动置 `Faulted`，连接池丢弃坏驱动并在下次采集重建，断线自动重连真正生效
- **Modbus 地址溢出回绕**：地址数值超过 65536 时返回配置错误，不再静默回绕到错误寄存器
- **Modbus PDU 上限**：连续地址批量读/写按协议上限自动切分（读寄存器 ≤125、线圈 ≤2000；写寄存器 ≤123、线圈 ≤1968），不再整组失败
- **Modbus 负数写入**：按二进制补码写入（-1 → 0xFFFF），不再抛溢出异常
- **S7/三菱/欧姆龙/AB/BACnet `ConnectAsync` 失败泄漏与状态机**：失败路径统一释放连接对象并置 `Faulted`，不再卡在 `Connecting` 或泄漏 socket
- **IO 超时串流风险**：IO 超时后强制断开驱动，下次获取走重连路径，避免复用状态未知的连接
- **连接池停机边角**：池销毁后晚到的驱动归还不再抛 `ObjectDisposedException`
- **OPC UA `security` 配置生效**：映射 `MessageSecurityMode`（None/Sign/SignAndEncrypt），端点实际安全模式低于配置时拒绝连接，不再静默降级为明文

### 新增

- **Modbus RTU / ASCII 串口支持**：基于 [NModbus.Serial](https://www.nuget.org/packages/NModbus.Serial)（与主包同版本），`ModbusRtuDriver`/`ModbusAsciiDriver` 可用；支持 `BaudRate`/`Parity`/`DataBits`/`StopBits`/`Timeout` 配置
- **AllenBradley / BACnet 批量读**：`ReadMultipleAsync`（CIP 原始字节解码）与 `ReadPropertyMultipleAsync`（按对象分组），批量失败自动回退逐点
- **连接池空置回收**：`PoolOptions.PoolIdleTimeout`（默认 10 分钟，0 禁用），热更新移除设备后连接与弹性管线自动释放，不再累积
- **分布式追踪**：`ActivitySource("PlcLibrary")` 埋点（ReadAsync/WriteAsync/Acquire/Dispatch 四类 span），宿主接 OpenTelemetry 即可链路追踪
- **指标设备标签**：读/写/获取/管道指标携带 `device.id`/`device.protocol` 标签
- **`TransportFailureDetector`**：统一传输级故障判定，供各驱动与后续协议扩展复用
- **配置-消费闭环测试**：`DriverConfigConsumptionTests` 断言每个驱动配置字段都被驱动源码消费，防止"文档声称可配、代码未生效"类问题

### 测试

- 192 个测试用例（原 156 → +36），覆盖：Modbus 地址保留/溢出回绕/PDU 切分/负数写/Faulted 淘汰重建、池空置回收、IO 超时强制断开、串口参数映射、S7 连接失败状态、配置-消费闭环等

### 破坏性变更

- 无公开 API 破坏
- `PoolOptions` 新增 `PoolIdleTimeout`（默认值向后兼容）
- 内部：连接级弹性管线键从 `Pool:{deviceId}` 调整为跟随连接池（`Pool:{protocol}|{connectionString}`），纯内部实现细节，外部不可见
