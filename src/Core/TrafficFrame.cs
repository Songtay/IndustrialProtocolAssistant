namespace IndustrialProtocolAssistant.Core;

/// <summary>报文方向：TX=主站发出（读请求/写/发布），RX=主站收到（响应/订阅推送）。</summary>
public enum TrafficDirection { Tx, Rx }

/// <summary>
/// 一次载荷/报文交换记录（报文监控 Tab 的原始数据）。
///
/// 各驱动在能力范围内上报尽可能真实的字节：
/// - S7：PLC 存储区读/写的原始字节（载荷级，非线上 TCP 帧）；
/// - MQTT：发布/订阅的消息 payload；
/// 后续驱动（Modbus/SerialFree）可上报线上真实帧，字段无需变化。
/// </summary>
public sealed record TrafficFrame
{
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>设备 Id（便于将来多设备区分，现为 dev1）。</summary>
    public required string DeviceId { get; init; }

    /// <summary>驱动类型，如 S7 / Mqtt。</summary>
    public required string DriverType { get; init; }

    public required TrafficDirection Direction { get; init; }

    /// <summary>人类可读的操作说明，如 "读 DB1.DBD0 (4B)" / "PUB sensor/temp"。</summary>
    public required string Description { get; init; }

    /// <summary>载荷字节；允许为空（如仅握手/心跳）。</summary>
    public byte[] Payload { get; init; } = [];

    /// <summary>附带文本（UTF-8 解码后的可读内容），无则留空。</summary>
    public string Text { get; init; } = "";

    public int Length => Payload.Length;
}
