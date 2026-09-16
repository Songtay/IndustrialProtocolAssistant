namespace IndustrialProtocolAssistant.Core;

/// <summary>连接参数的输入控件种类。</summary>
public enum ConnectionFieldKind
{
    Text,        // 单行文本框
    Int,         // 整数文本框
    Password,    // 密码框（界面显示掩码）
    Select,      // 下拉选择（Options 固定候选）
    SerialPort,  // 串口下拉（可编辑 + 系统串口枚举 + 刷新按钮）
}

/// <summary>
/// 驱动连接参数自描述：每个驱动声明自己需要的配置项，
/// UI 据此动态生成表单，新增协议（OPC UA / S7 / MQTT）无需改动界面。
/// </summary>
public sealed record ConnectionField(
    string Key,
    string Label,
    ConnectionFieldKind Kind,
    bool Required = true,
    string? DefaultValue = null,
    string? ToolTip = null,
    string[]? Options = null,
    int Width = 120)
{
    /// <summary>是否为下拉类控件（Select / SerialPort 共用候选列表展示）。</summary>
    public bool IsSelectable => Kind is ConnectionFieldKind.Select or ConnectionFieldKind.SerialPort;
}
