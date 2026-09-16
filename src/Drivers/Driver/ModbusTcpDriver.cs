using System.Net.Sockets;
using IndustrialProtocolAssistant.Core;

namespace IndustrialProtocolAssistant.Drivers.Driver;

/// <summary>
/// Modbus TCP 协议驱动：只负责建立 TCP 传输，读写解析继承自 ModbusDriverBase。
/// 同时实现手动原始收发（调试面板）：请求按"从站号 + PDU"（如 01 03 00 00 00 01）输入，
/// 可自动补 6 字节 MBAP 请求头（事务号自增、长度自动计算）；应答按 MBAP 长度字段收帧。
/// </summary>
public sealed class ModbusTcpDriver : ModbusDriverBase, IManualRawDriver
{
    private ushort _transactionId;

    public override string DriverType => "ModbusTcp";

    public ModbusTcpDriver(DeviceConfig config) : base(config) { }

    protected override async Task<NModbus.IModbusMaster> CreateMasterAsync(CancellationToken ct)
    {
        var host = _config.Get("Host");
        var port = _config.GetInt("Port", 502);
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("未配置 IP 地址（TCP 驱动需要 Host 参数）");
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _transport = client;
        return new NModbus.ModbusFactory().CreateMaster(client);
    }

    // ---------- 手动原始收发（调试面板）：任意 Hex 请求 → 原始应答 ----------

    public string ManualHint =>
        "请求 = 从站号 + PDU（如 01 03 00 00 00 01，不含 MBAP 头）。勾选“自动补 MBAP 请求头”会自动补 6 字节 MBAP"
        + "（事务号自增、长度自动计算；若粘贴的已是完整 MBAP 帧则按原样发送）。应答按 MBAP 长度字段收帧，超时视为无应答。";

    public string? AutoFrameLabel => "自动补 MBAP 请求头（事务号自增）";

    public bool AutoFrameDefault => true;

    public async Task<ManualRawReply> SendRawFrameAsync(byte[] request, bool applyAutoFrame, CancellationToken ct = default)
    {
        byte[] frame = applyAutoFrame && !LooksLikeFullMbap(request) ? WrapMbap(request) : request;

        if (!_connected || _transport is not TcpClient client)
            return new ManualRawReply(frame, Array.Empty<byte>());

        NetworkStream stream;
        try
        {
            stream = client.GetStream();
        }
        catch
        {
            return new ManualRawReply(frame, Array.Empty<byte>());
        }

        try
        {
            DrainBuffered(stream);

            EmitTraffic(TrafficDirection.Tx, $"手动请求（{frame.Length}B，MBAP）", frame);
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);

            byte[] reply = await ReadReplyAsync(stream, ct).ConfigureAwait(false);
            if (reply.Length > 0)
                EmitTraffic(TrafficDirection.Rx, $"手动应答（{reply.Length}B）", reply);
            return new ManualRawReply(frame, reply);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            MarkDisconnected();
            LogAction?.Invoke($"手动收发失败（{ex.Message}），已断开并准备重连");
            throw;
        }
    }

    /// <summary>判断是否已是完整 MBAP 请求帧（前 6 字节头：协议=0，长度=余下字节数）。</summary>
    private static bool LooksLikeFullMbap(byte[] b) =>
        b.Length >= 8
        && b[2] == 0 && b[3] == 0
        && ((b[4] << 8) | b[5]) == b.Length - 6;

    /// <summary>给"从站号 + PDU"补 6 字节 MBAP（事务号自增、协议 0、长度=后续字节数）。</summary>
    private byte[] WrapMbap(byte[] pdu)
    {
        ushort id = _transactionId++;
        int len = pdu.Length; // 从站号 + PDU 全部位于长度字段之后
        var frame = new byte[pdu.Length + 6];
        frame[0] = (byte)(id >> 8);
        frame[1] = (byte)id;
        frame[2] = 0;
        frame[3] = 0;
        frame[4] = (byte)(len >> 8);
        frame[5] = (byte)len;
        Buffer.BlockCopy(pdu, 0, frame, 6, pdu.Length);
        return frame;
    }

    /// <summary>发请求前先清空可能残留的上一个应答字节，避免把脏数据当成本帧应答。</summary>
    private static void DrainBuffered(NetworkStream stream)
    {
        var tmp = new byte[4096];
        for (int i = 0; i < 32 && stream.DataAvailable; i++)
            _ = stream.Read(tmp, 0, tmp.Length);
    }

    /// <summary>读取完整 MBAP 应答：先收 6 字节头解析长度，再收满 6+长度 字节；收到非标准应答时按"静默 30ms"收尾。</summary>
    private static async Task<byte[]> ReadReplyAsync(NetworkStream stream, CancellationToken ct)
    {
        var resp = new List<byte>(64);
        var buf = new byte[1024];
        long deadline = Environment.TickCount64 + 2000;
        long last = Environment.TickCount64;
        int? expected = null;

        while (Environment.TickCount64 < deadline)
        {
            if (ct.IsCancellationRequested) break;

            if (stream.DataAvailable)
            {
                int n = await stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) break; // 对端关闭
                for (int i = 0; i < n; i++) resp.Add(buf[i]);
                last = Environment.TickCount64;

                // 至少 6 字节时尝试按 MBAP 长度收帧
                if (expected is null && resp.Count >= 6)
                {
                    int len = (resp[4] << 8) | resp[5];
                    if (resp[2] == 0 && resp[3] == 0 && len >= 1 && len <= 260)
                        expected = 6 + len;
                }
                if (expected is not null && resp.Count >= expected.Value)
                    break;
                continue;
            }

            if (resp.Count > 0 && Environment.TickCount64 - last >= 30)
                break; // 非标准应答：收到数据后静默视为帧结束

            await Task.Delay(1, ct).ConfigureAwait(false);
        }

        // 收尾：尽可能清掉残余字节，避免污染下一个 NModbus 事务
        try { DrainBuffered(stream); } catch { /* 忽略 */ }
        return resp.ToArray();
    }
}
