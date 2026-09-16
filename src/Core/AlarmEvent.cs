namespace IndustrialProtocolAssistant.Core;

/// <summary>
/// 报警事件。
/// </summary>
public sealed record AlarmEvent(
    string TagId,
    string TagName,
    double Value,
    string Message,
    DateTimeOffset Timestamp);
