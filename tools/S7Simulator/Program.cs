using System.Text;
using Snap7;
using Snap7Server;
namespace S7Simulator;

/// <summary>
/// S7 服务端模拟器（基于 Snap7 Server，模拟西门子 S7-300/400 PLC）。
/// 供本仓库上位机（S7 驱动，底层为 S7netplus）在无真实 PLC 时连接、读取、写入调试。
///
/// 用法：
///   S7Simulator                默认监听 0.0.0.0:102，每 500ms 刷新模拟值
///   S7Simulator --port 10200   指定端口（避免占用/冲突）
///   S7Simulator --selftest     启动后用 S7netplus 自连验证（协议兼容性自检）
/// </summary>
internal static class Program
{
    // ---------------- 模拟内存区（Snap7 直接读写这些数组，C# 侧修改数组即改变 PLC 数据） ----------------
    private static readonly byte[] Db1 = new byte[512];     // 数据块 DB1
    private static readonly byte[] Db2 = new byte[256];     // 数据块 DB2
    private static readonly byte[] Mk = new byte[512];      // 位存储区 M
    private static readonly byte[] Inputs = new byte[128];  // 输入区 I
    private static readonly byte[] Outputs = new byte[128]; // 输出区 Q

    private static Server? _server;
    private static volatile bool _running = true;
    private static float _simTime;

