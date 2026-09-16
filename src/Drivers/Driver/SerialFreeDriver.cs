using System.IO.Ports;
using IndustrialProtocolAssistant.Core;

namespace IndustrialProtocolAssistant.Drivers.Driver;

/// <summary>
/// 串口自由格式协议主站（SerialFree）：
/// 没有标准协议，由用户按"帧头/命令/校验"自定义一问一答的请求帧（地址语法见 SerialFreeProtocol），
/// 驱动负责串口收发，并按 Tag 类型从回复帧中取值——等价于一个自定义串口协议的主站/客户端。
/// 轮询型驱动：每个 Tag 对应一条读请求帧，采集中持续轮询；写值时使用 || 后的写帧（或 {value} 占位）。
/// </summary>
public sealed class SerialFreeDriver : IDeviceDriver, ITrafficAwareDriver, IManualRawDriver
{
    private readonly DeviceConfig _config;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private SerialPort? _serial;
    private volatile bool _connected;

    public Action<string>? LogAction { get; set; }
    public Action<TrafficFrame>? TrafficSink { get; set; }

    public string DriverType => "SerialFree";

    public bool IsConnected => _connected;

    public SerialFreeDriver(DeviceConfig config)
    {
        if (config.DriverType != DriverType)
            throw new ArgumentException($"驱动类型不匹配：期望 {DriverType}，实际 {config.DriverType}");
        _config = config;
    }

    /// <summary>地址语法校验，供 UI 在新增 Tag 时即时提示。</summary>
    public static bool TryValidateAddress(string address, out string? error) =>
        SerialFreeProtocol.TryParse(address, out _, out error);

    // ---------- 串口参数（缺省 9600,8N1；数值从 DriverFactory.GetFields 声明的键读取） ----------

    private string PortName => _config.Get("SerialPort");

    private int BaudRate => _config.GetInt("BaudRate", 9600);

    private int DataBits => _config.GetInt("DataBits", 8) is var d && d is 7 or 8 ? d : 8;

    private Parity Parity => _config.Get("Parity", "无") switch
    {
        "偶校验" => Parity.Even,
        "奇校验" => Parity.Odd,
        _ => Parity.None,
    };

    private StopBits StopBits => _config.Get("StopBits", "1") == "2" ? StopBits.Two : StopBits.One;

    private string ParityText => Parity switch
    {
        Parity.Even => "偶校验",
        Parity.Odd => "奇校验",
        _ => "无校验",
    };

    private int ResponseTimeoutMs => Math.Max(50, _config.GetInt("ResponseTimeoutMs", 1000));

    private int FrameGapMs => Math.Max(1, _config.GetInt("FrameGapMs", 20));

    /// <summary>设备级帧尾校验方式（无/CRC16/XOR/SUM），自动追加到该设备全部 Tag 的读/写帧末尾。</summary>
    private string FrameChecksum => _config.Get("FrameChecksum", "无");

    // ---------- 连接 / 断开 ----------

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _ioLock.Wait(ct);
        try
        {
            CloseInternal();

            string name = PortName;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("串口自由协议需要配置串口名（如 COM3）");

            var sp = new SerialPort(name, BaudRate, Parity, DataBits, StopBits)
            {
                // 读采用 BytesToRead 非阻塞轮询，ReadTimeout 仅防止异常状态下 Read 永久阻塞
                ReadTimeout = ResponseTimeoutMs,
                WriteTimeout = 2000,
            };
            sp.Open();
            _serial = sp;
            _connected = true;

            LogAction?.Invoke($"串口 {name} 已打开（{BaudRate} 波特，{DataBits}{ParityText[0]}{StopBits switch { StopBits.Two => '2', _ => '1' }}）");
        }
        finally
        {
            _ioLock.Release();
        }
        return Task.CompletedTask;
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

    /// <summary>释放串口并置为断开（需已持有 _ioLock）。读/写失败时调用，触发轮询层重连。</summary>
    private void CloseInternal()
    {
        try { _serial?.Dispose(); } catch { /* 忽略二次释放异常 */ }
        _serial = null;
        _connected = false;
    }

