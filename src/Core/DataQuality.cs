namespace IndustrialProtocolAssistant.Core;

/// <summary>
/// 数据点质量位（工业通信常见语义：Good/Bad/Uncertain/Stale）。
/// </summary>
public enum DataQuality
{
    Good = 0,
    Bad = 1,
    Uncertain = 2,
    Stale = 3,
}
