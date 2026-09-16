namespace IndustrialProtocolAssistant.Core;

/// <summary>
/// 设备协议驱动抽象。
/// 新增协议（Modbus/OPC UA/S7）只需新增实现，UI 与采集引擎不感知差异。
/// </summary>
public interface IDeviceDriver : IAsyncDisposable
{
    string DriverType { get; }
    bool IsConnected { get; }

    /// <summary>驱动诊断日志回调，由采集引擎统一注入。</summary>
    Action<string>? LogAction { get; set; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>读取一个 Tag 的当前值。</summary>
    Task<TagValue> ReadTagAsync(TagDefinition tag, CancellationToken ct = default);

    /// <summary>写入一个 Tag 值（下发）。</summary>
    Task<bool> WriteTagAsync(TagDefinition tag, object value, CancellationToken ct = default);
}
