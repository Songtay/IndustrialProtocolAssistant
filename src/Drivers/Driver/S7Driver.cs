using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using IndustrialProtocolAssistant.Core;
using S7.Net;

namespace IndustrialProtocolAssistant.Drivers.Driver;

/// <summary>
/// S7 协议驱动（基于 S7netplus，支持 S7-200/300/400/1200/1500 以太网连接）。
///
/// Tag.Address 支持以下地址格式（大小写不敏感，允许空格）：
///   数据块：DB1.DBB0（字节）/ DB1.DBW0（字）/ DB1.DBD0（双字）/ DB1.DBX0.0（位）/ DB1.DBS0（S7 STRING）
///   位存储：MB0 / MW0 / MD0 / M0.0
///   输入区：IB0 / IW0 / ID0 / I0.0
///   输出区：QB0 / QW0 / QD0 / Q0.0
///
/// 连接参数（DriverFactory.GetFields 声明）：
///   Host：IP 地址；Port：端口（默认 102）；CpuType：CPU 型号（S7200/S7200Smart/S7300/S7400/S71200/S71500）；
///   Rack：机架号（S7-1200/1500 常见 0）；Slot：插槽号（S7-1200/1500 常见 1，S7-300 常见 2，S7-400 常见 3）。
///
/// 设计说明：
///   - 字节序为大端（S7 标准），数值按 Tag.DataType 解码；TagDataType.Auto 时按地址后缀（B/W/D）推断；
///   - Bool 写入位地址时使用 WriteBit，写入字节地址时写 0x00/0xFF；
///   - String 使用 S7 STRING 布局（前 2 字节：最大长度/当前长度，其后为 UTF-8 字符数据），DBS 地址自动识别；
///   - S7netplus 的连接 API 为同步阻塞，ConnectAsync 经 Task.Run 执行以避免阻塞 UI 线程；
///   - Plc 实例非线程安全，所有读写经 _gate 串行化；
///   - 读写异常视为断线，IsConnected 置 false 后由采集引擎自动重连。
/// </summary>
public sealed class S7Driver : IDeviceDriver, ITrafficAwareDriver
{
    private readonly DeviceConfig _config;
    private readonly Plc _plc;
    private readonly object _gate = new();
    private volatile bool _connected;

    public string DriverType => "S7";
    public bool IsConnected => _connected;
    public Action<string>? LogAction { get; set; }
    public Action<TrafficFrame>? TrafficSink { get; set; }

