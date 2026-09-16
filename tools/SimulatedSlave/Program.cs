using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using IndustrialProtocolAssistant.Drivers;
using NModbus;

namespace SimulatedSlave;

/// <summary>
/// Modbus 仿真从站（无真实 PLC 时的演示数据源），可同时监听 TCP 与 RTU。
/// 数据布局（与本仓库 UI 端预置 Tag 定义一致）：
///   - 保持寄存器 0~1 : 温度 Temperature (Float, IEEE754, 占 2 个寄存器)
///   - 保持寄存器 2    : 计数 Count (UInt16)
///   - 线圈 0          : 运行状态 Running (Bool)
/// 用法：
///   dotnet run -- 5020                仅 TCP，监听 127.0.0.1:5020
///   dotnet run -- 5020 COM3 9600      TCP + RTU 双通道（RTU 走串口 COM3）
///   dotnet run -- -    COM3 9600      仅 RTU（第一个参数传 "-" 跳过 TCP）
/// </summary>
internal static class Program
{
    private const byte SlaveId = 1;

    // 寄存器地址常量（与 UI 端 TagDefinition 对应）
    private const ushort RegTempBase = 0; // Float，占 2 个寄存器
    private const ushort RegCount     = 2; // UInt16，占 1 个寄存器
    private const ushort CoilRunning  = 0; // Bool

    private static async Task Main(string[] args)
    {
        // 参数解析：[0]=TCP 端口(默认 5020，"-" 表示不启动 TCP)，[1]=RTU 串口(默认不启动)，[2]=波特率(默认 9600)
        int? port = 5020; // 默认启用 TCP
        if (args.Length > 0 && args[0].Equals("-", StringComparison.OrdinalIgnoreCase))
            port = null; // 显式跳过 TCP
        else if (args.Length > 0 && int.TryParse(args[0], out var p) && p > 0 && p <= 65535)
            port = p;
        string? rtuCom = args.Length > 1 && !args[1].Equals("-", StringComparison.OrdinalIgnoreCase) ? args[1] : null;
        int baud = args.Length > 2 && int.TryParse(args[2], out var b) && b > 0 ? b : 9600;

        var factory = new ModbusFactory();

        // TCP 与 RTU 从站共享同一份数据源，保证两条通道看到的数据完全一致
        // 已知地址区间：保持寄存器 0~2（温度 Float 占 0-1，计数 UInt16 占 2）、线圈 0（运行状态）
        var dataStore = new SharedSlaveDataStore(
            Console.WriteLine,
            new[] { (RegTempBase, (ushort)3) },   // 保持寄存器 0~2
            new[] { (CoilRunning, (ushort)1) });  // 线圈 0
        var slave = factory.CreateSlave(SlaveId, dataStore);

        // 取消令牌：Ctrl+C 优雅退出
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // 启动数据模拟任务（周期更新寄存器/线圈）
        _ = Task.Run(() => SimulateDataAsync(slave, cts.Token));

        // ---------- TCP 通道 ----------
        TcpListener? listener = null;
        NModbus.IModbusSlaveNetwork? tcpNetwork = null;
        if (port is not null)
        {
            var address = new IPAddress(new byte[] { 127, 0, 0, 1 });
            listener = new TcpListener(address, port.Value);
            listener.Start();
            tcpNetwork = factory.CreateSlaveNetwork(listener);
            tcpNetwork.AddSlave(slave);
            Console.WriteLine($"[SimulatedSlave] Modbus TCP 从站：{address}:{port.Value}   从站 ID : {SlaveId}");
        }

        // ---------- RTU 通道 ----------
        NModbus.IModbusSlaveNetwork? rtuNetwork = null;
        if (rtuCom is not null)
        {
            var serial = new SerialPort(rtuCom, baud, Parity.None, 8, StopBits.One);
            try
            {
                serial.Open();
                var rtuSlave = factory.CreateSlave(SlaveId, dataStore); // 与 TCP 从站共享同一份数据
                rtuNetwork = factory.CreateRtuSlaveNetwork(new SerialPortStreamResource(serial));
                rtuNetwork.AddSlave(rtuSlave);
                Console.WriteLine($"[SimulatedSlave] Modbus RTU 从站：{rtuCom} @ {baud} bps, 8N1   从站 ID : {SlaveId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimulatedSlave] 打开串口 {rtuCom} 失败：{ex.Message}");
                serial.Dispose();
            }
        }

        Console.WriteLine($"   数据 : 保持寄存器[{RegTempBase}-{RegTempBase + 1}]=温度(Float), " +
                          $"保持寄存器[{RegCount}]=计数(UInt16), 线圈[{CoilRunning}]=运行状态(Bool)");
        Console.WriteLine($"   按 Ctrl+C 停止...");
        Console.WriteLine();

        try
        {
            var tasks = new List<Task>();
            if (tcpNetwork is not null) tasks.Add(tcpNetwork.ListenAsync(cts.Token));
            if (rtuNetwork is not null) tasks.Add(rtuNetwork.ListenAsync(cts.Token));
            if (tasks.Count == 0)
            {
                Console.WriteLine("[SimulatedSlave] 未启用任何通道，直接退出。");
                return;
            }
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        finally
        {
            try { tcpNetwork?.Dispose(); } catch { }
            try { rtuNetwork?.Dispose(); } catch { } // 同时关闭串口
            try { listener?.Stop(); } catch { }
            Console.WriteLine("[SimulatedSlave] 已停止。");
        }
    }

    /// <summary>周期性更新模拟数据：温度随机波动、计数递增、运行状态翻转。</summary>
    private static async Task SimulateDataAsync(IModbusSlave slave, CancellationToken ct)
    {
        var rnd = new Random();

        // 初始状态
        WriteFloat(slave, RegTempBase, 25.0f);
        slave.DataStore.HoldingRegisters.WritePoints(RegCount, new ushort[] { 0 });
        slave.DataStore.CoilDiscretes.WritePoints(CoilRunning, new bool[] { true });

        // 上一次由本模拟任务写入的值（用于检测主站的外部写入）
        var lastTemp = 25.0f;
        ushort lastCount = 0;
        bool lastRunning = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 每次先从 DataStore 读取当前值作为基准：
                // 主站外部写入的值会被尊重，而不是被本地变量强制覆盖。
                ushort count = slave.DataStore.HoldingRegisters.ReadPoints(RegCount, 1)[0];
                bool running = slave.DataStore.CoilDiscretes.ReadPoints(CoilRunning, 1)[0];
                var temp = ReadFloat(slave, RegTempBase);

                // 检测主站外部写入：当前值 != 上一次模拟写入的值
                if (count != lastCount)
                    Console.WriteLine($"[主站写入] 收到外部写入：计数 = {count}");
                if (Math.Abs(temp - lastTemp) > 0.01f)
                    Console.WriteLine($"[主站写入] 收到外部写入：温度 = {temp:F1} °C");
                if (running != lastRunning)
                    Console.WriteLine($"[主站写入] 收到外部写入：运行状态 = {running}");

                // 温度：在上一次值附近随机游走（±5°C，钳制在 20~80）
                temp = Math.Clamp(temp + (float)(rnd.NextDouble() * 10.0 - 5.0), 20f, 80f);
                WriteFloat(slave, RegTempBase, temp);

                // 计数：每周期 +1（从当前值继续）
                count = (ushort)((count + 1) % 65535);
                slave.DataStore.HoldingRegisters.WritePoints(RegCount, new[] { count });

                // 运行状态：每 20 个周期翻转一次，模拟设备启停
                if (count % 20 == 0) running = !running;
                slave.DataStore.CoilDiscretes.WritePoints(CoilRunning, new[] { running });

                lastTemp = temp;
                lastCount = count;
                lastRunning = running;

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 温度={temp:F1} °C  计数={count}  运行={running}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimulatedSlave] 数据更新异常: {ex.Message}");
            }

