using System.Runtime.InteropServices;

namespace S7Simulator;

/// <summary>Snap7 Server 的最小 P/Invoke 封装（仅覆盖模拟器所需功能）。
/// snap7.dll 为 Snap7 官方开源的 S7comm 服务端原生库，文件位于项目 native 目录。</summary>
internal static class Snap7Native
{
    // ---------------- 区域码（S7comm） ----------------
    public const int S7AreaPE = 0x81; // Q 输出
    public const int S7AreaPA = 0x82; // I 输入
    public const int S7AreaMK = 0x83; // M 位存储
    public const int S7AreaDB = 0x84; // DB 数据块

    // ---------------- 事件码 ----------------
    public const int EvcDataRead = 0x00000001;
    public const int EvcDataWrite = 0x00000002;
    public const int EvcClientsConnected = 0x00000004;
    public const int EvcClientsDisconnected = 0x00000008;

    private const string Dll = "snap7.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int Srv_Create(ref IntPtr server);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void Srv_Destroy(ref IntPtr server);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int Srv_StartTo(IntPtr server, [MarshalAs(UnmanagedType.LPStr)] string ip, int port);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int Srv_Stop(IntPtr server);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int Srv_RegisterArea(IntPtr server, int areaCode, int index, byte[] pUsrData, int size);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int Srv_SetEventsCallback(IntPtr server, SrvCallBack? callback, IntPtr usrPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void SrvCallBack(IntPtr usrPtr, int sender, int dataSize, int data, IntPtr param);

    /// <summary>Snap7 事件结构（对应 C 端 SrvEvent，共 8 个 int32）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SrvEvent
    {
        public int EvtTime;
        public int EvtSender;
        public int EvtCode;
        public int EvtRetCode;
        public int EvtParam1;
        public int EvtParam2;
        public int EvtParam3;
        public int EvtParam4;
    }

    /// <summary>Snap7 Server 错误码说明（模拟器用到的子集）。</summary>
    public static string ErrorText(int err) => err switch
    {
        0 => "成功",
        1 => "无法启动（可能端口被占用）",
        2 => "无法停止",
        3 => "无法绑定端口",
        17 => "参数错误",
        18 => "参数编号错误",
        20 => "索引越界",
        21 => "区域未找到",
        22 => "区域未知",
        23 => "区域已存在",
        24 => "无法注册区域",
        _ => $"Snap7 错误码 {err}",
    };
}
