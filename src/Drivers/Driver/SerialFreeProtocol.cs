using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IndustrialProtocolAssistant.Core;

namespace IndustrialProtocolAssistant.Drivers.Driver;

/// <summary>
/// 串口自由协议（SerialFree）的纯协议层：地址语法、帧渲染、写值编码、回复解析、校验码。
/// 不依赖任何 IO，便于单元测试。
///
/// 【地址语法】（UI 的"地址"列填一行，完整描述一次"一问一答"）
///   读帧hex[@数据起始字节] [|| 写帧hex]
///   · 读帧hex：轮询读取时发送的请求帧。例：01 03 00 00 00 01 84 0A
///   · @数据起始字节：回复帧中数据起始下标（0 基，默认 0）。例：01 03 00 00 00 01 84 0A@4
///   · || 写帧hex：可选。写入时改用写帧（可含 {value}）；缺省则把编码后的值追加到读帧末尾
///   · 字节写法：支持空格/逗号分隔（AA 55），也支持连续十六进制串（AA55 自动按 2 字符切分）
///
/// 【帧尾校验】
///   两种方式二选一（显式占位符优先，避免重复追加）：
///   · 设备级自动补：在连接参数「帧校验」下拉选 CRC16/XOR/SUM 后，本类 TryParse 会在读帧/写帧末尾
///     自动追加对应校验占位符（基于除自身外整帧计算），Tag 地址无需再写；选「无」则完全按地址原样渲染。
///   · 地址内显式占位符：{crc16}（CRC-16/MODBUS，低字节在前，占 2 字节）/ {xor}（单字节异或）/
///     {sum}（单字节累加和低 8 位）；适用于单帧内多处校验或非帧尾校验等特殊布局。
///
/// 【占位符】
///   {value}   写入时按 Tag 类型编码（大端）替换；含 {value} 的帧段只能写、不参与轮询读取
///   {crc16}   CRC-16/MODBUS 校验码（低字节在前，占 2 字节）
///   {xor}     整帧（除自身外）单字节异或校验
///   {sum}     整帧（除自身外）单字节累加和（低 8 位）
///
/// 【回复解析】
///   从"数据起始字节"按 Tag 类型取值：Bool=1 字节（非 0 为真）、Int16/UInt16=2 字节、
///   Int32/UInt32/Float=4 字节、Double=8 字节、String=从起始处读取最多"长度"个 ASCII 字节（遇 0x00 截断）。
///   16/32/64 位数值统一大端（高字节在前）。
/// </summary>
internal static class SerialFreeProtocol
{
    /// <summary>解析后的帧规格：读段 token 列表、回复数据偏移、写段 token 列表。</summary>
    internal sealed record FrameSpec(
        IReadOnlyList<string> ReadTokens,
        int Offset,
        IReadOnlyList<string> WriteTokens)
    {
        /// <summary>读段为空 → 该 Tag 只写不读。</summary>
        public bool IsWriteOnly => ReadTokens.Count == 0;

        /// <summary>地址里是否显式配置了 || 写段。</summary>
        public bool HasWriteSection => WriteTokens.Count > 0;

        /// <summary>读段是否含 {value}（含则只能用于写入，轮询时应跳过）。</summary>
        public bool ReadContainsValuePlaceholder => ReadTokens.Contains("{value}");

        /// <summary>
        /// 设备级「帧校验」自动补尾：在读帧/写帧末尾各追加一个校验占位符。
        /// 仅当对应帧段非空且未含任何显式校验占位符时追加（显式占位符优先，避免校验重复）。
        /// </summary>
        public FrameSpec WithTailChecksum(string checksumToken)
        {
            bool readChanged = ReadTokens.Count > 0 && !ReadTokens.Any(IsChecksumPlaceholder);
            bool writeChanged = WriteTokens.Count > 0 && !WriteTokens.Any(IsChecksumPlaceholder);
            if (!readChanged && !writeChanged) return this;

            IReadOnlyList<string> read = readChanged
                ? ReadTokens.Append(checksumToken).ToArray()
                : ReadTokens;
            IReadOnlyList<string> write = writeChanged
                ? WriteTokens.Append(checksumToken).ToArray()
                : WriteTokens;
            return new FrameSpec(read, Offset, write);
        }
    }

