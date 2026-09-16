namespace IndustrialProtocolAssistant.Core;

/// <summary>
/// 支持的寄存器/数据点数据类型。
/// Auto 仅用于 OPC UA：表示由服务器节点自动识别类型，不强制转换。
/// </summary>
public enum TagDataType
{
    Auto,
    Bool,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Float,
    Double,
    String,
}
