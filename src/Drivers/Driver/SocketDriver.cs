using System.Net.Sockets;
using IndustrialProtocolAssistant.Core;

namespace IndustrialProtocolAssistant.Drivers.Driver;

/// <summary>
/// TCP Socket 自由协议主站（Socket）：
/// 以自定义帧"一问一答"的方式访问以太网设备（测试服务器/网关/PLC 网口板卡等），
/// 地址语法与 SerialFree 完全一致（读帧hex[@偏移] || 写帧hex，支持 {value} {crc16} {xor} {sum}）；
/// 帧尾校验也可在连接参数「帧校验」下拉统一选择（CRC16/XOR/SUM），自动追加到该设备全部 Tag 的读/写帧。
/// 仅传输层由串口换成 TCP/IP 客户端——保持长连接 + 单线程串行收发，断线后由采集引擎自动重连。
/// 回复边界采用"短静默"判定（不做按定长拆包的复杂解析），适合周期发送固定帧、按偏移截取数据的调试场景。
/// </summary>
public sealed class SocketDriver : IDeviceDriver, ITrafficAwareDriver, IManualRawDriver
{
    /// <summary>收到数据后静默超过该时长即认为整帧结束（TCP 下一问一答无需像串口那样可配置帧间隔）。</summary>
    private const int FrameIdleMs = 30;

    private readonly DeviceConfig _config;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private volatile bool _connected;

    public Action<string>? LogAction { get; set; }
    public Action<TrafficFrame>? TrafficSink { get; set; }

    public string DriverType => "Socket";

    public bool IsConnected => _connected;

    public SocketDriver(DeviceConfig config)
    {
        if (config.DriverType != DriverType)
            throw new ArgumentException($"驱动类型不匹配：期望 {DriverType}，实际 {config.DriverType}");
        _config = config;
    }

    /// <summary>地址语法校验（与 SerialFree 完全一致），供 UI 在新增 Tag 时即时提示。</summary>
    public static bool TryValidateAddress(string address, out string? error) =>
        SerialFreeProtocol.TryParse(address, out _, out error);

    // ---------- 连接参数（键名与 DriverFactory.GetFields 声明一致） ----------

    private string Host => _config.Get("Host");

    private int Port => _config.GetInt("Port", 502);

    private int ResponseTimeoutMs => Math.Max(50, _config.GetInt("ResponseTimeoutMs", 1000));

    /// <summary>设备级帧尾校验方式（无/CRC16/XOR/SUM），自动追加到该设备全部 Tag 的读/写帧末尾。</summary>
    private string FrameChecksum => _config.Get("FrameChecksum", "无");

    // ---------- 连接 / 断开 ----------

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CloseInternal();

            string host = Host;
            if (string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("Socket 自由协议需要配置服务器 IP 地址（如 127.0.0.1）");

            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(host, Port, ct).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            var stream = client.GetStream();
            // 同步收发时的底层保护超时（业务层另有 ResponseTimeoutMs 轮询判定，两者不会互相干扰）
            stream.ReadTimeout = ResponseTimeoutMs;
            stream.WriteTimeout = 2000;

            _client = client;
            _stream = stream;
            _connected = true;
            LogAction?.Invoke($"已连接 {host}:{Port}（TCP 自由协议，一问一答）");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _ioLock.Wait(ct);
        try
        {
            CloseInternal();
        }
        finally
        {
            _ioLock.Release();
        }
        return Task.CompletedTask;
    }

    /// <summary>释放连接并置为断开（需已持有 _ioLock）。读/写失败时调用，触发轮询层重连。</summary>
    private void CloseInternal()
    {
        try { _stream?.Dispose(); } catch { /* 忽略二次释放异常 */ }
        try { _client?.Dispose(); } catch { /* 忽略二次释放异常 */ }
        _stream = null;
        _client = null;
        _connected = false;
    }

    // ---------- 轮询读取 ----------

    public async Task<TagValue> ReadTagAsync(TagDefinition tag, CancellationToken ct = default)
    {
        if (!_connected || _client is null || _stream is null) return TagValue.Bad(tag.Id);

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_connected || _client is null || _stream is null) return TagValue.Bad(tag.Id);

            if (!SerialFreeProtocol.TryParse(tag.Address, FrameChecksum, out var spec, out string? error))
            {
                LogAction?.Invoke($"Tag「{tag.Name}」地址无效：{error}");
                return TagValue.Bad(tag.Id);
            }

            // 纯写 Tag（读段为空）或读段含 {value}：只能写，不参与轮询读取
            if (spec!.IsWriteOnly || spec.ReadContainsValuePlaceholder)
                return TagValue.Stale(tag.Id);

            byte[] frame = SerialFreeProtocol.RenderFrame(spec.ReadTokens, null, tag.DataType, tag.Length);
            byte[] reply = Exchange(frame, $"{tag.Name} ({tag.Address})", ct);
            if (reply.Length == 0)
                return TagValue.Bad(tag.Id); // 服务器无响应：标记坏值，等待下一轮重试（长连接由引擎重连）

            return SerialFreeProtocol.ParseReply(tag, reply, spec.Offset);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            CloseInternal();
            LogAction?.Invoke($"Socket 读取失败（{ex.Message}），已断开并准备重连");
            return TagValue.Bad(tag.Id);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    // ---------- 写入 ----------

    public async Task<bool> WriteTagAsync(TagDefinition tag, object value, CancellationToken ct = default)
    {
        if (!_connected || _client is null || _stream is null) return false;

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_connected || _client is null || _stream is null) return false;

