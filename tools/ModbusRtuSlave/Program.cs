using System.IO.Ports;
using IndustrialProtocolAssistant.Drivers;
using NModbus;

namespace ModbusRtuSlave;

/// <summary>
/// ModbusRtuSlave —— 标准 Modbus RTU 串口从站模拟器（工业协议调试助手的配套工具）。
/// 在无真实 PLC / 仪表时，模拟一台 Modbus RTU 从站，供调试助手中的 Modbus RTU 主站驱动联调演示。
///
/// 基于 NModbus 从站协议栈实现，完整支持标准 RTU 功能码：
///   01/02 读线圈与离散输入、03/04 读寄存器、05/06 写单点、0F/10 批量写（含 CRC16 校验）。
///
/// 数据布局（与调试助手 UI 端预置 Tag、SimulatedSlave(TCP) 模拟器完全一致）：
///   - 保持寄存器 0~1 : 温度 Temperature (Float, IEEE754, 高字在前)
///   - 保持寄存器 2    : 计数 Count (UInt16)
///   - 线圈 0          : 运行状态 Running (Bool)
///
/// 说明：串口程序之间通信需要成对的虚拟串口（如 com0com），主站连一端、本工具连另一端。
/// </summary>
internal static class Program
{
    // 寄存器地址常量（与 UI 端 TagDefinition / SimulatedSlave 保持一致）
    private const ushort RegTempBase = 0; // Float，占 2 个寄存器
    private const ushort RegCount     = 2; // UInt16，占 1 个寄存器
    private const ushort CoilRunning  = 0; // Bool

