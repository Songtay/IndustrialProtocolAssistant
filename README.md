# 工业协议调试助手 · Industrial Protocol Assistant

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4?logo=windows&logoColor=white)]()
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen)]()

> 一个基于 **.NET 8 + WPF** 的工业协议调试与轻量监控工具。支持 **Modbus TCP / Modbus RTU / OPC UA / MQTT / S7 / SerialFree（串口自由协议）/ Socket（TCP 自由协议）** 共七种主站协议的读写调试，内置实时曲线、历史查询、阈值报警与报文监控，并附带配套模拟器——**无需真实 PLC 或串口设备即可完成端到端联调**。

---

## 目录

- [功能特性](#功能特性)
- [支持的协议](#支持的协议)
- [技术栈](#技术栈)
- [架构设计](#架构设计)
- [快速开始](#快速开始)
- [使用指南](#使用指南)
- [自由协议 Tag 地址语法](#自由协议-tag-地址语法)
- [内置模拟器](#内置模拟器)
- [项目结构](#项目结构)
- [构建与测试](#构建与测试)
- [贡献指南](#贡献指南)
- [路线图](#路线图)
- [许可证](#许可证)

---

## 功能特性

- **七种协议主站，下拉即切换**：Modbus TCP / Modbus RTU / OPC UA / MQTT / S7 / SerialFree / Socket。驱动自描述连接参数，切换协议时界面动态生成表单，无需改动代码。
- **连接管理**：连接 / 断开 / 断线自动重连（2s 退避）/ 可调轮询周期。
- **读写调试**：支持 Bool / Int16 / UInt16 / Int32 / UInt32 / Float / Double / String（OPC UA 额外支持 Auto 自动识别）。写值按协议能力下发：Modbus 功能码、S7 写位 / 写字节 / 写 DB、MQTT 发布、SerialFree / Socket 的 `{value}` 模板。
- **Tag（数据点）管理**：地址按协议自适应（寄存器号 / NodeId / Topic / DB 地址 / 请求帧模板），支持上下限阈值报警与可选的独立写地址。
- **实时监控**：Tag 实时值订阅，带 `Quality` 质量位与 `Timestamp`；LiveCharts2 实时趋势曲线。
- **历史曲线**：基于 SQLite 时序库按时间范围取数，**一键导出 CSV**。
- **手动原始帧收发**：串口助手式的「任意 Hex 请求 → 原始应答」，支持自动补帧（MBAP / CRC16 / 设备级校验）。
- **报文监控**：以 TX / RX 方向展示各驱动的载荷报文与可读文本。
- **值变化死区**：数值型采样相邻变化小于阈值时跳过落库与刷新，避免无效数据堆积。
- **配置持久化与导入导出**：设备参数与 Tag 配置存入 SQLite（`system-config.db`），支持导出 / 导入 JSON 配置文件用于跨机器备份。
- **日志**：基于 Serilog 按日滚动，界面内可查看。
- **配套模拟器**：Modbus / SerialFree / Socket / MQTT / S7 协议均可无实物联调（详见[内置模拟器](#内置模拟器)）。

> 界面截图可放入 `docs/images/` 目录并在本节引用，例如 `![主界面](docs/images/main-window.png)`。

---

## 支持的协议

| 协议 | 连接参数 | Tag 地址形态 | 手动原始帧 |
|---|---|---|---|
| **Modbus TCP** | IP / 端口 / 从站号 | 寄存器号 | 支持 |
| **Modbus RTU** | 串口 / 波特率 / 从站号 | 寄存器号 | 支持 |
| **OPC UA** | 端点 URL / SecurityPolicy / 用户名密码 | NodeId | 不支持（高层语义） |
| **MQTT** | Broker 地址 / ClientId / 认证 / QoS / 写后缀 | 订阅 Topic | 不支持（高层语义） |
| **S7** | IP / 端口 / CPU 型号 / 机架号 / 插槽号 | DB / M / I / Q 地址 | 不支持（高层语义） |
| **SerialFree** | 串口 / 波特率 / 校验 / 帧间隔 / 帧校验 | 请求帧模板（Hex） | 支持 |
| **Socket** | IP / 端口 / 响应超时 / 帧校验 | 请求帧模板（Hex） | 支持 |

---

## 技术栈

| 层面 | 技术 |
|---|---|
| 运行时 | .NET 8 (LTS) |
| 界面 | WPF + CommunityToolkit.Mvvm（MVVM） |
| 协议 | NModbus、OPCFoundation.NetStandard.Opc.Ua.Client、MQTTnet、S7netplus、自研自由协议帧解析 |
| 图表 | LiveChartsCore.SkiaSharpView.WPF |
| 日志 | Serilog + File Sink |
| 存储 | Microsoft.Data.Sqlite |
| 测试 | xUnit |

---

## 架构设计

### 分层结构

| 层 | 项目 | 职责 | 依赖 |
|---|---|---|---|
| 领域层 | `Core` | 实体与契约：`TagValue` / `TagDefinition` / `DeviceConfig` / `AlarmEvent` / `IDeviceDriver` / `DataQuality` | 无 |
| 驱动层 | `Drivers` | 各协议实现 + `DriverFactory`（插件式扩展点） | Core |
| 采集层 | `Acquisition` | `AcquisitionEngine`：后台轮询、断线重连、Channel 输出、报警判断 | Core、Drivers |
| 存储层 | `Storage` | SQLite 时序数据（`TimeSeriesStore`）与配置持久化（`ConfigStore`） | Core |
| 表现层 | `UI` | WPF + MVVM，实时曲线 / 历史查询 / 手动收发 / 报文监控 / 日志 | Core、Drivers、Acquisition、Storage |

### 核心设计

1. **驱动插件化（协议无关）**
   `IDeviceDriver` 只定义 `Connect / Disconnect / ReadTag / WriteTag` 与诊断日志回调。UI 与采集引擎仅通过 `IDeviceDriver` + `TagValue` 交互，不感知具体协议。新增协议只需：实现驱动 → 在 `DriverFactory` 注册，**UI 与采集引擎均无需改动**。

2. **采集引擎（Channel<T> 数据流）**
   每个设备一个轮询循环 `Task`，按 `PollIntervalMs` 读取其下所有 Tag；采样结果写入 `Channel<TagValue>`（多生产者 → 单消费者），解耦采集与 UI / 存储 / 报警。断线时自动重连，保证长期运行稳定。

3. **设备级 IO 闸门**
   串口与自由协议是「一问一答」式通信，轮询读、写值、单点读、手动收发共享同一把设备级 `SemaphoreSlim`，保证同一时刻只有一笔收发在线，避免应答错配。

4. **数据质量语义**
   每个 `TagValue` 携带 `Quality`（Good / Bad / Uncertain / Stale）与时间戳；驱动读取异常标记为 Bad，纯订阅场景标记为 Stale。UI / 存储 / 报警均基于质量位决策。

5. **数据流**

```
[PLC / 串口设备 / 网关]
        │  协议驱动
        ▼
   IDeviceDriver ──ReadTagAsync──▶ TagValue(Quality + Timestamp)
        │
        ▼
  AcquisitionEngine（按设备后台轮询）
        │  Channel<TagValue>          Channel<AlarmEvent>
        ├──────────▶ UI 实时值 / 曲线  ├──▶ 报警列表
        └──────────▶ TimeSeriesStore(SQLite)
```

---

## 快速开始

### 环境要求

- **Windows**（WPF 界面）
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 串口协议调试需要成对的虚拟串口（推荐 [com0com](https://sourceforge.net/projects/com0com/)）

### 克隆与构建

```bash
git clone <your-repo-url>
cd IndustrialProtocolAssistant
dotnet build IndustrialProtocolAssistant.sln
```

### 三步上手（无需真实 PLC）

**① 启动模拟从站**

```bash
cd tools/SimulatedSlave
dotnet run -- 5020
```

输出示例：

```
[SimulatedSlave] Modbus TCP 从站：127.0.0.1:5020   从站 ID : 1
   数据 : 保持寄存器[0-1]=温度(Float), 保持寄存器[2]=计数(UInt16), 线圈[0]=运行状态(Bool)
   按 Ctrl+C 停止...
```

**② 启动 WPF 助手**

```bash
cd src/UI
dotnet run --project IndustrialProtocolAssistant.UI.csproj
```

界面已预置默认连接参数：`IP=127.0.0.1` / `端口=5020` / `从站=1` / `轮询=1000ms`，直接点击 **连接并启动** 即可。

**③ 运行测试**

```bash
dotnet test IndustrialProtocolAssistant.sln
```

### 预置 Tag（与模拟从站数据布局一一对应）

| Tag | 类型 | 地址 | 对应从站存储区 |
|---|---|---|---|
| 温度 | Float | 0 | 保持寄存器 [0..1]（IEEE754，占 2 寄存器） |
| 运行状态 | Bool | 0 | 线圈 [0] |
| 计数 | UInt16 | 2 | 保持寄存器 [2] |

连接后点击「读取」，可看到实时值随从站更新而刷新；温度超过 80 会触发高报。

#### 模拟从站数据布局

- 保持寄存器 `0~1`：温度（Float，IEEE754），20~80°C 随机游走
- 保持寄存器 `2`：计数（UInt16），每周期 +1
- 线圈 `0`：运行状态（Bool），每 20 个周期翻转

默认端口 `5020`（避免与系统 Modbus 标准端口 502 冲突）。

---

## 使用指南

### 连接与轮询

- 界面顶部选择协议后，连接参数表单随驱动动态生成，点击 **连接并启动** 建立连接。
- 连接建立后默认**不自动读取**，需在「数据监控 → 实时监控」点击「读取」才开始轮询，避免连接即产生大量数据。
- 运行中添加 / 删除 Tag 后无需重启；若修改了驱动类型或连接参数，引擎会自动重建驱动实例。

### Tag 管理

- 「数据监控 → 配置管理」中新增 / 编辑 Tag，配置数据类型、协议地址、上下限报警与可选写地址。
- 配置按协议分组保存，切换协议时互不影响。

### 历史曲线与导出

- 在「实时监控 → 历史曲线」选择 Tag 与时间范围（分钟），点击查询即可查看历史趋势。
- 查询结果可 **一键导出为 CSV**。

### 手动原始帧收发

- 「手动收发」页提供串口助手式调试：输入 Hex 请求 → 立即显示原始应答。
- 仅线帧协议（SerialFree / Socket / Modbus RTU / Modbus TCP）可用；S7 / OPC UA / MQTT 为高层语义协议，请使用 Tag 读写。

### 报文监控

- 「报文监控」页以 TX / RX 方向展示驱动上报的载荷报文（如 S7 存储区读写字节、MQTT 消息 payload），便于排查通信细节。

### 配置持久化与导入导出

- 设备参数与 Tag 配置自动保存到 SQLite（`system-config.db`），下次启动自动恢复。
- 支持将当前协议配置导出为 JSON 文件、并从文件导入，便于跨机器备份与共享。

---

## 自由协议 Tag 地址语法

没有标准协议、对端只按「帧头 / 命令 / 校验」应答的设备，可使用 **SerialFree**（串口）或 **Socket**（TCP）驱动。二者的 Tag 地址语法完全一致：

| 语法 | 含义 | 示例 |
|---|---|---|
| `读帧hex` | 轮询读取时发送的请求帧 | `01 03 00 00 00 01 84 0A` |
| `@N` | 回复帧数据从第 N 字节开始（0 基） | `01 03 00 00 00 01 84 0A@4` |
| `\|\| 写帧hex` | 写入时改用写帧（可含 `{value}`） | `01 03 ...@4 \|\| 01 06 00 00 {value} {crc16}` |
| `{value}` | 写入值按 Tag 类型编码（数值大端）替换 | `01 06 00 00 {value} {crc16}` |
| `{crc16}` / `{xor}` / `{sum}` | 自动计算帧校验（基于除自身外整帧） | `AA 55 01 {xor}` |

- **帧尾校验**有两种配置方式：在连接参数「帧校验」下拉选择 CRC16 / XOR / SUM（自动追加到全部读 / 写帧末尾）；或直接在地址中显式写占位符（支持帧内任意位置的校验布局，此时不会重复追加）。
- **回复解析**按 Tag 类型从偏移处取值：Bool = 1 字节（非 0 为真）、Int16 / UInt16 = 2、Int32 / UInt32 / Float = 4、Double = 8（统一大端）；String 为 ASCII，按「长度」截取，遇 `0x00` 结束。
- **回复边界判定**：SerialFree 以「帧间隔」（静默超过该时长）判帧结束；Socket 以固定 30ms 短静默判帧结束。

**Socket 驱动 Tag 示例**（对应 `SocketSimulator` 的 rtu 引擎）：

| Tag | 类型 | 地址 |
|---|---|---|
| 温度 | Float | `01 03 00 00 00 02 {crc16}@3` |
| 计数 | UInt16 | `01 03 00 02 00 01 {crc16}@3` |
| 运行 | Bool | `01 01 00 00 00 01 {crc16}@3` |

写值（计数，UInt16）：`01 03 00 02 00 01 {crc16}@3 || 01 06 00 02 {value} {crc16}`

> 含 `{value}` 的帧段只参与写入，不参与轮询；无串口设备时可先用 com0com 建立虚拟串口对（如 COM3 ↔ COM4）。

---

## 内置模拟器

`tools/` 下提供全套配套模拟器，覆盖所有协议，便于无实物联调。

| 模拟器 | 说明 | 示例命令 |
|---|---|---|
| `SimulatedSlave` | Modbus TCP 从站（可同时开启 TCP + RTU 双通道，共享数据源） | `dotnet run -- 5020` |
| `ModbusRtuSlave` | 标准 Modbus RTU 串口从站（FC01~10 + CRC16） | `dotnet run -- --com COM4 --baud 9600` |
| `SerialFreeSlave` | SerialFree 串口从站（rtu / rule 双引擎） | `dotnet run -- --com COM4 --mode rtu` |
| `SocketSimulator` | Socket（TCP）服务端模拟器（rtu / rule 双引擎） | `dotnet run -- 5020` |
| `MqttSimulator` | 内置 MQTT Broker + 4 台模拟设备自动上报 | `dotnet run -- --port=1883` |
| `S7Simulator` | S7 服务端模拟器（基于 Snap7，模拟 S7-300/400） | `dotnet run -- --port 10200` |

常用参数：

- `SimulatedSlave`：`dotnet run -- 5020`（仅 TCP）、`dotnet run -- 5020 COM3 9600`（TCP + RTU）、`dotnet run -- - COM3 9600`（仅 RTU）。
- `SerialFreeSlave` / `SocketSimulator`：`--mode rtu`（类 Modbus 帧，开箱即用）或 `--mode rule --rules rules.example.json`（自定义规则匹配任意帧协议）。加 `--help` 查看全部用法。
- `MqttSimulator`：交互式命令 `help` / `list` / `publish <topic> <payload>` / `quit`。
- `S7Simulator`：`--selftest` 启动后用 S7netplus 自连验证协议兼容性。

---

## 项目结构

```
IndustrialProtocolAssistant/
├── src/
│   ├── Core/         # 实体 / 接口 / 领域契约（零依赖）
│   ├── Drivers/      # 协议驱动 + DriverFactory（插件式扩展点）
│   ├── Acquisition/  # 采集调度引擎（后台轮询 + Channel<T>）
│   ├── Storage/      # SQLite 时序数据 + 配置持久化
│   └── UI/           # WPF + MVVM 桌面客户端
├── tools/            # 各协议配套模拟器
├── tests/            # xUnit 单元测试
└── docs/             # 架构说明等文档
```

---

## 构建与测试

```bash
# 还原 + 构建整个解决方案
dotnet build IndustrialProtocolAssistant.sln -c Release

# 运行全部单元测试
dotnet test IndustrialProtocolAssistant.sln

# 构建 WPF 客户端
dotnet build src/UI/IndustrialProtocolAssistant.UI.csproj -c Release
```

测试覆盖驱动工厂、Tag 定义、MQTT 驱动（含集成测试）与 SerialFree 协议解析等模块。

---

## 贡献指南

欢迎任何形式的贡献！

1. Fork 本仓库并创建特性分支：`git checkout -b feature/your-feature`
2. 提交前请确保 `dotnet build` 与 `dotnet test` 均通过。
3. 遵循现有代码风格（分层架构、XML 注释、可空引用类型开启）。
4. 提交 Pull Request，并清晰描述改动动机与验证方式。

**新增一个协议驱动**只需三步：

1. 在 `src/Drivers/Driver/` 下实现 `IDeviceDriver`（如需手动收发再实现 `IManualRawDriver`）；
2. 在 `DriverFactory.SupportedTypes` 与 `GetFields` 中声明协议名与连接参数；
3. 在 `DriverFactory.Create` 中注册实例。UI 与采集引擎无需改动。

如发现 Bug 或有功能建议，欢迎提交 Issue。

---

## 路线图

- [ ] S7 驱动补充 S7-200 系列 V 区等地址
- [ ] 驱动插件目录动态加载（无需重新编译即可扩展协议）
- [ ] 时序数据批量落库与降采样（降低 SQLite 写入压力）
- [ ] Rx.NET 流处理（EMA / Kalman 滤波、Throttle / Buffer）
- [ ] OPC UA 证书安全配置增强
- [ ] 可选 Web API 数据转发（REST / SSE）

**已知限制**

- S7 驱动覆盖常见的 DB 块 / M / I / Q 区（位、字节、字、双字、STRING）。
- 图表为「最近采样值」实时刷新，长时间高精度历史请使用历史曲线查询。
- 界面仅支持 Windows。

---

## 许可证

本项目基于 [MIT License](LICENSE) 开源。
