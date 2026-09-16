using IndustrialProtocolAssistant.Core;
using System.Globalization;
using System.Text;

namespace IndustrialProtocolAssistant.UI;

/// <summary>
/// 「报文监控」Tab 的一行报文（UI 展示模型，由 <see cref="TrafficFrame"/> 转换而来）。
/// 字段全部为纯文本，便于 WPF 直接绑定。
/// </summary>
public sealed class TrafficEntry
{
    public const int MaxPayloadPreviewBytes = 24;   // 表格"载荷预览"最多展示的字节数
    public const int MaxDetailBytes = 512;          // 详情区最多展开的字节数（超长截断提示）

    /// <summary>时间，格式 HH:mm:ss.fff。</summary>
    public string Timestamp { get; init; } = "";

    /// <summary>方向：TX / RX，XAML 用于颜色与文案。</summary>
    public string Direction { get; init; } = "";

    /// <summary>方向中文描述。</summary>
    public string DirectionText => Direction == "TX" ? "发送" : "接收";

    /// <summary>驱动类型（S7 / Mqtt / …）。</summary>
    public string DriverType { get; init; } = "";

    /// <summary>操作描述，如 读 DB1.DBD0（4B）/ PUB sensor/temp。</summary>
    public string Description { get; init; } = "";

    /// <summary>载荷字节数。</summary>
    public int Length { get; init; }

    /// <summary>本帧距上一捕获帧的时间间隔（毫秒，纯数字）；首帧 / 无参考帧为 "-"。用于定位粘包、慢响应。</summary>
    public string DeltaText { get; init; } = "-";

    /// <summary>表格载荷预览（Hex，超长截断加 …+NB）。</summary>
    public string PayloadPreview { get; init; } = "";

    /// <summary>载荷对应的可读文本（UTF-8 解码，非文本时为空）。</summary>
    public string PayloadText { get; init; } = "";

    /// <summary>完整载荷 Hex（空格分隔大写，空载荷为空串）。用于搜索关键字、导出与右键复制。</summary>
    public string FullHex { get; init; } = "";

    /// <summary>完整载荷的 ASCII 视图（不可打印字节映射为 .，与详情区一致）。用于导出与右键复制。</summary>
    public string FullAscii { get; init; } = "";

    /// <summary>选中后详情区展示的完整 Hex + ASCII 双栏 dump。</summary>
    public string Detail { get; init; } = "";

    /// <summary>
    /// 由驱动上报帧生成展示条目。
    /// </summary>
    /// <param name="f">驱动上报的原始帧。</param>
    /// <param name="deltaMs">本帧相对上一帧的时间间隔（毫秒）；首帧传 null。</param>
    public static TrafficEntry FromFrame(TrafficFrame f, double? deltaMs = null)
    {
        var bytes = f.Payload ?? [];
        return new TrafficEntry
        {
            Timestamp = f.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
            Direction = f.Direction == TrafficDirection.Tx ? "TX" : "RX",
            DriverType = f.DriverType,
            Description = f.Description,
            Length = bytes.Length,
            DeltaText = deltaMs.HasValue ? FormatDelta(deltaMs.Value) : "-",
            PayloadPreview = FormatHexPreview(bytes),
            PayloadText = f.Text,
            FullHex = FormatHexFull(bytes),
            FullAscii = FormatAscii(bytes),
            Detail = BuildHexDump(bytes),
        };
    }

    private static string FormatDelta(double ms)
    {
        // ≥100ms 无需小数，其余保留 1 位小数，统一用 . 小数点便于比对与导出
        var text = ms >= 100 ? ms.ToString("0", CultureInfo.InvariantCulture)
                             : ms.ToString("0.#", CultureInfo.InvariantCulture);
        return text;
    }

    /// <summary>完整 Hex（空格分隔）。空载荷返回空串。</summary>
    private static string FormatHexFull(byte[] data)
    {
        if (data.Length == 0) return "";
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>完整 ASCII 视图：可打印字符原样，其余映射为 .（与详情区一致）。</summary>
    private static string FormatAscii(byte[] data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (var b in data)
        {
            var c = (char)b;
            sb.Append(c >= 0x20 && c <= 0x7E ? c : '.');
        }
        return sb.ToString();
    }

    /// <summary>载荷 Hex 预览：前 N 字节，其余省略为 …+剩余字节数。</summary>
    private static string FormatHexPreview(byte[] data)
    {
        if (data.Length == 0) return "(空)";
        var shown = Math.Min(data.Length, MaxPayloadPreviewBytes);
        var sb = new StringBuilder(shown * 3);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        if (data.Length > shown) sb.Append($" … +{data.Length - shown}B");
        return sb.ToString();
    }

    /// <summary>
    /// 详情 Hex+ASCII 双栏视图（经典 hexdump 布局）：
    /// 偏移  十六进制字节(16/行)  |ASCII|
    /// </summary>
    private static string BuildHexDump(byte[] data)
    {
        if (data.Length == 0) return "(空载荷)";
        var total = Math.Min(data.Length, MaxDetailBytes);
        var truncated = data.Length > total;
        var sb = new StringBuilder(total * 5);
        for (int off = 0; off < total; off += 16)
        {
            int count = Math.Min(16, total - off);
            sb.Append(off.ToString("X4")).Append("  ");

            // 十六进制区
            for (int i = 0; i < 16; i++)
            {
                if (i < count)
                    sb.Append(data[off + i].ToString("X2")).Append(' ');
                else
                    sb.Append("   ");
                if (i == 7) sb.Append(' '); // 8 字节一组
            }
            sb.Append(' ');

            // ASCII 区（可打印字符原样，其余 .）
            for (int i = 0; i < count; i++)
            {
                var c = (char)data[off + i];
                sb.Append(c >= 0x20 && c <= 0x7E ? c : '.');
            }
            sb.AppendLine();
        }
        if (truncated) sb.AppendLine($"…（共 {data.Length}B，此处仅显示前 {MaxDetailBytes}B）");
        return sb.ToString();
    }
}
