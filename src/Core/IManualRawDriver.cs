using System.Threading;
using System.Threading.Tasks;

namespace IndustrialProtocolAssistant.Core;

/// <summary>手动原始帧收发结果。</summary>
public sealed record ManualRawReply(byte[] SentFrame, byte[] ReceivedBytes)
{
    /// <summary>等待窗口内没有任何应答。</summary>
    public bool NoResponse => ReceivedBytes.Length == 0;
}

/// <summary>手动收发面板展示的驱动能力描述。</summary>
public sealed record ManualRawCapabilities(
    string DriverType,
    string ManualHint,
    string? AutoFrameLabel,
    bool AutoFrameDefault);

/// <summary>
/// 支持“任意 Hex 请求 → 原始应答”的线帧驱动（帧协议：SerialFree / Socket / ModbusRTU / ModbusTCP）。
///
/// 高层语义协议驱动（S7 / OPC UA / MQTT 等）不实现本接口，手动收发面板会提示“不支持，
/// 请使用 Tag 读写”；对它们而言不存在可直接手写的“线帧”。
///
/// 线程模型：驱动内部必须保证同一时刻只有一笔收发在线（实现内自行串行）；
/// 本方案里由采集引擎在设备级提供 IO 闸门，轮询读 / 写值 / 单点读 / 手动收发全部互斥，
/// 不会出现应答错配。
/// </summary>
public interface IManualRawDriver
{
    /// <summary>帧格式 / 自动补全语义说明（面板顶部展示）。</summary>
    string ManualHint { get; }

    /// <summary>“自动补全”开关文案；为 null 表示驱动没有可自动补的全字段（按原样发送）。</summary>
    string? AutoFrameLabel { get; }

    /// <summary>面板上“自动补全”开关的默认状态。</summary>
    bool AutoFrameDefault { get; }

    /// <summary>
    /// 发送一帧原始请求并读取完整应答。
    /// <paramref name="applyAutoFrame"/> 为 true 时按驱动语义自动补头/尾（MBAP / CRC16 / 设备级帧校验），
    /// 否则严格按用户输入原样上线。
    /// 返回 <see cref="ManualRawReply.SentFrame"/>（真实上线帧）与应答；未连接或超时返回空应答。
    /// 传输/连接异常由实现方标记断开并抛出。
    /// </summary>
    Task<ManualRawReply> SendRawFrameAsync(byte[] request, bool applyAutoFrame, CancellationToken ct = default);
}