    private static readonly string[] Placeholders = ["{value}", "{crc16}", "{xor}", "{sum}"];

    /// <summary>该 token 是否为帧校验占位符（显式写在地址里的校验由用户自行布局）。</summary>
    private static bool IsChecksumPlaceholder(string token) =>
        token is "{crc16}" or "{xor}" or "{sum}";

    // ==================== 地址解析 ====================

    /// <summary>解析地址语法；失败时返回便于 UI 提示的中文原因。</summary>
    internal static bool TryParse(string address, out FrameSpec? spec, out string? error)
    {
        spec = null;
        error = null;
        if (string.IsNullOrWhiteSpace(address))
        {
            error = "地址不能为空";
            return false;
        }

        // 1) 切分读段 / 写段（最多一个 ||）
        string[] parts = address.Split(["||"], StringSplitOptions.None);
        if (parts.Length > 2)
        {
            error = "地址中只能出现一次 ||（格式：读帧[@偏移] || 写帧）";
            return false;
        }

        // 2) 从读段末尾提取 @偏移
        string readHex = parts[0];
        int offset = 0;
        Match m = Regex.Match(readHex.Trim(), @"^(.*?)\s*@\s*(\d+)\s*$");
        if (m.Success)
        {
            readHex = m.Groups[1].Value;
            offset = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if (readHex.Contains('@'))
        {
            error = "偏移应写在读帧末尾：如 01 03 00 00 00 01 84 0A@4";
            return false;
        }

        // 3) 分别解析读段与写段的字节 token
        if (!TryTokenize(readHex, out var readTokens, out error)) return false;
        if (!TryTokenize(parts.Length == 2 ? parts[1] : null, out var writeTokens, out error)) return false;

        if (readTokens.Count == 0 && writeTokens.Count == 0)
        {
            error = "地址中至少要包含一个字节或占位符";
            return false;
        }

        spec = new FrameSpec(readTokens, offset, writeTokens);
        return true;
    }

    /// <summary>
    /// 解析地址并应用设备级「帧校验」自动补尾（连接参数 FrameChecksum：无/CRC16/XOR/SUM）。
    /// 兼容旧配置：参数缺失或为「无」时与原始 TryParse 完全一致；地址内已显式写校验占位符时不重复追加。
    /// </summary>
    internal static bool TryParse(string address, string frameChecksum, out FrameSpec? spec, out string? error)
    {
        if (!TryParse(address, out spec, out error)) return false;
        string? checksumToken = FrameChecksumToken(frameChecksum);
        if (checksumToken is not null)
            spec = spec!.WithTailChecksum(checksumToken);
        return true;
    }

    /// <summary>把连接参数中的校验方式映射为帧尾校验占位符；「无」/未知值返回 null（不自动追加）。</summary>
    private static string? FrameChecksumToken(string mode) => mode switch
    {
        "CRC16" => "{crc16}",
        "XOR" => "{xor}",
        "SUM" => "{sum}",
        _ => null,
    };

    private static bool TryTokenize(string? text, out List<string> tokens, out string? error)
    {
        tokens = new List<string>();
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        string[] raw = text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        foreach (string item in raw)
        {
            if (item.StartsWith('{') && item.EndsWith('}'))
            {
                if (item is "{value}" or "{crc16}" or "{xor}" or "{sum}")
                {
                    tokens.Add(item);
                    continue;
                }
                error = $"不支持占位符「{item}」，可用 {string.Join(" / ", Placeholders)}";
                return false;
            }

            if (item.Length % 2 == 0 && item.All(Uri.IsHexDigit))
            {
                for (int i = 0; i < item.Length; i += 2)
                    tokens.Add(item.Substring(i, 2));
                continue;
            }

            error = $"「{item}」不是合法的十六进制字节（2 位 hex，如 AA）或占位符";
            return false;
        }
        return true;
    }

    // ==================== 帧渲染 ====================

    /// <summary>
    /// 按 token 序列渲染发送帧。valueBytes 为 null（轮询读取）时 {value} 按 0 值编码兜底。
    /// 校验占位符基于"除自身外整帧"计算（crc16 低字节在前）。
    /// </summary>
    internal static byte[] RenderFrame(
        IReadOnlyList<string> tokens, byte[]? valueBytes, TagDataType type, int stringMaxBytes)
    {
        valueBytes ??= ZeroValueBytes(type, stringMaxBytes);

        // 第一遍：渲染全部非校验 token，得到帧主体
        var body = new List<byte>(valueBytes.Length + tokens.Count * 2);
        foreach (var t in tokens)
            if (t is not "{crc16}" and not "{xor}" and not "{sum}")
                body.AddRange(ResolveToken(t, valueBytes));

        // 第二遍：按原顺序输出，遇到校验占位符时插入计算结果
        var output = new List<byte>(body.Count + 4);
        foreach (var t in tokens)
        {
            switch (t)
            {
                case "{crc16}":
                    output.AddRange(Crc16(body));
                    break;
                case "{xor}":
                    output.Add((byte)body.Aggregate(0, (acc, b) => acc ^ b));
                    break;
                case "{sum}":
                    output.Add((byte)(body.Aggregate(0, (acc, b) => acc + b) & 0xFF));
                    break;
                default:
                    output.AddRange(ResolveToken(t, valueBytes));
                    break;
            }
        }
        return output.ToArray();
    }

    private static byte[] ResolveToken(string token, byte[] valueBytes) =>
        token == "{value}" ? valueBytes : [Convert.ToByte(token, 16)];

    /// <summary>CRC-16/MODBUS（多项式 0xA001，初值 0xFFFF），返回低字节在前。</summary>
    internal static byte[] Crc16(IReadOnlyList<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return [(byte)(crc & 0xFF), (byte)(crc >> 8)];
    }

    /// <summary>
    /// 设备级帧尾校验字节（供"手动收发"面板对任意 Hex 载荷补校验）。
    /// 语义与 RenderFrame 末尾校验占位符一致：基于除自身外的整帧计算。
    /// 「无」或未知模式返回空。
    /// </summary>
    internal static byte[] ComputeDeviceTail(IReadOnlyList<byte> body, string frameChecksum) => frameChecksum switch
    {
        "CRC16" => Crc16(body),
        "XOR" => [(byte)body.Aggregate(0, (acc, b) => acc ^ b)],
        "SUM" => [(byte)(body.Aggregate(0, (acc, b) => acc + b) & 0xFF)],
        _ => Array.Empty<byte>(),
    };

    // ==================== 写值编码 ====================

    /// <summary>把 UI 输入值按 Tag 类型编码为字节（数值大端）。格式错误抛异常由调用方提示。</summary>
    internal static byte[] EncodeValue(TagDefinition tag, object value)
    {
        switch (tag.DataType)
        {
            case TagDataType.Bool:
                return [ToBool(value) ? (byte)1 : (byte)0];
            case TagDataType.UInt16:
                return BigEndian(Convert.ToUInt16(value, CultureInfo.InvariantCulture), 2);
            case TagDataType.Int16:
                return BigEndian(unchecked((ushort)Convert.ToInt16(value, CultureInfo.InvariantCulture)), 2);
            case TagDataType.UInt32:
                return BigEndian(Convert.ToUInt32(value, CultureInfo.InvariantCulture), 4);
            case TagDataType.Int32:
                return BigEndian(unchecked((uint)Convert.ToInt32(value, CultureInfo.InvariantCulture)), 4);
            case TagDataType.Float:
                return BigEndian(BitConverter.SingleToUInt32Bits(Convert.ToSingle(value, CultureInfo.InvariantCulture)), 4);
            case TagDataType.Double:
                return BigEndian(BitConverter.DoubleToUInt64Bits(Convert.ToDouble(value, CultureInfo.InvariantCulture)), 8);
            case TagDataType.String:
                return EncodeString(value?.ToString() ?? "", tag.Length);
            default:
                throw new InvalidOperationException($"SerialFree 不支持类型 {tag.DataType}");
        }
    }

    private static byte[] EncodeString(string text, ushort maxBytes)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        // "长度"列对 SerialFree 表示 String 的最大 ASCII 字节数，超出则截断，保证帧布局稳定
        if (maxBytes > 0 && bytes.Length > maxBytes)
            Array.Resize(ref bytes, maxBytes);
        return bytes;
    }