    private static async Task<int> Main(string[] args)
    {
        string? com = null;
        int baud = 9600;
        byte slaveId = 1;
        var noSim = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "help":
                    PrintHelp();
                    return 0;
                case "--com": com = Next(args, ref i); break;
                case "--baud": baud = ParseInt(Next(args, ref i), 9600, "波特率"); break;
                case "--slave-id": slaveId = (byte)Math.Clamp(ParseInt(Next(args, ref i), 1, "从站号"), 1, 247); break;
                case "--no-sim": noSim = true; break;
                default:
                    // 位置参数兼容：ModbusRtuSlave COM3 9600
                    if (args[i].StartsWith("COM", StringComparison.OrdinalIgnoreCase)) com = args[i];
                    else if (int.TryParse(args[i], out var v)) baud = v;
                    else
                    {
                        Console.Error.WriteLine($"未知参数：{args[i]}（--help 查看用法）");
                        return 1;
                    }
                    break;
            }
        }

        if (com is null)
        {
            Console.Error.WriteLine("缺少串口参数，示例：ModbusRtuSlave --com COM3 --baud 9600（--help 查看全部用法）");
            return 1;
        }

        PrintBanner(com, baud, slaveId);

        // ---- 打开串口（RTU 帧格式固定 8N1，与主站 ModbusRtuDriver 一致）----
        using var serial = new SerialPort(com, baud, Parity.None, 8, StopBits.One)
        {
            WriteTimeout = 2000,
            ReadTimeout = 1000,
        };
        try
        {
            serial.Open();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"无法打开串口 {com}：{ex.Message}");
            Console.Error.WriteLine($"可用串口：{string.Join(", ", SerialPort.GetPortNames())}");
            PrintVirtualSerialHint();
            return 1;
        }

        // ---- NModbus RTU 从站（自动处理 RTU 帧解析、CRC 校验、功能码应答）----
        var factory = new ModbusFactory();
        var dataStore = new RtuSlaveDataStore(
            Log,
            new[] { (RegTempBase, (ushort)3) },  // 已定义保持寄存器 0~2
            new[] { (CoilRunning, (ushort)1) }); // 已定义线圈 0
        var slave = factory.CreateSlave(slaveId, dataStore);

        IModbusSlaveNetwork rtuNetwork;
        try
        {
            rtuNetwork = factory.CreateRtuSlaveNetwork(new SerialPortStreamResource(serial));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"创建 RTU 从站网络失败：{ex.Message}");
            return 1;
        }
        rtuNetwork.AddSlave(slave);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine();
        Console.WriteLine("== 数据布局（与调试助手 UI 预置 Tag 一致）==");
        Console.WriteLine($"  保持寄存器 [{RegTempBase}-{RegTempBase + 1}] : 温度 Temperature (Float, 高字在前)");
        Console.WriteLine($"  保持寄存器 [{RegCount}]                 : 计数 Count (UInt16)");
        Console.WriteLine($"  线圈 [{CoilRunning}]                     : 运行状态 Running (Bool)");
        Console.WriteLine();
        Console.WriteLine("== 主站 Tag 配置示例（Modbus RTU 驱动）==");
        PrintMasterExamples();
        Console.WriteLine();
        Log($"开始监听 {com} @ {baud} bps 8N1 · 从站号 {slaveId}{(noSim ? " · 固定数据（--no-sim）" : "")}，按 Ctrl+C 停止……");

        // 写入初始数据（无论是否模拟都会生效，--no-sim 时保持固定便于写值验证）
        InitData(slave);

        // ---- 周期刷新模拟量（每 2 秒；--no-sim 时不启动，数据保持初始值）----
        if (!noSim)
            _ = Task.Run(() => SimulateDataAsync(slave, cts.Token));

        try
        {
            await rtuNetwork.ListenAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C 正常退出
        }
        catch (Exception ex)
        {
            Log($"RTU 从站监听异常：{ex.Message}");
        }
        finally
        {
            try { rtuNetwork.Dispose(); } catch { /* Dispose 同时关闭串口 */ }
            Log("已停止。");
        }
        return 0;
    }

    /// <summary>写入演示初始状态：温度 25.0 °C、计数 0、运行状态 true。</summary>
    private static void InitData(IModbusSlave slave)
    {
        WriteFloat(slave, RegTempBase, 25.0f);
        slave.DataStore.HoldingRegisters.WritePoints(RegCount, new ushort[] { 0 });
        slave.DataStore.CoilDiscretes.WritePoints(CoilRunning, new bool[] { true });
    }

    /// <summary>
    /// 周期性更新模拟数据：温度随机波动、计数递增、运行状态翻转。
    /// 每次先读 DataStore 当前值作为基准，主站外部写入的值会被尊重而不是被本地变量强制覆盖。
    /// </summary>
    private static async Task SimulateDataAsync(IModbusSlave slave, CancellationToken ct)
    {
        var rnd = new Random();

        // 上一次由本模拟任务写入的值（用于检测主站的外部写入，初始与 InitData 相同）
        var lastTemp = ReadFloat(slave, RegTempBase);
        var lastCount = slave.DataStore.HoldingRegisters.ReadPoints(RegCount, 1)[0];
        var lastRunning = slave.DataStore.CoilDiscretes.ReadPoints(CoilRunning, 1)[0];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                ushort count = slave.DataStore.HoldingRegisters.ReadPoints(RegCount, 1)[0];
                var running = slave.DataStore.CoilDiscretes.ReadPoints(CoilRunning, 1)[0];
                var temp = ReadFloat(slave, RegTempBase);

                // 检测主站外部写入：当前值 != 上一轮本任务写入的值
                if (count != lastCount) Log($"[主站写入] 收到外部写入：计数 = {count}");
                if (Math.Abs(temp - lastTemp) > 0.01f) Log($"[主站写入] 收到外部写入：温度 = {temp:F1} °C");
                if (running != lastRunning) Log($"[主站写入] 收到外部写入：运行状态 = {running}");

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

                Log($"温度={temp:F1} °C  计数={count}  运行={running}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"数据更新异常：{ex.Message}");
            }

            await Task.Delay(2000, ct);
        }
    }

    /// <summary>从连续 2 个保持寄存器读取 Float（与 WriteFloat 对称，高字在前）。</summary>
    private static float ReadFloat(IModbusSlave slave, ushort startAddress)
    {
        var points = slave.DataStore.HoldingRegisters.ReadPoints(startAddress, 2);
        byte[] bytes = new byte[4];
        BitConverter.GetBytes(points[0]).CopyTo(bytes, 2); // 高字
        BitConverter.GetBytes(points[1]).CopyTo(bytes, 0); // 低字
        return BitConverter.ToSingle(bytes, 0);
    }

    /// <summary>将 Float 按 IEEE754 拆成 2 个 ushort 写入连续保持寄存器（大端/ABCD，与 NModbus 默认一致）。</summary>
    private static void WriteFloat(IModbusSlave slave, ushort startAddress, float value)
    {
        var bytes = BitConverter.GetBytes(value); // 4 字节
        ushort high = BitConverter.ToUInt16(bytes, 2);
        ushort low  = BitConverter.ToUInt16(bytes, 0);
        slave.DataStore.HoldingRegisters.WritePoints(startAddress, new[] { high, low });
    }

    // ---------- 输出辅助 ----------

    private static string Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : "";

    private static int ParseInt(string text, int fallback, string what)
    {
        if (int.TryParse(text, out var v)) return v;
        Console.Error.WriteLine($"参数 {what} 不是合法数字：{text}");
        return fallback;
    }

    private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

    private static void PrintBanner(string com, int baud, byte slaveId)
    {
        Console.WriteLine();
        Console.WriteLine("================ ModbusRtuSlave 串口从站模拟器 ================");
        Console.WriteLine($"监听串口 {com} @ {baud} bps 8N1 · 从站号 {slaveId} · 标准 Modbus RTU");
        Console.WriteLine("=============================================================");
        PrintVirtualSerialHint();
    }

    private static void PrintMasterExamples()
    {
        Console.WriteLine("  在 WPF 主站中新建 Modbus RTU 设备，连接主站端串口（如 COM3），");
        Console.WriteLine("  串口/波特率/从站号要与本模拟器一致，帧格式 8N1 固定；");
        Console.WriteLine("  Tag 地址填纯寄存器号（0 基，不是 4xxxx 那种惯用编号）：");
        Console.WriteLine("    名称    类型    地址");
        Console.WriteLine("    温度    Float   0     → 保持寄存器 0~1（高字在前）");
        Console.WriteLine("    计数    UInt16  2     → 保持寄存器 2");
        Console.WriteLine("    运行    Bool    0     → Bool 自动走线圈（FC01 读 / FC05 写）");
        Console.WriteLine("  “写值”面板可写：计数(UInt16→FC06)、温度(Float→FC16 批量写)、运行(Bool→FC05)。");
    }

    private static void PrintVirtualSerialHint()
    {
        var setupc = FindSetupc();
        Console.WriteLine();
        if (setupc is not null)
            Console.WriteLine($"已检测到 com0com：{setupc}\n  创建虚拟串口对（主站端 COM3 ↔ 从站端 COM4，需管理员权限）：\n  \"{setupc}\" install Portname=COM3 Portname=COM4");
        else
            Console.WriteLine("提示：本机串口程序间通信需要虚拟串口对。推荐安装 com0com（https://com0com.sourceforge.net/）\n  然后运行（管理员）：setupc.exe install Portname=COM3 Portname=COM4\n  主站程序连接 COM3，本模拟器连接 COM4。");
        Console.WriteLine();
    }

    private static string? FindSetupc()
    {
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "com0com"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "com0com"),
        };
        foreach (var dir in dirs)
        {
            var path = Path.Combine(dir, "setupc.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ModbusRtuSlave —— 标准 Modbus RTU 串口从站模拟器（工业协议调试助手的配套工具）

            用法：
              ModbusRtuSlave [COM口] [波特率]              位置参数简写
              ModbusRtuSlave --com COM3 --baud 9600 [选项]

            选项：
              --com <名>      监听串口（默认 COM3；需为虚拟串口对/真机串口的一端）
              --baud <值>     波特率（默认 9600）
              --slave-id <值> 从站号（默认 1，范围 1~247）
              --no-sim        固定初始数据（温度=25.0、计数=0、运行=true），
                              不周期变化，便于验证主站“写值”后能稳定读回
              -h, --help      显示本帮助

            功能码支持（由 NModbus 从站协议栈提供）：
              01 读线圈 / 02 读离散输入 / 03 读保持寄存器 / 04 读输入寄存器
              05 写单线圈 / 06 写单保持寄存器 / 0F 批量写线圈 / 10 批量写保持寄存器
              自动计算与校验 CRC16；写值（FC05/06/0F/10）同样完整支持。

            第一步（虚拟串口）：
              串口程序之间通信需成对虚拟串口。安装 com0com 后以管理员运行：
              setupc.exe install Portname=COM3 Portname=COM4
              （主站程序连接 COM3，本模拟器连接 COM4；真实设备则直接选对应串口）

            第二步（启动模拟器）：
              ModbusRtuSlave --com COM4 --baud 9600 --slave-id 1

            第三步（主站联调）：
              在调试助手中新建设备，协议选 Modbus RTU、串口 COM3、9600、从站号 1，
              Tag 配置（地址填纯寄存器号，类型决定数据区）：
                温度  Float   0    保持寄存器 0~1（高字在前）
                计数  UInt16  2    保持寄存器 2
                运行  Bool    0    线圈 0
              写值：计数(FC06)、温度 Float(FC16)、运行(FC05)。
            """);
    }
}