    // ---------------- 命令行参数 ----------------
    private static string _bindIp = "0.0.0.0";
    private static int _port = 102;
    private static int _intervalMs = 500;
    private static bool _selfTest;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ParseArgs(args);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _running = false; };

        SeedData();

        try
        {
            _server = new Server();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 创建 Snap7 Server 失败：{ex.Message}");
            return 1;
        }

        AttachEvents();

        // 注册内存区域：Snap7 会将客户端读写映射到这些数组上
        RegisterArea(S7.S7AreaDB, 1, Db1);   // DB1
        RegisterArea(S7.S7AreaDB, 2, Db2);   // DB2
        RegisterArea(S7.S7AreaMK, 0, Mk);    // M 区
        RegisterArea(S7.S7AreaPA, 0, Inputs);  // I 区（模拟外部信号）
        RegisterArea(S7.S7AreaPE, 0, Outputs); // Q 区（模拟输出）

        var rc = _server.StartTo(_bindIp, _port);
        if (rc != 0)
        {
            Console.WriteLine($"[错误] 监听 {_bindIp}:{_port} 失败：Snap7 返回 {rc}（{DescribeError(rc)}）");
            Console.WriteLine("       请检查端口是否被占用，可加 --port 指定其他端口。");
            _server.Destroy();
            return 1;
        }

        PrintBanner();

        using var timer = new System.Threading.Timer(_ => UpdateValues(), null, _intervalMs, _intervalMs);

        if (_selfTest)
        {
            var ok = SelfTest();
            Console.WriteLine(ok ? "\n[自测] 全部通过 ✔" : "\n[自测] 失败 ✘");
            _running = false;
        }
        else
        {
            while (_running)
                Thread.Sleep(200);
        }

        timer.Dispose();
        _server.Stop();
        _server.Destroy();
        Console.WriteLine("\n模拟器已停止。");
        return 0;
    }

    // ---------------- 区域注册 ----------------

    private static void RegisterArea(int area, int index, byte[] buffer)
    {
        var rc = _server!.RegisterArea(area, index, buffer, buffer.Length);
        if (rc != 0)
            Console.WriteLine($"[警告] 注册区域 area=0x{area:X2} idx={index} 失败：Snap7 返回 {rc}（{DescribeError(rc)}）");
    }

    private static void AttachEvents()
    {
        if (_server is null) return;
        _server.EventRead += OnRead;
        _server.EventWrite += OnWrite;
        _server.EventConnect += OnConnect;
        _server.EventDisconnect += OnDisconnect;
    }

    private static string AreaName(int area) => area switch
    {
        S7.S7AreaDB => "DB",
        S7.S7AreaMK => "M",
        S7.S7AreaPA => "I",
        S7.S7AreaPE => "Q",
        _ => $"0x{area:X2}",
    };

    private static void OnRead(IntPtr usrPtr, int sender, int area, int start, int size)
        => Console.WriteLine($"[读]   客户端#{sender}  {AreaName(area)} 字节偏移 {start}，长度 {size}");

    private static void OnWrite(IntPtr usrPtr, int sender, int area, int start, int size)
        => Console.WriteLine($"[写]   客户端#{sender}  {AreaName(area)} 字节偏移 {start}，长度 {size}");

    private static void OnConnect(IntPtr usrPtr, int sender, string clientIp, int clientPort)
        => Console.WriteLine($"[连接] 客户端#{sender}  {clientIp}:{clientPort}");

    private static void OnDisconnect(IntPtr usrPtr, int sender, string clientIp, int clientPort)
        => Console.WriteLine($"[断开] 客户端#{sender}  {clientIp}:{clientPort}");

    // ---------------- 初始数据 ----------------

    private static void SeedData()
    {
        // DB1 布局（大端，与博途 / S7Driver 一致）
        SetFloat(Db1, 0, 30f);         // DBD0  温度
        SetFloat(Db1, 4, 0.5f);        // DBD4  压力
        SetUShort(Db1, 8, 1000);       // DBW8  计数器
        SetUShort(Db1, 10, 1200);      // DBW10 转速
        Db1[12] = 0xFF;                // DBX12.0 运行标志
        SetS7String(Db1, 14, 32, "Running"); // DBS14 S7 STRING(32)

        Mk[0] = 0xFF;                  // M0.0 启动位
        SetUShort(Mk, 2, 0);           // MW2 秒表
        SetUShort(Inputs, 0, 512);     // IW0 模拟输入
    }

    // ---------------- 周期刷新模拟值 ----------------

    private static void UpdateValues()
    {
        _simTime += _intervalMs / 1000f;

        // 温度：约 22~38℃ 正弦波动
        SetFloat(Db1, 0, 30f + 8f * MathF.Sin(_simTime * 0.4f));
        // 压力：0.2~0.8 MPa 波动
        SetFloat(Db1, 4, 0.5f + 0.3f * MathF.Sin(_simTime * 0.7f + 1.3f));
        // 计数器：每秒 +1
        SetUShort(Db1, 8, (ushort)(1000 + (int)(_simTime * 2)));
        // 转速：1180~1240 波动
        SetUShort(Db1, 10, (ushort)(1200 + (int)(80 * MathF.Sin(_simTime * 0.9f))));
        // 运行标志：4 秒周期闪烁
        Db1[12] = (byte)(((int)_simTime / 4) % 2 == 0 ? 0xFF : 0x00);
        // S7 STRING 状态：Running / Standby 交替
        SetS7String(Db1, 14, 32, ((int)_simTime / 4) % 2 == 0 ? "Running" : "Standby");

        // M 区：M0.0 2 秒周期闪烁、MW2 系统秒表
        Mk[0] = (byte)(((int)_simTime / 2) % 2 == 0 ? 0xFF : 0x00);
        SetUShort(Mk, 2, (ushort)(Environment.TickCount / 1000 % 60000));

        // 输入区：IW0 波动（模拟外部信号）
        SetUShort(Inputs, 0, (ushort)(512 + 200 * MathF.Sin(_simTime * 1.2f)));
    }

    // ---------------- 自测（用 S7netplus，与上位机 S7 驱动同库） ----------------

    private static bool SelfTest()
    {
        Console.WriteLine("\n========== 自测：用 S7netplus 连接本机验证协议兼容性 ==========");
        try
        {
            using var plc = new S7.Net.Plc(S7.Net.CpuType.S7300, "127.0.0.1", _port, 0, 1);
            plc.Open();
            Console.WriteLine("[1] 已连接：CpuType=S7300, Rack=0, Slot=1");

            var temp = (float)plc.Read(S7.Net.DataType.DataBlock, 1, 0, S7.Net.VarType.Real);
            Console.WriteLine($"[2] 读 DB1.DBD0  温度   = {temp:F2} ℃");

            var counter = (ushort)plc.Read(S7.Net.DataType.DataBlock, 1, 8, S7.Net.VarType.Word);
            Console.WriteLine($"[3] 读 DB1.DBW8  计数器 = {counter}");

            var running = (bool)plc.Read(S7.Net.DataType.DataBlock, 1, 12, S7.Net.VarType.Bit);
            Console.WriteLine($"[4] 读 DB1.DBX12.0 运行 = {running}");

            var clock = (ushort)plc.Read(S7.Net.DataType.Memory, 0, 2, S7.Net.VarType.Word);
            Console.WriteLine($"[5] 读 MW2       秒表   = {clock} s");

            plc.WriteBit(S7.Net.DataType.Memory, 0, 0, 0, true);
            var bitBack = (bool)plc.Read(S7.Net.DataType.Memory, 0, 0, S7.Net.VarType.Bit);
            Console.WriteLine($"[6] 写 M0.0=true 回读   = {bitBack}");
            plc.WriteBit(S7.Net.DataType.Memory, 0, 0, 0, false);

            var written = new byte[] { 0x12, 0x34, 0x56 };
            plc.WriteBytes(S7.Net.DataType.DataBlock, 2, 0, written);
            var back = plc.ReadBytes(S7.Net.DataType.DataBlock, 2, 0, 3);
            Console.WriteLine($"[7] 写 DB2.DBB0={BitConverter.ToString(written)} 回读 {BitConverter.ToString(back)}");

            var strRaw = plc.ReadBytes(S7.Net.DataType.DataBlock, 1, 14, 16);
            var text = Encoding.UTF8.GetString(strRaw, 2, strRaw[1]).TrimEnd('\0');
            Console.WriteLine($"[8] 读 DB1.DBS14 状态   = \"{text}\"（max={strRaw[0]}, cur={strRaw[1]}）");

            plc.Close();
            Console.WriteLine("========== 自测结束 ==========");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[自测异常] {ex.Message}");
            return false;
        }
    }

    // ---------------- 命令行与提示 ----------------

    private static void ParseArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ip": _bindIp = Next(args, ref i); break;
                case "--port": _port = int.Parse(Next(args, ref i)); break;
                case "--interval": _intervalMs = int.Parse(Next(args, ref i)); break;
                case "--selftest": _selfTest = true; break;
                case "-h" or "--help": PrintUsage(); Environment.Exit(0); break;
                default:
                    Console.WriteLine($"未知参数：{args[i]}");
                    PrintUsage();
                    Environment.Exit(1);
                    break;
            }
        }
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            Console.WriteLine($"参数 {args[i]} 缺少值");
            PrintUsage();
            Environment.Exit(1);
        }
        return args[++i];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            用法：
              S7Simulator                     默认监听 0.0.0.0:102，每 500ms 刷新模拟值
              S7Simulator --port 10200        指定端口
              S7Simulator --ip 127.0.0.1      仅监听本机回环
              S7Simulator --interval 1000     刷新间隔（毫秒）
              S7Simulator --selftest          启动后用 S7netplus 自连验证
            """);
    }

    private static void PrintBanner()
    {
        Console.WriteLine($"""
            ============================================================
              S7 服务端模拟器（基于 Snap7 Server · 模拟 S7-300/400）
              监听   : {_bindIp}:{_port}
              区域   : DB1(512B) / DB2(256B) / M(512B) / I(128B) / Q(128B)
              刷新   : 每 {_intervalMs} ms 更新一次模拟值
            ------------------------------------------------------------
              上位机连接参数（本仓库 S7 驱动）:
                Host    = 127.0.0.1
                Port    = {_port}
                CpuType = S7300
                Rack    = 0
                Slot    = 1
            ------------------------------------------------------------
              示例 Tag（类型选“自动”即可按地址后缀识别）:
                DB1.DBD0    温度    Float    22~38℃ 正弦波动
                DB1.DBD4    压力    Float    0.2~0.8 MPa 波动
                DB1.DBW8    计数器  UInt16   每秒 +1
                DB1.DBW10   转速    UInt16   1180~1240 波动
                DB1.DBX12.0 运行标志 Bool     4 秒周期闪烁
                DB1.DBS14   状态    String   S7 STRING(32)，Running/Standby 交替
                MW2         秒表    UInt16   系统运行秒数
                M0.0        启动位  Bool     2 秒周期闪烁（可写）
                IW0         模拟输入 UInt16  波动
                Q0.0        输出    Bool     可写
              按 Ctrl+C 停止；S7Simulator --selftest 可自检
            ============================================================
            """);
    }

    private static string DescribeError(int rc) => rc switch
    {
        0 => "成功",
        1 => "监听失败（端口被占用或地址无效）",
        2 => "区域注册失败（参数非法）",
        _ => $"未知错误码 {rc}",
    };

    // ---------------- 大端读写辅助（S7 字节序） ----------------

    private static void SetUShort(byte[] buf, int pos, ushort v)
    {
        buf[pos] = (byte)(v >> 8);
        buf[pos + 1] = (byte)v;
    }

    private static void SetUInt(byte[] buf, int pos, uint v)
    {
        buf[pos] = (byte)(v >> 24);
        buf[pos + 1] = (byte)(v >> 16);
        buf[pos + 2] = (byte)(v >> 8);
        buf[pos + 3] = (byte)v;
    }

    private static void SetFloat(byte[] buf, int pos, float v) => SetUInt(buf, pos, BitConverter.SingleToUInt32Bits(v));

    /// <summary>写入 S7 STRING 布局：字节0=最大长度，字节1=当前长度，其后为 UTF-8 字符数据。</summary>
    private static void SetS7String(byte[] buf, int pos, int maxLen, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > maxLen) bytes = bytes[..maxLen];
        buf[pos] = (byte)maxLen;
        buf[pos + 1] = (byte)bytes.Length;
        Array.Clear(buf, pos + 2, maxLen);
        Array.Copy(bytes, 0, buf, pos + 2, bytes.Length);
    }
}
