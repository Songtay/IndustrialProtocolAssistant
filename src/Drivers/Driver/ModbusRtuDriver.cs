using System.IO.Ports;
using IndustrialProtocolAssistant.Core;
using NModbus;

namespace IndustrialProtocolAssistant.Drivers.Driver;

/// <summary>
/// Modbus RTU 协议驱动：通过串口（RS-232/485）通信，读写解析继承自 ModbusDriverBase。
/// 默认帧格式：8 数据位 / 无校验 / 1 停止位。
/// 同时实现手动原始收发（调试面板）：请求按"从站号 + PDU"（如 01 03 00 00 00 01，无 CRC）输入，
/// 可自动补 CRC16(MODBUS)；应答按串口"静默间隔"收整帧。
/// </summary>
public sealed class ModbusRtuDriver : ModbusDriverBase, IManualRawDriver
{
    /// <summary>手动原始收发专用的串口引用（与 NModbus master 共用同一端口；连接重建时被替换）。</summary>
    private SerialPort? _rawPort;

    public override string DriverType => "ModbusRtu";

    public ModbusRtuDriver(DeviceConfig config) : base(config) { }

    protected override Task<IModbusMaster> CreateMasterAsync(CancellationToken ct)
    {
        var portName = _config.Get("SerialPort");
        var baud = _config.GetInt("BaudRate", 9600);
        if (string.IsNullOrWhiteSpace(portName))
            throw new InvalidOperationException("未配置串口名（RTU 驱动需要 SerialPort 参数）");

        var sp = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 2000,
            WriteTimeout = 2000,
        };
        try
        {
            sp.Open();
        }
        catch
        {
            sp.Dispose();
            throw;
        }
        _rawPort = sp;
        var adapter = new SerialPortStreamResource(sp);
        _transport = adapter;
        return Task.FromResult<IModbusMaster>(new ModbusFactory().CreateRtuMaster(adapter));
    }

    // ---------- 手动原始收发（调试面板）：任意 Hex 请求 → 原始应答 ----------

    public string ManualHint =>
        "请求 = 从站号 + PDU（如 01 03 00 00 00 01，不含 CRC）。勾选“自动补 CRC16”会在帧尾补 MODBUS CRC"
        + "（低字节在前；粘贴的已是完整 RTU 帧则按原样发送）。应答按串口静默间隔收整帧，超时视为无应答。";

    public string? AutoFrameLabel => "自动补 CRC16（MODBUS，低字节在前）";

    public bool AutoFrameDefault => true;

    public async Task<ManualRawReply> SendRawFrameAsync(byte[] request, bool applyAutoFrame, CancellationToken ct = default)
    {
        byte[] frame = applyAutoFrame ? EnsureRtuCrc(request) : request;

        var sp = _rawPort;
        if (!_connected || sp is null || !sp.IsOpen)
            return new ManualRawReply(frame, Array.Empty<byte>());

        try
        {
            try { sp.DiscardInBuffer(); sp.DiscardOutBuffer(); } catch { /* 忽略 */ }

            EmitTraffic(TrafficDirection.Tx, $"手动请求（{frame.Length}B，RTU）", frame);
            sp.Write(frame, 0, frame.Length);

            byte[] reply = await ReadReplyAsync(sp, ct).ConfigureAwait(false);
            if (reply.Length > 0)
                EmitTraffic(TrafficDirection.Rx, $"手动应答（{reply.Length}B）", reply);

            // 收尾：把读帧之后残存的字节清掉，避免污染下一个 NModbus 事务
            await Task.Delay(FrameGapMs(_config.GetInt("BaudRate", 9600)), ct).ConfigureAwait(false);
            try { sp.DiscardInBuffer(); } catch { /* 忽略 */ }

            return new ManualRawReply(frame, reply);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            MarkDisconnected();
            _rawPort = null;
            LogAction?.Invoke($"手动收发失败（{ex.Message}），已断开并准备重连");
            throw;
        }
    }

    /// <summary>RTU 静默帧间隔：约 4 个字符时间（按波特率），下限 8ms 上限 50ms。</summary>
    private static int FrameGapMs(int baud)
    {
        double charMs = 1000.0 * 11 / Math.Max(300, baud); // 1起始 + 8数据 + 1停止 + 余量
        int gap = (int)Math.Ceiling(charMs * 4);
        return Math.Clamp(gap, 8, 50);
    }

    /// <summary>读取完整 RTU 应答：从首字节起累计等待最多 2s，收到数据后静默超过帧间隔视为一帧结束。</summary>
    private async Task<byte[]> ReadReplyAsync(SerialPort sp, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var resp = new List<byte>(64);
            var buf = new byte[1024];
            int gapMs = FrameGapMs(_config.GetInt("BaudRate", 9600));
            long deadline = Environment.TickCount64 + 2000;
            long last = Environment.TickCount64;

            while (Environment.TickCount64 < deadline)
            {
                if (ct.IsCancellationRequested) break;

                int avail = sp.BytesToRead;
                if (avail > 0)
                {
                    int n = sp.Read(buf, 0, Math.Min(avail, buf.Length));
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++) resp.Add(buf[i]);
                    last = Environment.TickCount64;
                    continue;
                }

                if (resp.Count > 0 && Environment.TickCount64 - last >= gapMs)
                    break; // 已收数据且静默超过帧间隔 → 一帧结束

                Thread.Sleep(1);
            }
            return resp.ToArray();
        }, ct).ConfigureAwait(false);
    }
}
