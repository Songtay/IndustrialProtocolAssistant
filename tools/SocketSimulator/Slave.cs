using System.Text.Json;

namespace SocketSimulator;

/// <summary>从站引擎统一接口：响应主站请求帧，并周期性刷新内部模拟量。</summary>
internal interface ISlaveEngine
{
    /// <summary>引擎说明（寄存器布局/规则条数），启动时打印。</summary>
    string Describe();

    /// <summary>周期刷新模拟数据（由定时器调用）。</summary>
    void SimulateTick();

    /// <summary>处理主站发来的完整请求帧；返回应答帧，返回 null 表示静默不应答。</summary>
    byte[]? HandleRequest(byte[] frame);
}

// =====================================================================================
// 引擎一：RTU 风格从站（默认，无需配置文件）
//   主站侧 Socket 驱动地址用"类 Modbus RTU"请求帧即可直接读写（与 SerialFree 完全一致）：
//   读保持寄存器  01 03 00 00 00 02 {crc16}@3   → 应答 01 03 04 [数据…] [crc16]
//   读线圈        01 01 00 00 00 01 {crc16}@3   → 应答 01 01 01 [位] [crc16]
//   写寄存器      01 03 ...@3 || 01 06 00 02 {value} {crc16}   → 回显原请求帧
//   写线圈        01 01 ...@3 || 01 05 00 00 FF 00 {crc16}     → 回显原请求帧
//   从站号不符 / CRC 错 → 静默；非法请求 → Modbus 异常响应。
// =====================================================================================
internal sealed class RtuSlave : ISlaveEngine
{
    public const int RegTemp = 0;     // 0..1   Float  温度（随机游走 20~80°C）
    public const int RegCount = 2;    // 2      UInt16 计数（每周期 +1，回绕）
    public const int RegVoltage = 3;  // 3..4   Float  电压（218~238V 慢游走）
    public const int CoilRunning = 0; // 0      Bool   运行状态（每 ~1.8s 翻转）

    private const int RegCapacity = 1024;
    private const int CoilCapacity = 1024;

    private readonly byte _slaveId;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly ushort[] _regs = new ushort[RegCapacity];
    private readonly bool[] _coils = new bool[CoilCapacity];
    private readonly Random _rnd = new();
    private int _tick;
    private ushort _count;

    public RtuSlave(byte slaveId, Action<string> log)
    {
        _slaveId = slaveId;
        _log = log;
        WriteFloat(RegTemp, 25f);
        WriteFloat(RegVoltage, 228f);
    }

    public string Describe() =>
        $"从站号 {_slaveId} · 寄存器表 {RegCapacity} 个（0..{RegCapacity - 1}）· 线圈表 {CoilCapacity} 个\n" +
        "  [0..1] Float 温度(20~80°C 游走)    [2] UInt16 计数(自增)    [3..4] Float 电压(218~238V)\n" +
        "  线圈[0] Bool 运行状态(周期翻转)";

    public void SimulateTick()
    {
        lock (_gate)
        {
            _tick++;

            float t = ReadFloat(RegTemp) + (float)((_rnd.NextDouble() - 0.5) * 1.2);
            t = Math.Clamp(t, 20f, 80f);
            WriteFloat(RegTemp, t);

            _count = (ushort)((_count + 1) % 60001);
            _regs[RegCount] = _count;

            float v = ReadFloat(RegVoltage) + (float)((_rnd.NextDouble() - 0.5) * 0.8);
            v = Math.Clamp(v, 218f, 238f);
            WriteFloat(RegVoltage, v);

            if (_tick % 6 == 0)
            {
                _coils[CoilRunning] = !_coils[CoilRunning];
                _log($"模拟量  温度 {t:F1}°C  计数 {_count}  电压 {v:F1}V  运行 {(_coils[CoilRunning] ? "ON" : "OFF")}");
            }
        }
    }

    public byte[]? HandleRequest(byte[] frame)
    {
        if (frame.Length < 8)
        {
            _log("收到不完整帧（不足 8 字节），忽略");
            return null;
        }

        byte slave = frame[0];
        byte func = frame[1];
        if (slave != _slaveId)
        {
            _log($"从站号 {slave} 不匹配（本从站 {_slaveId}），静默");
            return null;
        }

        ushort crc = ProtocolKit.Crc16(frame[..^2]);
        if (frame[^2] != (byte)crc || frame[^1] != (byte)(crc >> 8))
        {
            _log("CRC16 校验失败，静默不应答");
            return null;
        }

        _log($"收到 {ProtocolKit.ToHex(frame)}");
        return func switch
        {
            0x01 or 0x02 => ReadBits(frame, func, "线圈/离散输入"),
            0x03 or 0x04 => ReadRegisters(frame, func, "保持/输入寄存器"),
            0x05 => WriteSingleCoil(frame),
            0x06 => WriteSingleRegister(frame),
            _ => BuildException(func, 0x01), // 非法功能码
        };
    }

