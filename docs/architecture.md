# 架构说明

## 分层职责

| 层 | 项目 | 职责 | 依赖 |
|---|---|---|---|
| 领域层 | `Core` | 实体（`TagValue`/`TagDefinition`/`DeviceConfig`/`AlarmEvent`）、枚举（`DataQuality`/`TagDataType`）、驱动接口（`IDeviceDriver`） | 无 |
| 驱动层 | `Drivers` | 协议实现（`ModbusTcpDriver`）、驱动工厂（`DriverFactory`） | Core |
| 采集层 | `Acquisition` | 采集调度引擎（`AcquisitionEngine`）：后台轮询、断线重连、Channel 输出、报警判断 | Core, Drivers |
| 存储层 | `Storage` | 时序数据落库（`TimeSeriesStore`，SQLite） | Core |
| 表现层 | `UI` | WPF + MVVM（`MainViewModel`/`TagItem`/`MainWindow`），实时曲线/报警/日志 | Core, Drivers, Acquisition, Storage |

## 核心设计

### 1. 驱动插件化（协议无关）
- `IDeviceDriver` 定义 `Connect/Disconnect/ReadTag/WriteTag`。
- UI/采集引擎只通过 `IDeviceDriver` 与 `TagValue` 交互，**不感知具体协议**。
- 新增协议：实现 `IDeviceDriver` → 在 `DriverFactory` 注册 → 无需改动 UI。

### 2. 采集引擎（BackgroundService + Channel\<T\>）
- 每个设备一个轮询循环 `Task`，按 `PollIntervalMs` 读取其下所有 Tag。
- 采样结果写入 `Channel<TagValue>`（多生产者 → 单消费者），解耦采集与 UI/存储。
- 断线时自动重连（2s 退避），保证长期运行稳定性。
- 报警在引擎内统一判断（阈值 + 质量位），结果写入独立的 `Channel<AlarmEvent>`。

### 3. 数据质量语义
- 每个 `TagValue` 带 `Quality`（Good/Bad/Uncertain/Stale）+ `Timestamp`。
- 驱动读取异常 → `TagValue.Bad`；超时未更新 → 可标记为 Stale（扩展点）。
- UI/存储/报警均基于质量位决策，避免"坏数据当真数据用"。

### 4. UI 响应式更新
- `MainViewModel` 启动两个后台消费者：`ConsumeValuesAsync`（更新 Tag 实时值 + 图表 `ObservableValue`）、`ConsumeAlarmsAsync`（报警列表）。
- 图表使用 LiveCharts2 的 `ObservableValue`，值变更自动重绘，主线程通过 `Dispatcher.Invoke` 更新。

## 数据流
```
[Modbus 从站/PLC]
      │ Modbus TCP
      ▼
ModbusTcpDriver ──ReadTagAsync──▶ TagValue(Quality+Ts)
      │
      ▼
AcquisitionEngine (后台轮询 Task)
      │ Channel<TagValue>          Channel<AlarmEvent>
      ├──────────▶ UI 实时值/曲线   ├──▶ 报警列表
      └──────────▶ TimeSeriesStore(SQLite)
```

## 扩展点
- **新协议**：实现 `IDeviceDriver` + `DriverFactory` 注册（OPC UA 推荐 `OPCFoundation/UA-.NETStandard`）。
- **流处理**：用 Rx.NET（`IObservable`）+ `Where/Throttle/Buffer` 做高频降采样、EMA/Kalman 滤波。
- **批量落库**：`Channel<T>` + 批处理（`BatchAsync`）降低 SQLite 写入压力。
- **Web 转发**：新增 `ASP.NET Core Web API` 项目，从 `Channel` 订阅并暴露 REST/SSE。