    private static byte[] BigEndian(ulong value, int size)
    {
        var b = new byte[size];
        for (int i = 0; i < size; i++)
            b[size - 1 - i] = (byte)(value >> (8 * i));
        return b;
    }

    private static byte[] ZeroValueBytes(TagDataType type, int stringMaxBytes) => type switch
    {
        TagDataType.Bool => new byte[1],
        TagDataType.Int16 or TagDataType.UInt16 => new byte[2],
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => new byte[4],
        TagDataType.Double => new byte[8],
        TagDataType.String => new byte[Math.Max(0, stringMaxBytes)],
        _ => Array.Empty<byte>(),
    };

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        string s when s.Trim() is "1" or "true" or "True" or "yes" or "Yes" or "ON" or "on" => true,
        string s when s.Trim() is "0" or "false" or "False" or "no" or "No" or "OFF" or "off" => false,
        _ => false,
    };

    // ==================== 回复解析 ====================

    /// <summary>按 Tag 类型从回复帧"数据起始字节"取值（大端）。帧过短/越界返回 Bad。</summary>
    internal static TagValue ParseReply(TagDefinition tag, byte[] reply, int offset)
    {
        if (offset < 0 || offset >= reply.Length) return TagValue.Bad(tag.Id);

        try
        {
            return tag.DataType switch
            {
                TagDataType.Bool => Good(tag, reply[offset] != 0),
                TagDataType.UInt16 => Good(tag, (ushort)(reply[offset] << 8 | reply[offset + 1])),
                TagDataType.Int16 => Good(tag, unchecked((short)(reply[offset] << 8 | reply[offset + 1]))),
                TagDataType.UInt32 => Good(tag, BE32(reply, offset)),
                TagDataType.Int32 => Good(tag, unchecked((int)BE32(reply, offset))),
                TagDataType.Float => Good(tag, BitConverter.UInt32BitsToSingle(BE32(reply, offset))),
                TagDataType.Double => Good(tag, BitConverter.UInt64BitsToDouble(BE64(reply, offset))),
                TagDataType.String => Good(tag, DecodeString(reply, offset, tag.Length)),
                _ => TagValue.Bad(tag.Id),
            };
        }
        catch (IndexOutOfRangeException)
        {
            // 回复帧长度不足，无法取到完整数据
            return TagValue.Bad(tag.Id);
        }
    }

    private static TagValue Good(TagDefinition tag, object value) =>
        new(tag.Id, value, DataQuality.Good, DateTimeOffset.UtcNow);

    private static uint BE32(byte[] b, int o) =>
        ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    private static ulong BE64(byte[] b, int o) =>
        (ulong)BE32(b, o) << 32 | BE32(b, o + 4);

    private static string DecodeString(byte[] reply, int offset, int maxBytes)
    {
        int take = Math.Max(1, maxBytes);
        int end = Math.Min(reply.Length, offset + take);
        int len = end;
        for (int i = offset; i < end; i++)
        {
            if (reply[i] == 0)
            {
                len = i;
                break;
            }
        }
        return Encoding.ASCII.GetString(reply, offset, len - offset);
    }
}
