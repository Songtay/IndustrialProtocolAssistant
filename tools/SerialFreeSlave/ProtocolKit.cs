namespace SerialFreeSlave;

/// <summary>
/// 帧工具集：与 src/Drivers/Driver/SerialFreeProtocol.cs 保持一致的算法（CRC16 / XOR / SUM / 大端编码）。
/// 模拟从站必须按主站能理解的方式构造/校验帧，这里复制算法而不是引用 Drivers 的 internal 类型，
/// 使工具保持独立、便于单独发布。
/// </summary>
internal static class ProtocolKit
{
    // ---------- hex 文本 ↔ 字节 ----------

    /// <summary>解析 hex 文本为字节数组：支持空格/逗号分隔，也支持连续书写（如 010300000001）。</summary>
    public static byte[] ParseHex(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("hex 文本为空");

        var parts = text.Replace(",", " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>(parts.Length);
        foreach (var p in parts)
        {
            if (p.Length % 2 != 0 || !p.All(Uri.IsHexDigit))
                throw new FormatException($"不是合法的 hex 字节：{p}");
            for (int i = 0; i < p.Length; i += 2)
                bytes.Add(Convert.ToByte(p.Substring(i, 2), 16));
        }
        return bytes.ToArray();
    }

    public static string ToHex(IEnumerable<byte> bytes) =>
        string.Join(' ', bytes.Select(b => b.ToString("X2")));

    /// <summary>把文本按空格/逗号拆成渲染 token（每项为 2 位 hex、连续 hex 串或 {占位符}）。</summary>
    public static string[] SplitTokens(string text) =>
        text.Replace(",", " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // ---------- 校验算法（与主站 SerialFreeProtocol 完全一致） ----------

    /// <summary>CRC-16/MODBUS，返回 16 位校验值（发送顺序为低字节在前）。</summary>
    public static ushort Crc16(IReadOnlyList<byte> data)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < data.Count; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    public static byte Xor(IReadOnlyList<byte> data)
    {
        byte v = 0;
        for (int i = 0; i < data.Count; i++) v ^= data[i];
        return v;
    }

    public static byte Sum(IReadOnlyList<byte> data)
    {
        uint v = 0;
        for (int i = 0; i < data.Count; i++) v += data[i];
        return (byte)(v & 0xFF);
    }

    // ---------- 帧渲染（占位符展开，算法同 SerialFreeProtocol.RenderFrame） ----------

    /// <summary>
    /// 渲染一帧：数据 token 依次拼出基础帧；帧尾的校验占位符 {crc16}/{xor}/{sum} 各自基于
    /// "除自身外"的基础帧计算（互不包含，与主站一致）。{value} 用 valueBytes 替换；
    /// resolveVar 回调可把自定义 {name:Type} token 解析为字节（自定义规则引擎使用）。
    /// </summary>
    public static byte[] Render(IReadOnlyList<string> tokens, byte[]? valueBytes = null, Func<string, byte[]>? resolveVar = null)
    {
        var baseBytes = new List<byte>(tokens.Count * 2);
        var checksums = new List<int>(); // 0=crc16 1=xor 2=sum
        foreach (var t in tokens)
        {
            switch (t)
            {
                case "{crc16}": checksums.Add(0); break;
                case "{xor}": checksums.Add(1); break;
                case "{sum}": checksums.Add(2); break;
                case "{value}":
                    if (valueBytes is null)
                        throw new FormatException("{value} 需要提供值字节");
                    baseBytes.AddRange(valueBytes);
                    break;
                default:
                    if (t.StartsWith('{') && t.EndsWith('}') && resolveVar is not null)
                        baseBytes.AddRange(resolveVar(t));
                    else
                        baseBytes.AddRange(ParseHex(t));
                    break;
            }
        }

        var frame = baseBytes.ToList();
        foreach (var op in checksums)
        {
            switch (op)
            {
                case 0:
                    var crc = Crc16(baseBytes);
                    frame.Add((byte)crc);        // 低字节在前，与主站一致
                    frame.Add((byte)(crc >> 8));
                    break;
                case 1: frame.Add(Xor(baseBytes)); break;
                case 2: frame.Add(Sum(baseBytes)); break;
            }
        }
        return frame.ToArray();
    }

    /// <summary>把文本直接渲染成帧（先拆 token 再渲染），供请求匹配/校验使用。</summary>
    public static byte[] RenderText(string text) => Render(SplitTokens(text));

    // ---------- 类型编码（大端，与 SerialFreeProtocol.EncodeValue 一致） ----------

    /// <summary>按协议类型名把值编码为大端字节。type 取值：Bool/Int16/UInt16/Int32/UInt32/Float/Double。</summary>
    public static byte[] EncodeValue(string type, object value)
    {
        switch (type)
        {
            case "Bool": return ToBool(value) ? new byte[] { 0x01 } : new byte[] { 0x00 };
            case "Int16": return BigEndian(BitConverter.GetBytes(Convert.ToInt16(value)));
            case "UInt16": return BigEndian(BitConverter.GetBytes(Convert.ToUInt16(value)));
            case "Int32": return BigEndian(BitConverter.GetBytes(Convert.ToInt32(value)));
            case "UInt32": return BigEndian(BitConverter.GetBytes(Convert.ToUInt32(value)));
            case "Float": return BigEndian(BitConverter.GetBytes(Convert.ToSingle(value)));
            case "Double": return BigEndian(BitConverter.GetBytes(Convert.ToDouble(value)));
            default: throw new FormatException($"不支持的协议类型：{type}（可选 Bool/Int16/UInt16/Int32/UInt32/Float/Double）");
        }
    }

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        string s => s.Equals("1", StringComparison.OrdinalIgnoreCase)
                 || s.Equals("true", StringComparison.OrdinalIgnoreCase)
                 || s.Equals("on", StringComparison.OrdinalIgnoreCase),
        _ => Convert.ToBoolean(value),
    };

    private static byte[] BigEndian(byte[] bytes)
    {
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }
}