            if (!SerialFreeProtocol.TryParse(tag.Address, FrameChecksum, out var spec, out string? error))
            {
                LogAction?.Invoke($"Tag「{tag.Name}」地址无效：{error}");
                return false;
            }

            byte[] valueBytes = SerialFreeProtocol.EncodeValue(tag, value); // 值格式错误抛异常，由 UI 提示

            byte[] frame;
            if (spec!.HasWriteSection)
            {
                // 显式写帧：{value} 占位符被替换，校验占位符自动计算
                frame = SerialFreeProtocol.RenderFrame(spec.WriteTokens, valueBytes, tag.DataType, tag.Length);
            }
            else
            {
                // 无写段：读段含 {value} 则替换；否则把编码值追加到读帧末尾
                byte[] readFrame = SerialFreeProtocol.RenderFrame(spec.ReadTokens, valueBytes, tag.DataType, tag.Length);
                frame = spec.ReadContainsValuePlaceholder
                    ? readFrame
                    : readFrame.Concat(valueBytes).ToArray();
            }

            byte[] reply = Exchange(frame, $"{tag.Name} ({tag.Address})", ct);
            if (reply.Length == 0)
                LogAction?.Invoke($"Tag「{tag.Name}」写 {ToHex(frame)} 已发出，服务器无应答");
            return true; // 发出即认为写入成功；应答内容由用户协议自行定义，不做强制校验
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            CloseInternal();
            LogAction?.Invoke($"Socket 写入失败（{ex.Message}），已断开并准备重连");
            return false;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    // ---------- 手动原始收发（调试面板）：任意 Hex 请求 → 原始应答 ----------

    /// <summary>请求 = TCP 自由协议线帧的任意 Hex（空格/逗号分隔或连续串）。</summary>
    public string ManualHint =>
        "请求帧为服务器自定义线帧的任意 Hex。勾选“自动追加帧校验”会按连接参数（CRC16/XOR/SUM）在帧尾自动补校验；"
        + "连接参数未配置校验时按原样发送。应答按“短静默”收整帧，超时视为无应答。";

    public string? AutoFrameLabel => FrameChecksum == "无" ? null : $"自动追加 {FrameChecksum} 帧校验";

    public bool AutoFrameDefault => FrameChecksum != "无";

    public async Task<ManualRawReply> SendRawFrameAsync(byte[] request, bool applyAutoFrame, CancellationToken ct = default)
    {
        byte[] frame = applyAutoFrame
            ? request.Concat(SerialFreeProtocol.ComputeDeviceTail(request, FrameChecksum)).ToArray()
            : request;

        if (!_connected || _client is null || _stream is null) return new ManualRawReply(frame, Array.Empty<byte>());

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_connected || _client is null || _stream is null) return new ManualRawReply(frame, Array.Empty<byte>());

            byte[] reply = Exchange(frame, "手动请求", ct);
            return new ManualRawReply(frame, reply);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            CloseInternal();
            LogAction?.Invoke($"手动收发失败（{ex.Message}），已断开并准备重连");
            throw;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    // ---------- 底层收发：发一帧 → 按"短静默"收完整应答 ----------

    /// <summary>
    /// 发送请求帧并读取服务器完整应答（需已持有 _ioLock）。
    /// 自由协议帧长不定，按"收到数据后静默超过 FrameIdleMs 视为一帧结束"判定边界；
    /// 从首字节起累计等待最多 ResponseTimeoutMs，全程无数据则视为无响应（返回空）。
    /// 对端断开（读到 0 字节）也会立即结束，交由轮询层重连。
    /// </summary>
    private byte[] Exchange(byte[] frame, string target, CancellationToken ct)
    {
        var stream = _stream!;

        // 载荷捕获：发出的完整请求帧（TCP 线帧，含用户配置的帧头/校验）
        EmitTraffic(TrafficDirection.Tx, $"请求 {target}（{frame.Length}B）", frame);

        stream.Write(frame, 0, frame.Length);

        var resp = new List<byte>(frame.Length + 64);
        var buf = new byte[1024];
        int timeoutMs = ResponseTimeoutMs;
        long start = Environment.TickCount64;
        long last = start;

        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (ct.IsCancellationRequested) break;

            if (stream.DataAvailable)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break; // 对端主动关闭 → 帧结束
                for (int i = 0; i < n; i++) resp.Add(buf[i]);
                last = Environment.TickCount64;
                continue;
            }

            if (resp.Count > 0 && Environment.TickCount64 - last >= FrameIdleMs)
                break; // 已收数据且静默超过一帧间隔 → 整帧结束

            Thread.Sleep(1);
        }
        if (resp.Count > 0)
            EmitTraffic(TrafficDirection.Rx, $"应答 {target}（{resp.Count}B）", resp.ToArray());
        return resp.ToArray();
    }

    /// <summary>上报一条线帧报文（TX=请求帧，RX=应答帧）。</summary>
    private void EmitTraffic(TrafficDirection dir, string description, byte[] payload)
    {
        var sink = TrafficSink;
        if (sink is null || payload.Length == 0) return;
        sink(new TrafficFrame
        {
            Timestamp = DateTimeOffset.UtcNow,
            DeviceId = _config.Id,
            DriverType = DriverType,
            Direction = dir,
            Description = description,
            Payload = payload,
            Text = ToReadableText(payload),
        });
    }

    private static string ToReadableText(byte[] data)
    {
        if (data.Length == 0) return "";
        var text = System.Text.Encoding.ASCII.GetString(data);
        return text.Any(c => c < 0x20 && c != '\t' && c != '\n' && c != '\r') ? "" : text;
    }

    private static string ToHex(byte[] bytes) =>
        string.Join(' ', bytes.Select(b => b.ToString("X2")));

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _ioLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
