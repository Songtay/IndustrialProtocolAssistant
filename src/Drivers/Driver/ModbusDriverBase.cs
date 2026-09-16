using System.Text;
using IndustrialProtocolAssistant.Core;

namespace IndustrialProtocolAssistant.Drivers.Driver;

/// <summary>
/// Modbus 驱动公共基类（模板方法模式）：
/// 传输层（TCP / RTU / 后续可加 ASCII）由子类在 CreateMasterAsync 中建立，
/// 读/写解析逻辑全部收口在此，保持协议语义一致。
/// </summary>
public abstract class ModbusDriverBase : IDeviceDriver, ITrafficAwareDriver
{
    protected readonly DeviceConfig _config;
    protected NModbus.IModbusMaster? _master;
    protected IDisposable? _transport;
    protected volatile bool _connected;

    public Action<string>? LogAction { get; set; }
    public Action<TrafficFrame>? TrafficSink { get; set; }

    /// <summary>从站号（协议参数 SlaveId），缺省 1。</summary>
    protected byte SlaveId => _config.GetByte("SlaveId", 1);

    /// <summary>把 Tag 地址解析为 Modbus 寄存器号；非数字地址（OPC UA NodeId / MQTT Topic）明确报错。</summary>
    private static ushort ResolveAddress(TagDefinition tag) =>
        tag.TryGetModbusAddress(out var addr)
            ? addr
            : throw new InvalidOperationException(
                $"Tag「{tag.Name}」的地址「{tag.Address}」不是有效的 Modbus 寄存器号（0~65535）");

    /// <summary>子类声明自己的驱动类型（须与 DeviceConfig.DriverType 一致）。</summary>
    public abstract string DriverType { get; }

    /// <summary>真实连接状态：由 ConnectAsync 成功置位，读/写失败时复位。</summary>
    public bool IsConnected => _connected;

    protected ModbusDriverBase(DeviceConfig config)
    {
        if (config.DriverType != DriverType)
            throw new ArgumentException($"驱动类型不匹配：期望 {DriverType}，实际 {config.DriverType}");
        _config = config;
    }

