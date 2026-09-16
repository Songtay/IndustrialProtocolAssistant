using System.Net;
using System.Net.Sockets;

namespace SocketSimulator;

/// <summary>
/// SocketSimulator —— Socket（TCP）自由协议服务端模拟器。
/// 监听一个 TCP 端口并应答主站请求，配合工业协议调试助手中的 Socket 主站驱动联调演示
/// （传输层为 TCP，一问一答；地址语法与 SerialFree 完全一致）。
/// 两种引擎：
///   rtu  （默认）解析"类 Modbus RTU"请求（01/02/03/04/05/06 功能码 + CRC16），开箱即用；
///   rule 按 JSON 规则文件精确匹配"请求帧 → 应答模板"，可模拟任意自定义帧协议。
/// 说明：请求帧边界沿用主站一致的"静默 ≥ 帧间隔判帧结束"方式（TCP 流式传输，无串口帧头界定）。
/// </summary>
internal static class Program
{
    /// <summary>日志统一串行化（多客户端连接并发时避免输出交错）。</summary>
    private static readonly object LogGate = new();

    private static int _clientCount;

    private static int Main(string[] args)
    {
        string listenIp = "127.0.0.1";
        int port = 5020;
        string mode = "rtu";
        string? rulesFile = null;
        byte slaveId = 1;
        int gapMs = 30;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "help":
                    PrintHelp();
                    return 0;
                case "--listen": listenIp = Next(args, ref i); break;
                case "--port": port = ParseInt(Next(args, ref i), 5020, "端口"); break;
                case "--mode": mode = Next(args, ref i).ToLowerInvariant(); break;
                case "--rules": rulesFile = Next(args, ref i); break;
                case "--slave-id": slaveId = (byte)Math.Clamp(ParseInt(Next(args, ref i), 1, "从站号"), 1, 247); break;
                case "--gap": gapMs = Math.Max(5, ParseInt(Next(args, ref i), 30, "帧间隔")); break;
                default:
                    // 位置参数兼容：SocketSimulator 5020
                    if (int.TryParse(args[i], out int v)) port = v;
                    else
                    {
                        Console.Error.WriteLine($"未知参数：{args[i]}（--help 查看用法）");
                        return 1;
                    }
                    break;
            }
        }

        if (port is <= 0 or > 65535)
        {
            Console.Error.WriteLine($"端口不合法：{port}（范围 1~65535）");
            return 1;
        }
        if (!IPAddress.TryParse(listenIp, out var bindIp))
        {
            Console.Error.WriteLine($"监听地址不合法：{listenIp}（支持 127.0.0.1 / 0.0.0.0 等）");
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

        // ---- 引擎 ----
        ISlaveEngine engine;
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

        // ---- 开始监听 ----
        var listener = new TcpListener(bindIp, port);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"无法监听 {listenIp}:{port}：{ex.Message}");
            return 1;
        }

        PrintBanner(listenIp, port, mode, gapMs);
        Console.WriteLine();
        Console.WriteLine("== 从站说明 ==");
        Console.WriteLine(engine.Describe());
        Console.WriteLine();
        Console.WriteLine("== 主站 Tag 配置示例（Socket 驱动，地址栏直接粘贴）==");
        PrintMasterExamples(mode);
        Console.WriteLine();
        Log($"开始监听 {listenIp}:{port}，按 Ctrl+C 停止……");

        // ---- 周期刷新模拟量 ----
        using var simTimer = new Timer(_ =>
        {
            try { engine.SimulateTick(); }
            catch (Exception ex) { Log($"模拟数据更新失败：{ex.Message}"); }
        }, null, 300, 300);

        // ---- 接受连接主循环：每客户端独立任务，一问一答 ----
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (SocketException ex)
            {
                if (cts.IsCancellationRequested) break;
                Log($"接受连接异常：{ex.Message}，1 秒后重试……");
                Thread.Sleep(1000);
                continue;
            }
            catch (InvalidOperationException) { break; }

            _ = Task.Run(() => ServeClient(client, engine, gapMs, cts.Token));
        }

        listener.Stop();
        Log("已停止。");
        return 0;
    }

    // ---------- 单客户端会话：读取请求帧（静默判帧）→ 应答 ----------

    private static async Task ServeClient(TcpClient client, ISlaveEngine engine, int gapMs, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            string peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
            int nowCount = Interlocked.Increment(ref _clientCount);
            using var ns = client.GetStream();
            Log($"{peer} 接入连接（当前 {nowCount} 个客户端）");

            var frame = new List<byte>(64);
            long lastActivity = Environment.TickCount64;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (ns.DataAvailable)
                    {
                        var buf = new byte[4096];
                        int n = ns.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        for (int i = 0; i < n; i++) frame.Add(buf[i]);
                        lastActivity = Environment.TickCount64;
                        continue;
                    }

                    // 已有数据且静默超过帧间隔 → 判定整帧结束，交由引擎应答
                    if (frame.Count > 0 && Environment.TickCount64 - lastActivity >= gapMs)
                    {
                        byte[] request = frame.ToArray();
                        frame.Clear();

                        byte[]? reply;
                        try { reply = engine.HandleRequest(request); }
                        catch (Exception ex) { Log($"处理请求异常：{ex.Message}"); continue; }

                        if (reply is { Length: > 0 })
                        {
                            ns.Write(reply, 0, reply.Length);
                            ns.Flush();
                        }
                        continue;
                    }

                    // 空闲且对端已关闭（FIN）→ 结束本会话
                    if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0)
                        break;

                    await Task.Delay(2, ct);
                }
            }
            catch (OperationCanceledException) { /* 程序退出 */ }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Log($"{peer} 连接异常：{ex.Message}");
            }

            Log($"{peer} 断开连接（剩余 {Math.Max(0, Interlocked.Decrement(ref _clientCount))} 个客户端）");
        }
    }

    // ---------- 输出辅助 ----------

    private static string Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : "";

    private static int ParseInt(string text, int fallback, string what)
    {
        if (int.TryParse(text, out int v)) return v;
        Console.Error.WriteLine($"参数 {what} 不是合法数字：{text}");
        return fallback;
    }

    private static void Log(string msg)
    {
        lock (LogGate) Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }

    private static void PrintBanner(string ip, int port, string mode, int gapMs)
    {
        Console.WriteLine();
        Console.WriteLine("================ SocketSimulator TCP 服务端模拟器 ================");
        Console.WriteLine($"监听 {ip}:{port} · 帧间隔 {gapMs}ms 判帧 · 引擎 {mode}");
        Console.WriteLine("==================================================================");
    }

    private static void PrintMasterExamples(string mode)
    {
        if (mode == "rule")
        {
            Console.WriteLine("  rule 模式：在 WPF 主站中新建 Socket 设备（IP + 端口），Tag 地址填规则文件里对应的请求帧；");
            Console.WriteLine("  数据偏移 @N 由应答模板决定。规则文件格式见 tools/SocketSimulator/rules.example.json。");
            Console.WriteLine("  若改用主站连接参数“帧校验”（设备级自动补帧尾），须保证本设备所有请求帧的校验方式一致，");
            Console.WriteLine("  且与规则文件 request 的校验占位对应（不同校验并存时请保留地址内 {crc16}/{xor} 显式写法）。");
            return;
        }

        Console.WriteLine("  帧尾校验两种做法任选（不会重复追加）：① 主站连接参数“帧校验”选 CRC16/XOR/SUM，地址不写校验；");
        Console.WriteLine("  ② “帧校验”选“无”，在地址里显式写 {crc16}/{xor}/{sum}。下面的示例用的是做法②：");
        Console.WriteLine("  在 WPF 主站（协议选 Socket，IP 填本机监听地址、端口填监听端口），Tag 地址直接复制：");
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
            SocketSimulator —— Socket（TCP）服务端模拟器（工业协议调试助手的配套工具）

            用法：
              SocketSimulator [端口]                       位置参数简写
              SocketSimulator --port 5020 [选项]

            选项：
              --listen <IP>     监听地址（默认 127.0.0.1；需局域网联调用 0.0.0.0）
              --port <值>       监听端口（默认 5020，与主站 Socket 驱动默认端口一致）
              --mode rtu|rule   从站引擎（默认 rtu）
                                rtu  ：解析"类 Modbus RTU"请求（01/02/03/04/05/06 + CRC16），
                                       带寄存器/线圈模拟数据，主站零配置即可联调；
                                rule ：按规则文件精确匹配请求→应答，模拟任意自定义帧协议。
              --rules <文件>    规则文件 JSON（配合 --mode rule，参考 rules.example.json）
              --slave-id <值>   RTU 引擎从站号（默认 1）
              --gap <ms>        静默帧间隔（默认 30ms，与主站 Socket 驱动的 30ms 判帧一致）
              -h, --help        显示本帮助

            启动示例：
              SocketSimulator                      # 监听 127.0.0.1:5020，RTU 引擎
              SocketSimulator --port 9000          # 自定义端口
              SocketSimulator --listen 0.0.0.0     # 局域网联调（允许外部主机接入）
              SocketSimulator --mode rule --rules rules.example.json

            主站联调（Socket 驱动）：
              在调试助手中新建设备，协议选 Socket，IP/端口填监听地址（默认 127.0.0.1:5020）；
              帧尾校验可在连接参数“帧校验”下拉选（CRC16/XOR/SUM 自动补帧尾），
              也可在地址内显式写 {crc16} 等（不会重复追加）。示例（RTU 引擎）：
                温度  Float   01 03 00 00 00 02 {crc16}@3
                计数  UInt16  01 03 00 02 00 01 {crc16}@3
                运行  Bool    01 01 00 00 00 01 {crc16}@3
                写计数（写值）：01 03 00 02 00 01 {crc16}@3 || 01 06 00 02 {value} {crc16}

            自定义协议（rule 引擎）：
              SocketSimulator --mode rule --rules rules.example.json
            """);
    }
}
