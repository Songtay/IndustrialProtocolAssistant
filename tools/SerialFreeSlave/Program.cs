using System.IO.Ports;

namespace SerialFreeSlave;

/// <summary>
/// SerialFreeSlave —— SerialFree 串口从站模拟器。
/// 监听一个串口并应答主站请求，配合工业协议调试助手中的 SerialFree 主站驱动联调演示。
/// 两种引擎：
///   rtu  （默认）解析"类 Modbus RTU"请求（01/02/03/04/05/06 功能码 + CRC16），开箱即用；
///   rule 按 JSON 规则文件精确匹配"请求帧 → 应答模板"，可模拟任意自定义帧协议。
/// 说明：串口程序之间通信需要成对的虚拟串口（如 com0com），主站连一端、本工具连另一端。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? com = null;
        int baud = 9600;
        string mode = "rtu";
        string? rulesFile = null;
        byte slaveId = 1;
        int gapMs = 20;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "help":
                    PrintHelp();
                    return 0;
                case "--com": com = Next(args, ref i); break;
                case "--baud": baud = ParseInt(Next(args, ref i), 9600, "波特率"); break;
                case "--mode": mode = Next(args, ref i).ToLowerInvariant(); break;
                case "--rules": rulesFile = Next(args, ref i); break;
                case "--slave-id": slaveId = (byte)Math.Clamp(ParseInt(Next(args, ref i), 1, "从站号"), 1, 247); break;
                case "--gap": gapMs = Math.Max(5, ParseInt(Next(args, ref i), 20, "帧间隔")); break;
                default:
                    // 位置参数兼容：SerialFreeSlave COM4 9600
                    if (args[i].StartsWith("COM", StringComparison.OrdinalIgnoreCase)) com = args[i];
                    else if (int.TryParse(args[i], out int v)) baud = v;
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
            Console.Error.WriteLine("缺少串口参数，示例：SerialFreeSlave --com COM4 --baud 9600（--help 查看全部用法）");
            return 1;
        }
        if (mode is not ("rtu" or "rule"))
        {
            Console.Error.WriteLine($"未知引擎模式：{mode}（可选 rtu / rule）");
            return 1;
        }
        if (mode == "rule" && string.IsNullOrWhiteSpace(rulesFile))
        {
            Console.Error.WriteLine("rule 模式需要指定规则文件：--rules rules.example.json");
            return 1;
        }

        PrintBanner(com, baud, mode, gapMs);

        // ---- 引擎 ----
        ISerialSlaveEngine engine;
        try
        {
            engine = mode == "rtu"
                ? new RtuSlave(slaveId, Log)
                : new RuleSlave(Log, rulesFile!);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动引擎失败：{ex.Message}");
            return 1;
        }

        // ---- 打开串口 ----
        using var serial = new SerialPort(com, baud, Parity.None, 8, StopBits.One)
        {
            WriteTimeout = 2000,
            ReadTimeout = 500,
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

        Console.WriteLine();
        Console.WriteLine("== 从站说明 ==");
        Console.WriteLine(engine.Describe());
        Console.WriteLine();
        Console.WriteLine("== 主站 Tag 配置示例（SerialFree 驱动，地址栏直接粘贴）==");
        PrintMasterExamples(mode);
        Console.WriteLine();
        Log($"开始监听 {com} @ {baud}bps 8N1，按 Ctrl+C 停止……");

        // ---- 周期刷新模拟量 ----
        using var simTimer = new Timer(_ =>
        {
            try { engine.SimulateTick(); }
            catch (Exception ex) { Log($"模拟数据更新失败：{ex.Message}"); }
        }, null, 300, 300);

        // ---- 接收主循环（与主站驱动相同的"静默 ≥ 帧间隔判帧结束"）----
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var frame = new List<byte>(64);
        long lastActivity = Environment.TickCount64;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                int n = serial.BytesToRead;
                if (n > 0)
                {
                    var buf = new byte[n];
                    int r = serial.Read(buf, 0, n);
                    for (int i = 0; i < r; i++) frame.Add(buf[i]);
                    lastActivity = Environment.TickCount64;
                    continue;
                }

                if (frame.Count > 0 && Environment.TickCount64 - lastActivity >= gapMs)
                {
                    byte[] request = frame.ToArray();
                    frame.Clear();
                    byte[]? reply = engine.HandleRequest(request);
                    if (reply is not null && reply.Length > 0)
                        serial.Write(reply, 0, reply.Length);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log($"串口异常：{ex.Message}，等待 1 秒后重试……");
                try { serial.Close(); serial.Open(); } catch { /* 下轮再试 */ }
                Thread.Sleep(1000);
            }
            Thread.Sleep(2);
        }

        Log("已停止。");
        return 0;
    }

    // ---------- 输出辅助 ----------

    private static string Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : "";

    private static int ParseInt(string text, int fallback, string what)
    {
        if (int.TryParse(text, out int v)) return v;
        Console.Error.WriteLine($"参数 {what} 不是合法数字：{text}");
        return fallback;
    }

    private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

    private static void PrintBanner(string com, int baud, string mode, int gapMs)
    {
        Console.WriteLine();
        Console.WriteLine("================ SerialFreeSlave 串口从站模拟器 ================");
        Console.WriteLine($"监听串口 {com} @ {baud} bps 8N1 · 帧间隔 {gapMs}ms 判帧 · 引擎 {mode}");
        Console.WriteLine("=================================================================");
        PrintVirtualSerialHint();
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

    private static void PrintMasterExamples(string mode)
    {
        if (mode == "rule")
        {
            Console.WriteLine("  rule 模式：在 WPF 主站中新建 SerialFree 设备，Tag 地址填规则文件里对应的请求帧；");
            Console.WriteLine("  数据偏移 @N 由应答模板决定。规则文件格式见 tools/SerialFreeSlave/rules.example.json。");
            Console.WriteLine("  若改用主站连接参数“帧校验”（设备级自动补帧尾），须保证本设备所有请求帧的校验方式一致，");
            Console.WriteLine("  且与规则文件 request 的校验占位对应（不同校验并存时请保留地址内 {crc16}/{xor} 显式写法）。");
            return;
        }

        Console.WriteLine("  帧尾校验两种做法任选（不会重复追加）：① 主站连接参数“帧校验”选 CRC16/XOR/SUM，地址不写校验；");
        Console.WriteLine("  ② “帧校验”选“无”，在地址里显式写 {crc16}/{xor}/{sum}。下面的示例用的是做法②：");
        Console.WriteLine("  在 WPF 主站（协议选 SerialFree）连主站端串口（如 COM3），Tag 地址直接复制：");
        Console.WriteLine("    名称    类型    地址");
        Console.WriteLine("    温度    Float   01 03 00 00 00 02 {crc16}@3");
        Console.WriteLine("    计数    UInt16  01 03 00 02 00 01 {crc16}@3");
        Console.WriteLine("    电压    Float   01 03 00 03 00 02 {crc16}@3");
        Console.WriteLine("    运行    Bool    01 01 00 00 00 01 {crc16}@3");
        Console.WriteLine("    写计数（在“写值”面板写入，类型 UInt16）：");
        Console.WriteLine("            01 03 00 02 00 01 {crc16}@3 || 01 06 00 02 {value} {crc16}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            SerialFreeSlave —— SerialFree 串口从站模拟器（工业协议调试助手的配套工具）

            用法：
              SerialFreeSlave [COM口] [波特率]                位置参数简写
              SerialFreeSlave --com COM4 --baud 9600 [选项]

            选项：
              --com <名>      监听串口（默认 COM4；需为虚拟串口对/真机串口的一端）
              --baud <值>     波特率（默认 9600）
              --mode rtu|rule 从站引擎（默认 rtu）
                              rtu  ：解析"类 Modbus RTU"请求（01/02/03/04/05/06 + CRC16），
                                     带寄存器/线圈模拟数据，主站零配置即可联调；
                              rule ：按规则文件精确匹配请求→应答，模拟任意自定义帧协议。
              --rules <文件>  规则文件 JSON（配合 --mode rule，参考 rules.example.json）
              --slave-id <值> RTU 引擎从站号（默认 1）
              --gap <ms>      静默帧间隔（默认 20ms，与主站驱动 FrameGapMs 默认一致）
              -h, --help      显示本帮助

            第一步（虚拟串口）：
              串口程序之间通信需成对虚拟串口。安装 com0com 后以管理员运行：
              setupc.exe install Portname=COM3 Portname=COM4
              （主站程序连接 COM3，本模拟器连接 COM4；真实设备则直接选对应串口）

            第二步（启动模拟器）：
              SerialFreeSlave --com COM4 --baud 9600

            第三步（主站联调）：
              在调试助手中新建设备，协议选 SerialFree、串口 COM3、9600；
              帧尾校验可在连接参数“帧校验”下拉选（CRC16/XOR/SUM 自动补帧尾），
              也可在地址内显式写 {crc16} 等（不会重复追加）。示例（RTU 引擎）：
                温度  Float   01 03 00 00 00 02 {crc16}@3
                计数  UInt16  01 03 00 02 00 01 {crc16}@3
                运行  Bool    01 01 00 00 00 01 {crc16}@3
                写计数（写值）：01 03 00 02 00 01 {crc16}@3 || 01 06 00 02 {value} {crc16}

            自定义协议（rule 引擎）：
              SerialFreeSlave --com COM4 --mode rule --rules rules.example.json
            """);
    }
}
