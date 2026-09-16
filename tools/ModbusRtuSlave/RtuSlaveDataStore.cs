using NModbus;

namespace ModbusRtuSlave;

/// <summary>
/// 线程安全的点位存储（数组 + 锁）。
/// 写入时会检测"未定义地址"：主站写入超出 known 区间的地址时打印警告，便于发现 Tag 配置错误。
/// </summary>
internal sealed class RtuPointSource<TPoint> : IPointSource<TPoint>
{
    private readonly TPoint[] _points;
    private readonly object _gate = new();
    private readonly string _kind;                             // 点位类型名（用于日志）
    private readonly Action<string> _log;                      // 日志输出
    private readonly (ushort Start, ushort Count)[] _known;    // 已定义地址区间

    public RtuPointSource(int capacity, string kind, Action<string> log, (ushort Start, ushort Count)[] known)
    {
        _points = new TPoint[capacity];
        _kind = kind;
        _log = log;
        _known = known;
    }

    public TPoint[] ReadPoints(ushort startAddress, ushort numberOfPoints)
    {
        lock (_gate)
        {
            var buffer = new TPoint[numberOfPoints];
            Array.Copy(_points, startAddress, buffer, 0, numberOfPoints);
            return buffer;
        }
    }

    public void WritePoints(ushort startAddress, TPoint[] points)
    {
        lock (_gate)
        {
            Array.Copy(points, 0, _points, startAddress, points.Length);
        }
        WarnIfUnknownWrite(startAddress, points.Length);
    }

    /// <summary>收集写入区间中未定义的地址并打印警告（已定义区间内的写入不打扰）。</summary>
    private void WarnIfUnknownWrite(ushort startAddress, int count)
    {
        var unknowns = new List<ushort>();
        for (var i = 0; i < count; i++)
        {
            var addr = (ushort)(startAddress + i);
            var known = false;
            foreach (var (s, c) in _known)
            {
                if (addr >= s && addr < s + c) { known = true; break; }
            }
            if (!known) unknowns.Add(addr);
        }
        if (unknowns.Count > 0)
            _log($"[警告] 主站写入了未定义的{_kind}：地址 {string.Join(", ", unknowns)}");
    }
}

/// <summary>
/// RTU 从站数据源：模拟 4 类点位（线圈 / 离散输入 / 保持寄存器 / 输入寄存器）。
/// 保持寄存器与线圈承载演示数据；离散输入与输入寄存器留空（读返回 0，供主站读测试）。
/// </summary>
internal sealed class RtuSlaveDataStore : ISlaveDataStore
{
    private const int Capacity = 65536; // Modbus 地址空间上限
    private const string KindCoil = "线圈";
    private const string KindDiscrete = "离散输入";
    private const string KindHolding = "保持寄存器";
    private const string KindInput = "输入寄存器";

    public IPointSource<bool> CoilDiscretes { get; }
    public IPointSource<bool> CoilInputs { get; }
    public IPointSource<ushort> HoldingRegisters { get; }
    public IPointSource<ushort> InputRegisters { get; }

    public RtuSlaveDataStore(
        Action<string> log,
        (ushort Start, ushort Count)[] knownHoldings,
        (ushort Start, ushort Count)[] knownCoils)
    {
        HoldingRegisters = new RtuPointSource<ushort>(Capacity, KindHolding, log, knownHoldings);
        InputRegisters   = new RtuPointSource<ushort>(Capacity, KindInput, log, Array.Empty<(ushort, ushort)>());
        CoilDiscretes    = new RtuPointSource<bool>(Capacity, KindCoil, log, knownCoils);
        CoilInputs       = new RtuPointSource<bool>(Capacity, KindDiscrete, log, Array.Empty<(ushort, ushort)>());
    }
}