            await Task.Delay(2000, ct); // 每 2 秒更新一次
        }
    }

    /// <summary>从连续 2 个保持寄存器读取 Float（与 WriteFloat 对称，高字在前）。</summary>
    private static float ReadFloat(IModbusSlave slave, ushort startAddress)
    {
        var points = slave.DataStore.HoldingRegisters.ReadPoints(startAddress, 2);
        ushort high = points[0];
        ushort low = points[1];
        byte[] bytes = new byte[4];
        BitConverter.GetBytes(high).CopyTo(bytes, 2);
        BitConverter.GetBytes(low).CopyTo(bytes, 0);
        return BitConverter.ToSingle(bytes, 0);
    }

    /// <summary>将 Float 按 IEEE754 拆成 2 个 ushort 写入连续保持寄存器（大端/ABCD，与 NModbus 默认一致）。</summary>
    private static void WriteFloat(IModbusSlave slave, ushort startAddress, float value)
    {
        var bytes = BitConverter.GetBytes(value); // 4 字节
        // NModbus 寄存器为 ushort(2 字节)，按大端字序写入：先高字后低字
        ushort high = BitConverter.ToUInt16(bytes, 2);
        ushort low  = BitConverter.ToUInt16(bytes, 0);
        slave.DataStore.HoldingRegisters.WritePoints(startAddress, new[] { high, low });
    }
}

/// <summary>
/// 线程安全的点存储（数组 + 锁），供多个从站实例共享：
/// TCP 与 RTU 从站指向同一数据源，保证两条通道读写一致。
/// 写入时会检测"未定义地址"：主站写入超出 known 区间的地址时打印警告，便于发现配置错误。
/// </summary>
internal sealed class SharedPointSource<TPoint> : IPointSource<TPoint>
{
    private readonly TPoint[] _points;
    private readonly object _gate = new();
    private readonly string _kind;                             // 点位类型名（用于日志）
    private readonly Action<string> _log;                      // 日志输出
    private readonly (ushort Start, ushort Count)[] _known;    // 已定义地址区间

    public SharedPointSource(int capacity, string kind, Action<string> log, (ushort Start, ushort Count)[] known)
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

/// <summary>共享数据源：模拟从站 4 类点位（线圈/离散输入/保持寄存器/输入寄存器）。</summary>
internal sealed class SharedSlaveDataStore : ISlaveDataStore
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

    public SharedSlaveDataStore(
        Action<string> log,
        (ushort Start, ushort Count)[] knownHoldings,
        (ushort Start, ushort Count)[] knownCoils)
    {
        HoldingRegisters = new SharedPointSource<ushort>(Capacity, KindHolding, log, knownHoldings);
        InputRegisters   = new SharedPointSource<ushort>(Capacity, KindInput, log, Array.Empty<(ushort, ushort)>());
        CoilDiscretes    = new SharedPointSource<bool>(Capacity, KindCoil, log, knownCoils);
        CoilInputs       = new SharedPointSource<bool>(Capacity, KindDiscrete, log, Array.Empty<(ushort, ushort)>());
    }
}
