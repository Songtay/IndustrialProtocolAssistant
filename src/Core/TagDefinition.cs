using System.Globalization;

namespace IndustrialProtocolAssistant.Core;

/// <summary>
/// 数据点（Tag）定义：描述一个可被采集/下发的变量。
/// Address 是与协议无关的地址字符串，由各驱动按自身语义解析：
/// Modbus = 寄存器号（如 0 / 42）、OPC UA = NodeId（如 ns=2;s=温度）、MQTT = 订阅 Topic（如 sensor/temp）。
/// WriteAddress 为可选的"下发/发送地址"（与 Address 解耦）：MQTT 指独立的发送 Topic（如 devices/01/ctrl），
/// 配置后写入直接发布到它，不再拼接写后缀；其余协议目前不使用，留空即可。
/// </summary>
public sealed record TagDefinition(
    string Id,
    string Name,
    TagDataType DataType,
    string DeviceId,
    string Address,
    ushort Length = 1,
    double? HighAlarm = null,
    double? LowAlarm = null,
    string? WriteAddress = null)
{
    /// <summary>
    /// 创建 Tag。registerLength 仅对 String 类型有意义（寄存器数，1 寄存器 = 2 个 ASCII 字符），
    /// 其余类型由数据长度自动推导；传 null 时 String 使用默认 8 个寄存器。
    /// writeAddress：可选的写入/发送地址（MQTT 为独立发送 Topic），留空由驱动按默认规则下发。
    /// </summary>
    public static TagDefinition Create(
        string name, TagDataType dataType, string deviceId, string address,
        double? highAlarm = null, double? lowAlarm = null, int? registerLength = null,
        string? writeAddress = null) =>
        new(Guid.NewGuid().ToString("N")[..8], name, dataType, deviceId, address,
            (ushort)(registerLength ?? GetRegisterLength(dataType)),
            highAlarm, lowAlarm, string.IsNullOrWhiteSpace(writeAddress) ? null : writeAddress.Trim());

    private static ushort GetRegisterLength(TagDataType dataType) => dataType switch
    {
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 2,
        TagDataType.Double => 4,
        TagDataType.String => 8, // 默认 16 个 ASCII 字符
        _ => 1,
    };

    /// <summary>尝试把地址解析为 Modbus 寄存器号（供 Modbus 驱动使用）；NodeId/Topic 等非数字地址返回 false。</summary>
    public bool TryGetModbusAddress(out ushort value) =>
        ushort.TryParse(Address, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