    /// <summary>子类：建立底层传输（TcpClient / SerialPort）并返回 NModbus master。</summary>
    protected abstract Task<NModbus.IModbusMaster> CreateMasterAsync(CancellationToken ct);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var master = await CreateMasterAsync(ct).ConfigureAwait(false);
            master.Transport.ReadTimeout = 2000;
            master.Transport.WriteTimeout = 2000;
            _master = master;
            _connected = true;
        }
        catch
        {
            _connected = false;
            throw;
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;
        try { _master?.Dispose(); } catch { }
        try { _transport?.Dispose(); } catch { }
        _master = null;
        _transport = null;
        return Task.CompletedTask;
    }

    /// <summary>标记连接断开并释放底层资源，供读/写失败时调用，触发轮询层重连。</summary>
    protected void MarkDisconnected()
    {
        _connected = false;
        try { _master?.Dispose(); } catch { }
        try { _transport?.Dispose(); } catch { }
        _master = null;
        _transport = null;
    }

    /// <summary>寄存器序列 → 线上字节序（每寄存器大端 hi-lo 顺序串接），与 Modbus 响应数据区一致。</summary>
    private static byte[] RegsToBytes(ushort[] regs)
    {
        var bytes = new byte[regs.Length * 2];
        for (int i = 0; i < regs.Length; i++)
        {
            bytes[i * 2] = (byte)(regs[i] >> 8);
            bytes[i * 2 + 1] = (byte)regs[i];
        }
        return bytes;
    }

    /// <summary>上报一条载荷报文（TX=写入数据，RX=读出数据）。Text：载荷为可打印文本时附上。protected 供子类手动原始收发复用。</summary>
    protected void EmitTraffic(TrafficDirection dir, string description, byte[] payload)
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

    private static string ToReadableText(byte[] data)
    {
        if (data.Length == 0) return "";
        var text = Encoding.ASCII.GetString(data);
        return text.Any(c => c < 0x20 && c != '\t' && c != '\n' && c != '\r') ? "" : text;
    }

    /// <summary>
    /// RTU 请求自动补全（手动原始收发）：若请求已是“末尾带正确 CRC16(MODBUS)”的完整帧则原样返回，
    /// 否则在帧尾补 CRC（低字节在前），避免把已带 CRC 的粘贴帧补成两段。
    /// </summary>
    protected static byte[] EnsureRtuCrc(byte[] request)
    {
        if (request.Length >= 4)
        {
            byte[] tail = SerialFreeProtocol.Crc16(request.AsSpan(0, request.Length - 2).ToArray());
            if (tail[0] == request[^2] && tail[1] == request[^1]) return request;
        }

        byte[] crc = SerialFreeProtocol.Crc16(request);
        var frame = new byte[request.Length + 2];
        Buffer.BlockCopy(request, 0, frame, 0, request.Length);
        frame[^2] = crc[0];
        frame[^1] = crc[1];
        return frame;
    }

    public async Task<TagValue> ReadTagAsync(TagDefinition tag, CancellationToken ct = default)
    {
        if (_master is null) return TagValue.Bad(tag.Id);

        try
        {
            return tag.DataType switch
            {
                TagDataType.Bool => await ReadBool(tag).ConfigureAwait(false),
                TagDataType.Int16 => await ReadInt16(tag).ConfigureAwait(false),
                TagDataType.UInt16 => await ReadUInt16(tag).ConfigureAwait(false),
                TagDataType.Int32 => await ReadInt32(tag).ConfigureAwait(false),
                TagDataType.UInt32 => await ReadUInt32(tag).ConfigureAwait(false),
                TagDataType.Float => await ReadFloat(tag).ConfigureAwait(false),
                TagDataType.Double => await ReadDouble(tag).ConfigureAwait(false),
                TagDataType.String => await ReadString(tag).ConfigureAwait(false),
                _ => TagValue.Bad(tag.Id),
            };
        }
        catch
        {
            // 读取失败（超时/对端断开/协议异常）→ 标记断开，让轮询层感知并重连
            MarkDisconnected();
            return TagValue.Bad(tag.Id);
        }
    }

    public async Task<bool> WriteTagAsync(TagDefinition tag, object value, CancellationToken ct = default)
    {
        if (_master is null) return false;
        try
        {
            var slave = SlaveId;
            var addr = ResolveAddress(tag);
            switch (tag.DataType)
            {
                case TagDataType.Bool:
                    var coil = ToBool(value);
                    await _master.WriteSingleCoilAsync(slave, addr, coil).ConfigureAwait(false);
                    // 载荷捕获：写单线圈（FC05），线圈数据 0xFF00=ON / 0x0000=OFF
                    EmitTraffic(TrafficDirection.Tx, $"写线圈 Addr={addr}（FC05）", coil ? new byte[] { 0xFF, 0x00 } : new byte[] { 0x00, 0x00 });
                    return true;
                case TagDataType.UInt16:
                    var u16 = Convert.ToUInt16(value);
                    await _master.WriteSingleRegisterAsync(slave, addr, u16).ConfigureAwait(false);
                    EmitTraffic(TrafficDirection.Tx, $"写寄存器 Addr={addr}（FC06）", RegsToBytes(new[] { u16 }));
                    return true;
                case TagDataType.Int16:
                    var i16 = unchecked((ushort)Convert.ToInt16(value));
                    await _master.WriteSingleRegisterAsync(slave, addr, i16).ConfigureAwait(false);
                    EmitTraffic(TrafficDirection.Tx, $"写寄存器 Addr={addr}（FC06）", RegsToBytes(new[] { i16 }));
                    return true;
                case TagDataType.UInt32:
                    await WriteUInt32(slave, addr, Convert.ToUInt32(value)).ConfigureAwait(false);
                    return true;
                case TagDataType.Int32:
                    await WriteInt32(slave, addr, Convert.ToInt32(value)).ConfigureAwait(false);
                    return true;
                case TagDataType.Float:
                    await WriteFloat(slave, addr, Convert.ToSingle(value)).ConfigureAwait(false);
                    return true;
                case TagDataType.Double:
                    await WriteDouble(slave, addr, Convert.ToDouble(value)).ConfigureAwait(false);
                    return true;
                case TagDataType.String:
                    var text = value?.ToString() ?? "";
                    // 超长时直接拒绝，不触发通信，避免误判断线
                    if (Encoding.ASCII.GetByteCount(text) > tag.Length * 2) return false;
                    await WriteString(slave, addr, text, tag.Length).ConfigureAwait(false);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is not FormatException and not InvalidCastException and not OverflowException)
        {
            // 通信类异常（超时/断开）→ 标记断开并向上抛出，由调用方处理
            MarkDisconnected();
            throw;
        }
    }

    private async Task<TagValue> ReadBool(TagDefinition tag)
    {
        var addr = ResolveAddress(tag);
        var coils = await _master!.ReadCoilsAsync(SlaveId, addr, 1).ConfigureAwait(false);
        // 载荷捕获：读线圈响应（FC01），数据字节 0x00/0x01
        EmitTraffic(TrafficDirection.Rx, $"读线圈 Addr={addr}（FC01）", new[] { coils[0] ? (byte)0x01 : (byte)0x00 });
        return new TagValue(tag.Id, coils[0], DataQuality.Good, DateTimeOffset.UtcNow);
    }
    private async Task<TagValue> ReadUInt16(TagDefinition tag)
    {
        var addr = ResolveAddress(tag);
        var regs = await _master!.ReadHoldingRegistersAsync(SlaveId, addr, 1).ConfigureAwait(false);
        // 载荷捕获：读保持寄存器响应（FC03），寄存器值大端字节序（线上数据区）
        EmitTraffic(TrafficDirection.Rx, $"读保持寄存器 Addr={addr}×1（FC03）", RegsToBytes(regs));
        return new TagValue(tag.Id, regs[0], DataQuality.Good, DateTimeOffset.UtcNow);
    }
    private async Task<TagValue> ReadInt16(TagDefinition tag)
    {
        var v = await ReadUInt16(tag).ConfigureAwait(false);
        return v with { Value = unchecked((short)(ushort)(v.Value ?? 0)) };
    }
    private async Task<TagValue> ReadUInt32(TagDefinition tag)
    {
        var addr = ResolveAddress(tag);
        var regs = await _master!.ReadHoldingRegistersAsync(SlaveId, addr, 2).ConfigureAwait(false);
        // 载荷捕获：2 个保持寄存器 = 4 字节大端（线上响应数据区）
        EmitTraffic(TrafficDirection.Rx, $"读保持寄存器 Addr={addr}×2（FC03）", RegsToBytes(regs));
        uint v = (uint)regs[0] << 16 | regs[1];
        return new TagValue(tag.Id, v, DataQuality.Good, DateTimeOffset.UtcNow);
    }
    private async Task<TagValue> ReadInt32(TagDefinition tag)
    {
        var v = await ReadUInt32(tag).ConfigureAwait(false);
        return v with { Value = unchecked((int)(uint)v.Value!) };
    }
    private async Task<TagValue> ReadFloat(TagDefinition tag)
    {
        var addr = ResolveAddress(tag);
        var regs = await _master!.ReadHoldingRegistersAsync(SlaveId, addr, 2).ConfigureAwait(false);
        byte[] bytes = new byte[4];
        // Modbus: 高字在前
        BitConverter.GetBytes(regs[0]).CopyTo(bytes, 2);
        BitConverter.GetBytes(regs[1]).CopyTo(bytes, 0);
        float f = BitConverter.ToSingle(bytes, 0);
        EmitTraffic(TrafficDirection.Rx, $"读保持寄存器 Addr={addr}×2（FC03）", RegsToBytes(regs));
        return new TagValue(tag.Id, f, DataQuality.Good, DateTimeOffset.UtcNow);
    }

    private async Task<TagValue> ReadDouble(TagDefinition tag)
    {
        var addr = ResolveAddress(tag);
        var regs = await _master!.ReadHoldingRegistersAsync(SlaveId, addr, 4).ConfigureAwait(false);
        byte[] bytes = new byte[8];
        BitConverter.GetBytes(regs[0]).CopyTo(bytes, 6);
        BitConverter.GetBytes(regs[1]).CopyTo(bytes, 4);
        BitConverter.GetBytes(regs[2]).CopyTo(bytes, 2);
        BitConverter.GetBytes(regs[3]).CopyTo(bytes, 0);
        EmitTraffic(TrafficDirection.Rx, $"读保持寄存器 Addr={addr}×4（FC03）", RegsToBytes(regs));
        return new TagValue(tag.Id, BitConverter.ToDouble(bytes, 0), DataQuality.Good, DateTimeOffset.UtcNow);
    }

    /// <summary>ASCII 字符串读取：每个寄存器存 2 个字符（高字节在前），从首个 '\0' 截断。</summary>
    private async Task<TagValue> ReadString(TagDefinition tag)
    {
        var addr = ResolveAddress(tag);
        var regs = await _master!.ReadHoldingRegistersAsync(SlaveId, addr, tag.Length).ConfigureAwait(false);
        byte[] bytes = new byte[regs.Length * 2];
        for (int i = 0; i < regs.Length; i++)
        {
            bytes[i * 2] = (byte)(regs[i] >> 8);
            bytes[i * 2 + 1] = (byte)(regs[i] & 0xFF);
        }
        int len = Array.IndexOf(bytes, (byte)0);
        if (len < 0) len = bytes.Length;
        EmitTraffic(TrafficDirection.Rx, $"读保持寄存器 Addr={addr}×{tag.Length}（FC03）", bytes);
        return new TagValue(tag.Id, Encoding.ASCII.GetString(bytes, 0, len), DataQuality.Good, DateTimeOffset.UtcNow);
    }

    /// <summary>把 UI 输入值安全转为 bool（兼容 "1"/"0"/"true"/"false"，其余返回 false 不抛异常）。</summary>
    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        string s when s.Trim() is "1" or "true" or "True" or "yes" or "Yes" or "ON" or "on" => true,
        string s when s.Trim() is "0" or "false" or "False" or "no" or "No" or "OFF" or "off" => false,
        _ => false,
    };

    /// <summary>32 位值按 Modbus 惯例写入：高字在前，占 2 个连续保持寄存器。</summary>
    private async Task WriteUInt32(byte slave, ushort address, uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        var regs = new[] { BitConverter.ToUInt16(bytes, 2), BitConverter.ToUInt16(bytes, 0) };
        await _master!.WriteMultipleRegistersAsync(slave, address, regs).ConfigureAwait(false);
        EmitTraffic(TrafficDirection.Tx, $"写寄存器 Addr={address}×2（FC16）", RegsToBytes(regs));
    }

    private async Task WriteInt32(byte slave, ushort address, int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        var regs = new[] { BitConverter.ToUInt16(bytes, 2), BitConverter.ToUInt16(bytes, 0) };
        await _master!.WriteMultipleRegistersAsync(slave, address, regs).ConfigureAwait(false);
        EmitTraffic(TrafficDirection.Tx, $"写寄存器 Addr={address}×2（FC16）", RegsToBytes(regs));
    }

    private async Task WriteFloat(byte slave, ushort address, float value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        var regs = new[] { BitConverter.ToUInt16(bytes, 2), BitConverter.ToUInt16(bytes, 0) };
        await _master!.WriteMultipleRegistersAsync(slave, address, regs).ConfigureAwait(false);
        EmitTraffic(TrafficDirection.Tx, $"写寄存器 Addr={address}×2（FC16）", RegsToBytes(regs));
    }

    /// <summary>Double 占 4 个连续保持寄存器，高字在前。</summary>
    private async Task WriteDouble(byte slave, ushort address, double value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        var regs = new[]
        {
            BitConverter.ToUInt16(bytes, 6),
            BitConverter.ToUInt16(bytes, 4),
            BitConverter.ToUInt16(bytes, 2),
            BitConverter.ToUInt16(bytes, 0),
        };
        await _master!.WriteMultipleRegistersAsync(slave, address, regs).ConfigureAwait(false);
        EmitTraffic(TrafficDirection.Tx, $"写寄存器 Addr={address}×4（FC16）", RegsToBytes(regs));
    }

    /// <summary>ASCII 字符串写入：每个寄存器 2 个字符（高字节在前），不足部分补 '\0'。</summary>
    private async Task WriteString(byte slave, ushort address, string value, ushort regCount)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        var regs = new ushort[regCount];
        for (int i = 0; i < regCount; i++)
        {
            byte hi = i * 2 < bytes.Length ? bytes[i * 2] : (byte)0;
            byte lo = i * 2 + 1 < bytes.Length ? bytes[i * 2 + 1] : (byte)0;
            regs[i] = (ushort)(hi << 8 | lo);
        }
        await _master!.WriteMultipleRegistersAsync(slave, address, regs).ConfigureAwait(false);
        EmitTraffic(TrafficDirection.Tx, $"写寄存器 Addr={address}×{regCount}（FC16）", RegsToBytes(regs));
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
