using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace IndustrialProtocolAssistant.UI;

/// <summary>配置导出/导入中的单个 Tag（JSON 序列化友好 DTO，DataType 用枚举名文本）。</summary>
public sealed class TagProfileDto
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public string Address { get; set; } = "";
    public int Length { get; set; } = 1;
    public double? HighAlarm { get; set; }
    public double? LowAlarm { get; set; }
    public string? WriteAddress { get; set; }
}

/// <summary>配置导出/导入的整体结构：当前驱动 + 连接参数 + 轮询间隔 + 该协议下全部 Tag。</summary>
public sealed class DeviceProfileDto
{
    public string DriverType { get; set; } = "";
    public int PollIntervalMs { get; set; } = 1000;
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.Ordinal);
    public List<TagProfileDto> Tags { get; set; } = new();
}

/// <summary>
/// 设备配置文件的 JSON 读写（用于跨机器/跨场景备份与恢复）。
/// 编码宽松转义，中文名称直接以 UTF-8 明文写入便于人工核对。
/// </summary>
public static class DeviceProfileFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>写入配置文件；失败抛出异常（由调用方提示）。</summary>
    public static void Save(string path, DeviceProfileDto profile) =>
        File.WriteAllText(path, JsonSerializer.Serialize(profile, Options));

    /// <summary>读取配置文件。JSON 格式非法返回 null 并给出错误信息。</summary>
    public static DeviceProfileDto? Load(string path, out string error)
    {
        error = "";
        try
        {
            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<DeviceProfileDto>(json, Options);
            if (profile is null)
            {
                error = "文件内容为空或不是合法的配置 JSON";
                return null;
            }
            return profile;
        }
        catch (JsonException ex)
        {
            error = $"JSON 解析失败：{ex.Message}";
            return null;
        }
        catch (Exception ex)
        {
            error = $"读取失败：{ex.Message}";
            return null;
        }
    }
}
