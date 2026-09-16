namespace IndustrialProtocolAssistant.Core;

/// <summary>
/// 一个数据点的带时间戳采样值。
/// 统一模型：无论底层是 Modbus/OPC UA/S7，UI 只认 TagValue。
/// </summary>
public sealed record TagValue(
    string TagId,
    object? Value,
    DataQuality Quality,
    DateTimeOffset Timestamp)
{
    public static TagValue Bad(string tagId) =>
        new(tagId, null, DataQuality.Bad, DateTimeOffset.UtcNow);

    public static TagValue Stale(string tagId) =>
        new(tagId, null, DataQuality.Stale, DateTimeOffset.UtcNow);
}