    public S7Driver(DeviceConfig config)
    {
        if (config.DriverType != DriverType)
            throw new ArgumentException($"驱动类型不匹配：期望 {DriverType}，实际 {config.DriverType}");

        _config = config;
        var host = config.Get("Host", "127.0.0.1");
        var port = config.GetInt("Port", 102);
        var rack = (short)config.GetByte("Rack", 0);
        var slot = (short)config.GetByte("Slot", 1);

        _plc = new Plc(ToCpuType(config.Get("CpuType", "S7300")), host, port, rack, slot);
        _plc.ReadTimeout = Math.Max(100, config.GetInt("ReadTimeout", 3000));
        _plc.WriteTimeout = Math.Max(100, config.GetInt("WriteTimeout", 3000));
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // Open() 为同步阻塞，放入线程池避免阻塞调用方（UI 线程）。
            await Task.Run(() => _plc.Open(), ct);
            _connected = true;
            LogAction?.Invoke(
                $"[S7] 已连接 {_config.Get("Host")}（Port={_config.GetInt("Port", 102)}, " +
                $"CpuType={_config.Get("CpuType", "S7300")}, Rack={_config.GetByte("Rack", 0)}, Slot={_config.GetByte("Slot", 1)}）");
        }
        catch (Exception ex)
        {
            _connected = false;
            LogAction?.Invoke($"[S7] 连接失败: {DescribeError(ex)}");
            throw;
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            _plc.Close();
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"[S7] 断开时异常（忽略）: {ex.Message}");
        }
        finally
        {
            _connected = false;
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public Task<TagValue> ReadTagAsync(TagDefinition tag, CancellationToken ct = default)
    {
        if (!_connected || !_plc.IsConnected)
            return Task.FromResult(TagValue.Bad(tag.Id));

        try
        {
            lock (_gate)
            {
                if (!TryParseAddress(tag.Address, out var addr, out var error))
                {
                    LogAction?.Invoke($"[S7] {tag.Name} 的地址「{tag.Address}」无效：{error}");
                    return Task.FromResult(TagValue.Bad(tag.Id));
                }

                object? value;
                if (addr.Kind == AddressKind.S7String)
                {
                    value = ReadS7String(addr, tag.Address);
                }
                else
                {
                    var bytes = _plc.ReadBytes(addr.Area, addr.DbNumber, addr.ByteOffset, GetByteLength(addr, tag));
                    // 载荷捕获：PLC 返回该地址的原始字节（S7 载荷级监控，非线上 TCP 帧）
                    EmitTraffic(TrafficDirection.Rx, $"读 {tag.Address}（{bytes.Length}B）", bytes);
                    value = DecodeValue(tag.DataType, bytes, addr);
                }

                return Task.FromResult(new TagValue(tag.Id, value, DataQuality.Good, DateTimeOffset.UtcNow));
            }
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"[S7] 读取 {tag.Name}（{tag.Address}）失败: {DescribeError(ex)}");
            MarkDisconnected();
            return Task.FromResult(TagValue.Bad(tag.Id));
        }
    }

    public Task<bool> WriteTagAsync(TagDefinition tag, object value, CancellationToken ct = default)
    {
        if (!_connected || !_plc.IsConnected)
            return Task.FromResult(false);

        try
        {
            lock (_gate)
            {
                if (!TryParseAddress(tag.Address, out var addr, out var error))
                {
                    LogAction?.Invoke($"[S7] {tag.Name} 的地址「{tag.Address}」无效：{error}");
                    return Task.FromResult(false);
                }

                var written = addr.Kind == AddressKind.Bit
                    ? WriteBit(addr, tag.Address, value)
                    : WriteToArea(addr, tag.Address, tag.DataType, value);

                if (!written)
                    LogAction?.Invoke($"[S7] 写入 {tag.Name}（{tag.Address}）被 PLC 拒绝");

                return Task.FromResult(written);
            }
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"[S7] 写入 {tag.Name}（{tag.Address}）失败: {DescribeError(ex)}");
            MarkDisconnected();
            return Task.FromResult(false);
        }
    }

    /// <summary>写入位地址（WriteBit 成功后按约定返回 true）。</summary>
    private bool WriteBit(S7AddressInfo addr, string target, object value)
    {
        var bit = ToBool(value);
        _plc.WriteBit(addr.Area, addr.DbNumber, addr.ByteOffset, addr.BitOffset, bit);
        // 载荷捕获：位写以单字节 0x00/0x01 呈现目标位值
        EmitTraffic(TrafficDirection.Tx, $"写 {target}（位 {addr.BitOffset}={bit}）", new[] { bit ? (byte)0x01 : (byte)0x00 });
        return true;
    }

    /// <summary>按类型写入非位地址。</summary>
    private bool WriteToArea(S7AddressInfo addr, string target, TagDataType type, object value)
    {
        if (addr.Kind == AddressKind.S7String)
        {
            WriteS7String(addr, target, value?.ToString() ?? string.Empty);
            return true;
        }

        var data = EncodeValue(type, value, addr);
        _plc.WriteBytes(addr.Area, addr.DbNumber, addr.ByteOffset, data);
        // 载荷捕获：写入存储区的原始字节
        EmitTraffic(TrafficDirection.Tx, $"写 {target}（{data.Length}B）", data);
        return true;
    }

    /// <summary>读取 S7 STRING（布局：字节0=最大长度，字节1=当前长度，其后为字符数据）。</summary>
    private string ReadS7String(S7AddressInfo addr, string target)
    {
        var header = _plc.ReadBytes(addr.Area, addr.DbNumber, addr.ByteOffset, 2);
        var maxLen = Math.Max(header[0], (byte)1);
        var curLen = Math.Min(header[1], maxLen);
        if (curLen <= 0) return string.Empty;

        var payload = _plc.ReadBytes(addr.Area, addr.DbNumber, addr.ByteOffset + 2, curLen);
        // 载荷捕获：合并 2B 长度头 + 字符数据，还原 S7 STRING 在存储区中的完整字节
        EmitTraffic(TrafficDirection.Rx, $"读 {target}（S7 STRING {curLen}B）", header.Concat(payload).ToArray());
        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>写入 S7 STRING，自动截断到声明最大长度并回写长度头。</summary>
    private void WriteS7String(S7AddressInfo addr, string target, string text)
    {
        var header = _plc.ReadBytes(addr.Area, addr.DbNumber, addr.ByteOffset, 2);
        EmitTraffic(TrafficDirection.Rx, $"读 {target}（S7 STRING 长度头 2B）", header);
        var maxLen = Math.Max(header[0], (byte)1);

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > maxLen) bytes = bytes[..maxLen];

        var buf = new byte[2 + maxLen];
        buf[0] = maxLen;
        buf[1] = (byte)bytes.Length;
        Array.Copy(bytes, 0, buf, 2, bytes.Length);
        _plc.WriteBytes(addr.Area, addr.DbNumber, addr.ByteOffset, buf);
        // 载荷捕获：写入的完整 STRING 字节（长度头 + 字符区）
        EmitTraffic(TrafficDirection.Tx, $"写 {target}（S7 STRING {buf.Length}B）", buf);
    }

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        _ => Convert.ToBoolean(value),
    };

    /// <summary>按 Tag.DataType 读取的字节数；Auto 时按地址后缀（B/W/D）推断。</summary>
    private static int GetByteLength(S7AddressInfo addr, TagDefinition tag)
    {
        var type = tag.DataType;
        if (type != TagDataType.Auto && type != TagDataType.String)
            return type switch
            {
                TagDataType.Bool => 1,
                TagDataType.Int16 or TagDataType.UInt16 => 2,
                TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 4,
                TagDataType.Double => 8,
                _ => 1,
            };

        // String：固定长度字符数组（无长度前缀），长度由 Tag.Length 决定。
        if (type == TagDataType.String)
            return Math.Max(1, (int)tag.Length);

        // Auto：按地址后缀推断。
        return addr.Kind switch
        {
            AddressKind.Bit or AddressKind.Byte => 1,
            AddressKind.Word => 2,
            AddressKind.DWord => 4,
            _ => 4,
        };
    }

    private static object DecodeValue(TagDataType type, byte[] data, S7AddressInfo addr) => type switch
    {
        TagDataType.Bool => addr.BitOffset >= 0
            ? (data[0] & (1 << addr.BitOffset)) != 0
            : data[0] != 0,
        TagDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(data),
        TagDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(data),
        TagDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(data),
        TagDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(data),
        TagDataType.Float => BinaryPrimitives.ReadSingleBigEndian(data),
        TagDataType.Double => BinaryPrimitives.ReadDoubleBigEndian(data),
        TagDataType.String => DecodeString(data),
        _ => DecodeByKind(data, addr), // Auto：按地址后缀推断
    };

    /// <summary>Auto 类型按地址后缀解码（B/W/D 分别对应 byte/ushort/uint）。</summary>
    private static object DecodeByKind(byte[] data, S7AddressInfo addr) => addr.Kind switch
    {
        AddressKind.Bit => (data[0] & (1 << addr.BitOffset)) != 0,
        AddressKind.Word => BinaryPrimitives.ReadUInt16BigEndian(data),
        AddressKind.DWord => BinaryPrimitives.ReadUInt32BigEndian(data),
        _ => data[0],
    };

    private static string DecodeString(byte[] data) =>
        Encoding.UTF8.GetString(data).TrimEnd('\0').TrimEnd();

    /// <summary>把值编码为大端字节序列；Auto 时按地址后缀推断。</summary>
    private static byte[] EncodeValue(TagDataType type, object value, S7AddressInfo addr) => type switch
    {
        TagDataType.Int16 => EncInt16(Convert.ToInt16(value)),
        TagDataType.UInt16 => EncUInt16(Convert.ToUInt16(value)),
        TagDataType.Int32 => EncInt32(Convert.ToInt32(value)),
        TagDataType.UInt32 => EncUInt32(Convert.ToUInt32(value)),
        TagDataType.Float => EncSingle(Convert.ToSingle(value)),
        TagDataType.Double => EncDouble(Convert.ToDouble(value)),
        TagDataType.Bool => new[] { Convert.ToBoolean(value) ? (byte)0xFF : (byte)0x00 },
        TagDataType.String => Encoding.UTF8.GetBytes(value?.ToString() ?? string.Empty),
        _ => addr.Kind switch
        {
            AddressKind.Word => EncUInt16(Convert.ToUInt16(value)),
            AddressKind.DWord => EncUInt32(Convert.ToUInt32(value)),
            _ => new[] { Convert.ToByte(value) }, // Byte
        },
    };

    private static byte[] EncInt16(short v) { var b = new byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, v); return b; }
    private static byte[] EncUInt16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }
    private static byte[] EncInt32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }
    private static byte[] EncUInt32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] EncSingle(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, v); return b; }
    private static byte[] EncDouble(double v) { var b = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); return b; }

    private static CpuType ToCpuType(string value) =>
        Enum.TryParse<CpuType>(value?.Trim(), ignoreCase: true, out var cpu) ? cpu : CpuType.S7300;

    /// <summary>把 S7netplus 的异常转换为可读的描述（含 PLC 错误码）。</summary>
    private static string DescribeError(Exception ex) => ex switch
    {
        PlcException plc => $"错误码 {plc.ErrorCode}：{plc.Message}",
        _ => ex.Message,
    };

    private void MarkDisconnected()
    {
        if (_connected)
        {
            _connected = false;
            LogAction?.Invoke("[S7] 连接已断开（等待自动重连）");
        }
    }

    /// <summary>上报一条载荷报文（TX=写入，RX=读出）。Text 字段：全部为可打印 ASCII/UTF-8 时附上，便于 UI 直接显示文本。</summary>
    private void EmitTraffic(TrafficDirection dir, string description, byte[] payload)
    {
        var sink = TrafficSink;
        if (sink is null) return;
        sink(new TrafficFrame
        {
            Timestamp = DateTimeOffset.UtcNow,
            DeviceId = _config.Id,
            DriverType = DriverType,
            Direction = dir,
            Description = description,
            Payload = payload,
            Text = ToReadableText(payload),
        });
    }

    /// <summary>载荷可读文本：解码成功且不含控制字符才返回，否则空串（由 UI 决定按 Hex 展示）。</summary>
    private static string ToReadableText(byte[] data)
    {
        if (data.Length == 0) return "";
        try
        {
            var text = Encoding.UTF8.GetString(data);
            return text.Any(c => c < 0x20 && c != '\t' && c != '\n' && c != '\r') ? "" : text;
        }
        catch
        {
            return "";
        }
    }

    // ---------- 地址解析 ----------

    /// <summary>供 UI 等外部校验 S7 地址格式（只校验不解析，错误信息可直接展示给用户）。</summary>
    public static bool TryValidateAddress(string? address, out string error)
    {
        error = string.Empty;
        return TryParseAddress(address, out _, out error);
    }

    private enum AddressKind { Bit, Byte, Word, DWord, S7String }

    /// <summary>解析后的 S7 地址：区域、DB 号、字节偏移、位偏移（非位地址为 -1）与地址类别。</summary>
    private sealed record S7AddressInfo(DataType Area, int DbNumber, int ByteOffset, int BitOffset, AddressKind Kind);

    // DB1.DBX0.0 / DB1.DBB0 / DB1.DBW0 / DB1.DBD0 / DB1.DBS0
    private static readonly Regex DbRegex = new(
        @"^DB(\d+)\.DB(X|B|W|D|S)(\d+)(?:\.(\d+))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // M/I/Q 区：M0.0 / MB0 / MW0 / MD0 / I0.0 / IB0 / Q0.0 / QB0 ...
    private static readonly Regex AreaRegex = new(
        @"^([MIQ])(B|W|D)?(\d+)(?:\.(\d+))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryParseAddress(string? address, out S7AddressInfo info, out string error)
    {
        info = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            error = "地址为空";
            return false;
        }

        var s = address.Trim();

        var dbMatch = DbRegex.Match(s);
        if (dbMatch.Success)
        {
            var db = int.Parse(dbMatch.Groups[1].Value);
            if (db < 1)
            {
                error = $"DB 号必须 ≥ 1（当前 {db}）";
                return false;
            }
            return BuildInfo(DataType.DataBlock, db, char.ToUpperInvariant(dbMatch.Groups[2].Value[0]),
                int.Parse(dbMatch.Groups[3].Value), dbMatch.Groups[4].Value, out info, out error);
        }

        var areaMatch = AreaRegex.Match(s);
        if (areaMatch.Success)
        {
            var area = char.ToUpperInvariant(areaMatch.Groups[1].Value[0]) switch
            {
                'I' => DataType.Input,
                'Q' => DataType.Output,
                _ => DataType.Memory,
            };
            var kind = areaMatch.Groups[2].Success ? char.ToUpperInvariant(areaMatch.Groups[2].Value[0]) : 'X';
            return BuildInfo(area, 0, kind, int.Parse(areaMatch.Groups[3].Value), areaMatch.Groups[4].Value, out info, out error);
        }

        error = "不支持的 S7 地址格式，支持：DB1.DBD0 / DB1.DBW0 / DB1.DBB0 / DB1.DBX0.0 / DB1.DBS0、M0.0 / MW0 / MD0、I0.0 / ID0、Q0.0 / QD0 等";
        return false;
    }

    private static bool BuildInfo(DataType area, int db, char kind, int offset, string bitText,
        out S7AddressInfo info, out string error)
    {
        info = null!;
        error = string.Empty;
        if (offset < 0)
        {
            error = $"字节偏移不能为负（当前 {offset}）";
            return false;
        }

        switch (kind)
        {
            case 'X':
            {
                var bit = bitText is { Length: > 0 } ? int.Parse(bitText) : 0;
                if (bit is < 0 or > 7)
                {
                    error = $"位偏移 {bit} 超出范围（0~7）";
                    return false;
                }
                info = new S7AddressInfo(area, db, offset, bit, AddressKind.Bit);
                return true;
            }
            case 'B':
                info = new S7AddressInfo(area, db, offset, -1, AddressKind.Byte);
                return true;
            case 'W':
                info = new S7AddressInfo(area, db, offset, -1, AddressKind.Word);
                return true;
            case 'D':
                info = new S7AddressInfo(area, db, offset, -1, AddressKind.DWord);
                return true;
            case 'S':
                info = new S7AddressInfo(area, db, offset, -1, AddressKind.S7String);
                return true;
            default:
                error = $"未知地址类别 {kind}";
                return false;
        }
    }
}