    private byte[]? ReadRegisters(byte[] f, byte func, string kind)
    {
        int start = (f[2] << 8) | f[3];
        int qty = (f[4] << 8) | f[5];
        lock (_gate)
        {
            if (qty is <= 0 or > 125) return BuildException(func, 0x03);                    // 非法数据值
            if (start < 0 || start + qty > RegCapacity) return BuildException(func, 0x02);  // 非法数据地址

            var payload = new byte[1 + qty * 2];
            payload[0] = (byte)(qty * 2);
            for (int i = 0; i < qty; i++)
            {
                ushort v = _regs[start + i];
                payload[1 + i * 2] = (byte)(v >> 8);
                payload[2 + i * 2] = (byte)v;
            }
            _log($"读{kind}  地址 {start}  数量 {qty}");
            return BuildReply(func, payload);
        }
    }

    private byte[]? ReadBits(byte[] f, byte func, string kind)
    {
        int start = (f[2] << 8) | f[3];
        int qty = (f[4] << 8) | f[5];
        lock (_gate)
        {
            if (qty is <= 0 or > 2000) return BuildException(func, 0x03);
            if (start < 0 || start + qty > CoilCapacity) return BuildException(func, 0x02);

            int byteCount = (qty + 7) / 8;
            var payload = new byte[1 + byteCount];
            payload[0] = (byte)byteCount;
            for (int i = 0; i < qty; i++)
                if (_coils[start + i])
                    payload[1 + i / 8] |= (byte)(1 << (i % 8));

            _log($"读{kind}  地址 {start}  数量 {qty}");
            return BuildReply(func, payload);
        }
    }

    private byte[]? WriteSingleRegister(byte[] f)
    {
        int addr = (f[2] << 8) | f[3];
        ushort value = (ushort)((f[4] << 8) | f[5]);
        lock (_gate)
        {
            if (addr < 0 || addr >= RegCapacity) return BuildException(0x06, 0x02);
            _regs[addr] = value;
            if (addr == RegCount) _count = value; // 与自增计数保持同步，方便演示"写后读"
            _log($"写寄存器  地址 {addr} = {value} → 回显 {ProtocolKit.ToHex(f)}");
        }
        return f;
    }

    private byte[]? WriteSingleCoil(byte[] f)
    {
        int addr = (f[2] << 8) | f[3];
        int raw = (f[4] << 8) | f[5];
        if (raw is not (0xFF00 or 0x0000))
            return BuildException(0x05, 0x03);

        lock (_gate)
        {
            if (addr < 0 || addr >= CoilCapacity) return BuildException(0x05, 0x02);
            bool on = raw == 0xFF00;
            _coils[addr] = on;
            _log($"写线圈  地址 {addr} = {(on ? "ON" : "OFF")} → 回显 {ProtocolKit.ToHex(f)}");
        }
        return f;
    }

    // ---------- 帧构造辅助 ----------

    private byte[] BuildReply(byte func, byte[] payload)
    {
        var resp = new byte[2 + payload.Length + 2];
        resp[0] = _slaveId;
        resp[1] = func;
        Array.Copy(payload, 0, resp, 2, payload.Length);
        AppendCrc(resp, resp.Length - 2);
        _log($"应答 {ProtocolKit.ToHex(resp)}");
        return resp;
    }

    private byte[] BuildException(byte func, byte code)
    {
        var resp = new byte[5];
        resp[0] = _slaveId;
        resp[1] = (byte)(func | 0x80);
        resp[2] = code;
        AppendCrc(resp, 3);
        _log($"异常应答 {ProtocolKit.ToHex(resp)}（异常码 {code}）");
        return resp;
    }

    private void AppendCrc(byte[] frame, int length)
    {
        ushort crc = ProtocolKit.Crc16(frame[..length]);
        frame[length] = (byte)crc;
        frame[length + 1] = (byte)(crc >> 8);
    }

    // ---------- Float 与寄存器表互转（大端） ----------

    private float ReadFloat(int baseReg)
    {
        var b = new byte[4];
        b[0] = (byte)(_regs[baseReg] >> 8);
        b[1] = (byte)_regs[baseReg];
        b[2] = (byte)(_regs[baseReg + 1] >> 8);
        b[3] = (byte)_regs[baseReg + 1];
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        return BitConverter.ToSingle(b);
    }

    private void WriteFloat(int baseReg, float value)
    {
        var b = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        _regs[baseReg] = (ushort)((b[0] << 8) | b[1]);
        _regs[baseReg + 1] = (ushort)((b[2] << 8) | b[3]);
    }
}