    // ---------- 轮询读取 ----------

    public async Task<TagValue> ReadTagAsync(TagDefinition tag, CancellationToken ct = default)
    {
        if (!_connected || _serial is null) return TagValue.Bad(tag.Id);

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_connected || _serial is null) return TagValue.Bad(tag.Id);

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
                return TagValue.Bad(tag.Id); // 从站无响应：标记坏值，但不断线（可能只是设备忙/离线，等待下一轮）

            return SerialFreeProtocol.ParseReply(tag, reply, spec.Offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CloseInternal();
            LogAction?.Invoke($"串口读取失败（{ex.Message}），已断开并准备重连");
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
        if (!_connected || _serial is null) return false;

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_connected || _serial is null) return false;

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
                LogAction?.Invoke($"Tag「{tag.Name}」写 {ToHex(frame)} 已发出，从站无应答");
            return true; // 发出即认为写入成功；应答内容由用户协议自行定义，不做强制校验
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CloseInternal();
            LogAction?.Invoke($"串口写入失败（{ex.Message}），已断开并准备重连");
            return false;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    // ---------- 手动原始收发（调试面板）：任意 Hex 请求 → 原始应答 ----------

    /// <summary>请求 = 设备自定义线帧的任意 Hex（空格/逗号分隔或连续串）。</summary>
    public string ManualHint =>
        "请求帧为设备自定义线帧的任意 Hex。勾选“自动追加帧校验”会按连接参数（CRC16/XOR/SUM）在帧尾自动补校验；"
        + "连接参数未配置校验时按原样发送。应答按“静默间隔（FrameGapMs）”自动收整帧，超时视为无应答。";

    public string? AutoFrameLabel => FrameChecksum == "无" ? null : $"自动追加 {FrameChecksum} 帧校验";

    public bool AutoFrameDefault => FrameChecksum != "无";

    public async Task<ManualRawReply> SendRawFrameAsync(byte[] request, bool applyAutoFrame, CancellationToken ct = default)
    {
        byte[] frame = applyAutoFrame
            ? request.Concat(SerialFreeProtocol.ComputeDeviceTail(request, FrameChecksum)).ToArray()
            : request;

        if (!_connected || _serial is null) return new ManualRawReply(frame, Array.Empty<byte>());

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_connected || _serial is null) return new ManualRawReply(frame, Array.Empty<byte>());

            byte[] reply = Exchange(frame, "手动请求", ct);
            return new ManualRawReply(frame, reply);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
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

    // ---------- 底层收发：发一帧 → 按"静默间隔"收完整帧 ----------

    /// <summary>
    /// 发送请求帧并读取从站完整应答（需已持有 _ioLock）。
    /// 自由协议帧长不定，按"收到数据后静默超过 FrameGapMs 视为一帧结束"判定边界；
    /// 从首字节起累计等待最多 ResponseTimeoutMs，全程无数据则视为无响应（返回空）。
    /// </summary>
    private byte[] Exchange(byte[] frame, string target, CancellationToken ct)
    {
        var sp = _serial!;
        try { sp.DiscardInBuffer(); } catch { /* 忽略 */ }

        // 载荷捕获：发出的完整请求帧（串口真实线帧，含用户配置的帧头/校验）
        EmitTraffic(TrafficDirection.Tx, $"请求 {target}（{frame.Length}B）", frame);

        sp.Write(frame, 0, frame.Length);

        var resp = new List<byte>(frame.Length + 32);
        int timeoutMs = ResponseTimeoutMs;
        int gapMs = FrameGapMs;
        long start = Environment.TickCount64;
        long last = start;

        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (ct.IsCancellationRequested) break;

            int avail = sp.BytesToRead;
            if (avail > 0)
            {
                var buf = new byte[avail];
                int n = sp.Read(buf, 0, avail);
                for (int i = 0; i < n; i++) resp.Add(buf[i]);
                last = Environment.TickCount64;
                continue;
            }

            if (resp.Count > 0 && Environment.TickCount64 - last >= gapMs)
                break; // 已收数据且静默超过帧间隔 → 一帧结束

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
