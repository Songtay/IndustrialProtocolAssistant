using System.Globalization;

namespace IndustrialProtocolAssistant.Core;

/// <summary>
/// 设备连接配置（与协议无关的通用容器）。
/// 协议相关参数统一放在 Params 字典，键由各驱动在 DriverFactory.GetFields 中声明，
/// 因此新增协议（OPC UA / S7 / MQTT）不需要改动本类型与界面。
/// </summary>
public sealed record DeviceConfig(
    string Id,
    string Name,
    string DriverType,
    int PollIntervalMs,
    Dictionary<string, string> Params)
{
    /// <summary>读取字符串参数，缺失返回空串。</summary>
    public string Get(string key) => Params.TryGetValue(key, out var v) ? v : string.Empty;

    /// <summary>读取字符串参数，缺失或空白时返回默认值。</summary>
    public string Get(string key, string def) =>
        Params.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;

    /// <summary>读取整数参数，解析失败返回默认值。</summary>
    public int GetInt(string key, int def = 0) =>
        Params.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : def;

    /// <summary>读取字节参数（如 Modbus 从站号），解析失败返回默认值。</summary>
    public byte GetByte(string key, byte def = 1) =>
        Params.TryGetValue(key, out var v) && byte.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : def;

    /// <summary>读取布尔参数，解析失败返回默认值。</summary>
    public bool GetBool(string key, bool def = false) =>
        Params.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : def;

    /// <summary>缺失或空白时填充默认值并返回。</summary>
    public string Ensure(string key, string def)
    {
        if (Params.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        Params[key] = def;
        return def;
    }
}
