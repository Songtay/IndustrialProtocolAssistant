namespace IndustrialProtocolAssistant.UI;

/// <summary>日志选项卡中的一条日志。</summary>
public sealed class LogEntry
{
    /// <summary>时间，格式 HH:mm:ss.fff。</summary>
    public string Timestamp { get; init; } = "";

    /// <summary>Serilog 级别枚举名（Information / Warning / Error …），用于 XAML 颜色触发。</summary>
    public string Level { get; init; } = "";

    /// <summary>渲染后的日志文本。</summary>
    public string Message { get; init; } = "";

    /// <summary>显示用短级别（INF / WRN / ERR …）。</summary>
    public string LevelShort => Level switch
    {
        "Verbose" => "VRB",
        "Debug" => "DBG",
        "Information" => "INF",
        "Warning" => "WRN",
        "Error" => "ERR",
        "Fatal" => "FTL",
        _ => Level,
    };
}
