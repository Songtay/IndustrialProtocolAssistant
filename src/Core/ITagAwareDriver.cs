namespace IndustrialProtocolAssistant.Core;

/// <summary>
/// 可选接口：事件驱动型驱动（如 MQTT）在连接前需要知道本设备的 Tag 列表，
/// 以便在 ConnectAsync 时完成订阅（订阅 Topic）等准备工作。
/// 采集引擎会在每次 StartAsync 的 ConnectAsync 之前调用 SetTags。
/// 轮询型驱动（Modbus）无需实现此接口。
/// </summary>
public interface ITagAwareDriver
{
    void SetTags(IEnumerable<TagDefinition> tags);
}
