using IndustrialProtocolAssistant.Core;
using IndustrialProtocolAssistant.Drivers.Driver;

namespace IndustrialProtocolAssistant.Drivers;

/// <summary>
/// 驱动工厂：负责两件事
/// 1) SupportedTypes + GetFields：向 UI 声明支持的协议及各协议的连接参数（驱动自描述，UI 动态表单的数据源）
/// 2) Create：根据 DeviceConfig.DriverType 创建对应驱动实例
/// 新增协议只需在此注册，UI 与采集引擎均无需改动。
/// </summary>
public static class DriverFactory
{
    /// <summary>UI 驱动下拉框的候选列表（新增协议只需在此注册）。</summary>
    public static string[] SupportedTypes { get; } = { "ModbusTcp", "ModbusRtu", "SerialFree", "Socket", "OpcUa", "Mqtt", "S7" };

    /// <summary>某驱动类型需要哪些连接参数（UI 据此生成动态表单）。</summary>
    public static IReadOnlyList<ConnectionField> GetFields(string driverType) => driverType switch
    {
        "ModbusTcp" =>
        [
            new("Host", "IP 地址", ConnectionFieldKind.Text, DefaultValue: "127.0.0.1",
                ToolTip: "目标设备 IP，如 192.168.1.10", Width: 120),
            new("Port", "端口", ConnectionFieldKind.Int, DefaultValue: "5020",
                ToolTip: "Modbus TCP 端口，默认 502", Width: 60),
            new("SlaveId", "从站号", ConnectionFieldKind.Int, DefaultValue: "1",
                ToolTip: "从站地址 1~247", Width: 60),
        ],
        "ModbusRtu" =>
        [
            new("SerialPort", "串口", ConnectionFieldKind.SerialPort,
                ToolTip: "选择或直接输入串口名，如 COM3；点击右侧按钮刷新可用列表", Width: 90),
            new("BaudRate", "波特率", ConnectionFieldKind.Select, DefaultValue: "9600",
                Options: ["1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200"],
                ToolTip: "与对端设备保持一致", Width: 80),
            new("SlaveId", "从站号", ConnectionFieldKind.Int, DefaultValue: "1",
                ToolTip: "从站地址 1~247", Width: 60),
        ],
        "SerialFree" =>
        [
            new("SerialPort", "串口", ConnectionFieldKind.SerialPort,
                ToolTip: "选择或直接输入串口名，如 COM3；点击右侧按钮刷新可用列表", Width: 90),
            new("BaudRate", "波特率", ConnectionFieldKind.Select, DefaultValue: "9600",
                Options: ["1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200"],
                ToolTip: "与对端设备保持一致", Width: 80),
            new("DataBits", "数据位", ConnectionFieldKind.Select, DefaultValue: "8",
                Options: ["8", "7"],
                ToolTip: "常见 8；老式仪表可能为 7（一般配偶校验）", Width: 55),
            new("Parity", "校验位", ConnectionFieldKind.Select, DefaultValue: "无",
                Options: ["无", "偶校验", "奇校验"],
                ToolTip: "常见 无校验(8N1)；7E1 场景选偶校验", Width: 70),
            new("StopBits", "停止位", ConnectionFieldKind.Select, DefaultValue: "1",
                Options: ["1", "2"],
                ToolTip: "常见 1；老式设备可能为 2", Width: 55),
            new("ResponseTimeoutMs", "响应超时", ConnectionFieldKind.Int, DefaultValue: "1000",
                ToolTip: "发出请求帧后，等待从站完整应答的最长时间（毫秒）", Width: 80),
            new("FrameGapMs", "帧间隔", ConnectionFieldKind.Int, DefaultValue: "20",
                ToolTip: "收到数据后静默超过该时长视为一帧结束（毫秒），用于无定长的自定义协议", Width: 70),
            new("FrameChecksum", "帧校验", ConnectionFieldKind.Select, DefaultValue: "无",
                Options: ["无", "CRC16", "XOR", "SUM"],
                ToolTip: "帧尾校验：按“除自身外整帧”计算并自动追加到该设备全部 Tag 的读/写帧末尾；Tag 地址中已显式写 {crc16}/{xor}/{sum} 占位符时不会重复追加，选“无”则地址原样渲染", Width: 75),
        ],
        "Socket" =>
        [
            new("Host", "IP 地址", ConnectionFieldKind.Text, DefaultValue: "127.0.0.1",
                ToolTip: "目标设备/服务器 IP，如 192.168.1.10", Width: 120),
            new("Port", "端口", ConnectionFieldKind.Int, DefaultValue: "5020",
                ToolTip: "设备监听端口（自定义 TCP 服务，配套模拟器 SocketSimulator 默认 5020）", Width: 60),
            new("ResponseTimeoutMs", "响应超时", ConnectionFieldKind.Int, DefaultValue: "1000",
                ToolTip: "发出请求帧后，等待完整应答的最长时间（毫秒）", Width: 80),
            new("FrameChecksum", "帧校验", ConnectionFieldKind.Select, DefaultValue: "无",
                Options: ["无", "CRC16", "XOR", "SUM"],
                ToolTip: "帧尾校验：按“除自身外整帧”计算并自动追加到该设备全部 Tag 的读/写帧末尾；Tag 地址中已显式写 {crc16}/{xor}/{sum} 占位符时不会重复追加，选“无”则地址原样渲染", Width: 75),
        ],
        "OpcUa" =>
        [
            new("EndpointUrl", "端点 URL", ConnectionFieldKind.Text, DefaultValue: "opc.tcp://127.0.0.1:4840",
                ToolTip: "OPC UA 服务器端点，如 opc.tcp://192.168.1.10:4840", Width: 170),
            new("SecurityPolicy", "安全策略", ConnectionFieldKind.Select, DefaultValue: "None",
                Options: ["None", "Sign", "SignAndEncrypt"],
                ToolTip: "OPC UA 安全策略", Width: 110),
            new("Username", "用户名", ConnectionFieldKind.Text, Required: false,
                ToolTip: "留空使用匿名认证", Width: 100),
            new("Password", "密码", ConnectionFieldKind.Password, Required: false,
                ToolTip: "与用户名配对使用，仅用于身份认证", Width: 100),
        ],
        "Mqtt" =>
        [
            new("BrokerUrl", "Broker 地址", ConnectionFieldKind.Text, DefaultValue: "mqtt://127.0.0.1:1883",
                ToolTip: "MQTT Broker 地址，如 mqtt://broker.emqx.io:1883", Width: 150),
            new("ClientId", "客户端 ID", ConnectionFieldKind.Text, Required: false, DefaultValue: "IpaClient",
                ToolTip: "MQTT 客户端标识，留空自动生成", Width: 110),
            new("Username", "用户名", ConnectionFieldKind.Text, Required: false,
                ToolTip: "留空使用匿名连接", Width: 90),
            new("Password", "密码", ConnectionFieldKind.Password, Required: false,
                ToolTip: "与用户名配对使用", Width: 90),
            new("QoS", "QoS", ConnectionFieldKind.Select, DefaultValue: "0",
                Options: ["0", "1", "2"],
                ToolTip: "消息服务质量等级：0=最多一次，1=至少一次，2=恰好一次", Width: 50),
            new("SetTopicSuffix", "写后缀", ConnectionFieldKind.Text, Required: false, DefaultValue: "/set",
                ToolTip: "未配置发送 Topic 的 Tag：写值发布到「订阅 Topic + 写后缀」；留空则直接发布到订阅 Topic。Tag 已单独配置发送 Topic 时以此字段为准，本后缀不生效", Width: 60),
        ],
        "S7" =>
        [
            new("Host", "IP 地址", ConnectionFieldKind.Text, DefaultValue: "192.168.0.1",
                ToolTip: "S7 PLC 的 IP 地址，如 192.168.0.1", Width: 120),
            new("Port", "端口", ConnectionFieldKind.Int, DefaultValue: "102",
                ToolTip: "S7 协议端口，默认 102", Width: 60),
            new("CpuType", "CPU 型号", ConnectionFieldKind.Select, DefaultValue: "S7300",
                Options: ["S7200", "S7200Smart", "S7300", "S7400", "S71200", "S71500"],
                ToolTip: "PLC 的 CPU 型号，影响连接参数协商", Width: 90),
            new("Rack", "机架号", ConnectionFieldKind.Int, DefaultValue: "0",
                ToolTip: "CPU 所在机架号：S7-1200/1500 常见 0，S7-300 常见 0", Width: 60),
            new("Slot", "插槽号", ConnectionFieldKind.Int, DefaultValue: "1",
                ToolTip: "CPU 所在插槽号：S7-1200/1500 常见 1，S7-300 常见 2，S7-400 常见 3", Width: 60),
        ],
        _ => throw new NotSupportedException($"不支持的驱动类型：{driverType}"),
    };

    /// <summary>创建驱动实例。驱动未实现时抛出明确提示。</summary>
    public static IDeviceDriver Create(DeviceConfig config) => config.DriverType switch
    {
        "ModbusTcp" => new ModbusTcpDriver(config),
        "ModbusRtu" => new ModbusRtuDriver(config),
        "SerialFree" => new SerialFreeDriver(config),
        "Socket" => new SocketDriver(config),
        "OpcUa" => new OpcUaDriver(config),
        "Mqtt" => new MqttDriver(config),
        "S7" => new S7Driver(config),
        _ => throw new NotSupportedException($"不支持的驱动类型：{config.DriverType}"),
    };
}
