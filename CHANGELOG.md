# Changelog

本项目遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

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
