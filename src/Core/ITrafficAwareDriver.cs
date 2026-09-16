namespace IndustrialProtocolAssistant.Core;

/// <summary>
/// 可选接口：具备"载荷级报文捕获"能力的驱动（如 S7 / MQTT）。
/// 采集引擎创建/重建驱动后注入 <see cref="TrafficSink"/>，
/// 驱动在底层收发点把能拿到的真实字节回调上报，由引擎转给 UI「报文监控」。
/// 轮询型驱动（Modbus）若后续要接入，只需实现本接口并在读写路径上报即可。
/// </summary>
public interface ITrafficAwareDriver
{
    /// <summary>报文上报回调，由采集引擎注入（线程安全，驱动任意线程直接调用）。</summary>
    Action<TrafficFrame>? TrafficSink { get; set; }
}