// =====================================================================================
// 引擎二：自定义规则从站（--mode rule --rules xxx.json）
//   按"请求帧 → 应答模板"精确匹配，可模拟任意一问一答协议（不必是 Modbus 帧形）。
//   应答模板支持注入共享模拟量 {temp:Float} {count:UInt16} {voltage:Float} {running:Bool}
//   以及帧尾校验占位 {crc16} {xor} {sum}。请求帧支持 {crc16} 等校验占位展开后精确匹配，
//   不支持 {value}（写入场景请用 RTU 引擎的 06/05 功能码演示）。
//   规则文件 JSON（参考 rules.example.json）：
//   [ { "name": "温度查询", "request": "AA 55 01", "reply": "AA 55 {temp:Float} {crc16}" } ]
// =====================================================================================
internal sealed class RuleSlave : ISlaveEngine
{
    private sealed record Rule(string Name, byte[] Request, string[] ReplyTokens);

    private readonly List<Rule> _rules = new();
    private readonly DemoSignals _sig = new();
    private readonly Action<string> _log;

    public RuleSlave(Action<string> log, string rulesFile)
    {
        _log = log;
        Load(rulesFile);
    }

    public string Describe() =>
        $"已加载 {_rules.Count} 条应答规则 · 模拟变量 {string.Join(" / ", DemoSignals.Names)}";

    public void SimulateTick() => _sig.Tick();

    private void Load(string file)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException($"找不到规则文件：{file}");

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("规则文件顶层必须是 JSON 数组（参考 rules.example.json）");

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string name = el.GetProperty("name").GetString() ?? "未命名规则";
            string requestText = el.GetProperty("request").GetString() ?? "";
            string replyText = el.GetProperty("reply").GetString() ?? "";

            if (requestText.Contains("{value}", StringComparison.Ordinal))
                throw new FormatException($"规则「{name}」的 request 不支持 {{value}}（请求必须精确匹配）");

            byte[] request = ProtocolKit.RenderText(requestText);
            _rules.Add(new Rule(name, request, ProtocolKit.SplitTokens(replyText)));
            _log($"规则「{name}」 请求 {ProtocolKit.ToHex(request)}");
        }
        if (_rules.Count == 0)
            throw new FormatException("规则文件为空");
    }

    public byte[]? HandleRequest(byte[] frame)
    {
        _log($"收到 {ProtocolKit.ToHex(frame)}");
        foreach (var rule in _rules)
        {
            if (frame.AsSpan().SequenceEqual(rule.Request))
            {
                byte[] reply = ProtocolKit.Render(rule.ReplyTokens, resolveVar: ResolveVar);
                _log($"命中规则「{rule.Name}」 → 应答 {ProtocolKit.ToHex(reply)}");
                return reply;
            }
        }
        _log("无匹配规则，静默");
        return null;
    }

    /// <summary>把 {name:Type} token 解析为共享模拟量的大端编码字节。</summary>
    private byte[] ResolveVar(string token)
    {
        string inner = token[1..^1]; // 去掉 {}
        int colon = inner.IndexOf(':');
        if (colon <= 0 || colon == inner.Length - 1)
            throw new FormatException($"应答模板 token 格式应为 {{name:Type}}，实际 {token}");
        return ProtocolKit.EncodeValue(inner[(colon + 1)..], _sig.Get(inner[..colon]));
    }
}

/// <summary>共享模拟量：供自定义规则的应答模板注入，随 300ms 定时器缓慢变化。</summary>
internal sealed class DemoSignals
{
    public static readonly string[] Names = { "temp", "count", "voltage", "running" };

    private readonly object _gate = new();
    private readonly Random _rnd = new();
    private float _temp = 25f;
    private ushort _count;
    private float _voltage = 228f;
    private bool _running;
    private int _tick;

    public object Get(string name)
    {
        lock (_gate)
        {
            return name.ToLowerInvariant() switch
            {
                "temp" => _temp,
                "count" => _count,
                "voltage" => _voltage,
                "running" => _running,
                _ => throw new FormatException($"未知模拟变量：{name}（可选 {string.Join(" / ", Names)}）"),
            };
        }
    }

    public void Tick()
    {
        lock (_gate)
        {
            _tick++;
            _temp = Math.Clamp(_temp + (float)((_rnd.NextDouble() - 0.5) * 1.2), 20f, 80f);
            _count = (ushort)((_count + 1) % 60001);
            _voltage = Math.Clamp(_voltage + (float)((_rnd.NextDouble() - 0.5) * 0.8), 218f, 238f);
            if (_tick % 6 == 0) _running = !_running;
        }
    }
}
