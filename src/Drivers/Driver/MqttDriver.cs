using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IndustrialProtocolAssistant.Core;
using MQTTnet;
using MQTTnet.Client;

namespace IndustrialProtocolAssistant.Drivers.Driver;

/// <summary>
/// MQTT 驱动：发布/订阅模式，与 Modbus 的请求/响应完全不同。
/// - Tag.Address 即订阅 Topic（支持 + / # 通配符），ConnectAsync 时完成订阅；
/// - 收到消息后按 Tag 数据类型解析并缓存，ReadTagAsync 直接返回缓存（MQTT 无主动读取语义）；
/// - WriteTagAsync 的发布目标优先级：Tag.WriteAddress（独立发送 Topic，如 devices/01/ctrl）→
///   未配置时回退「订阅 Topic + 写后缀」（默认 /set，可配置），避免订阅自身发布的回路；
/// - payload 兼容两种格式：裸值（如 "12.5" / "1"）或 JSON（{"value": 12.5}），写入统一为裸值。
/// </summary>
public sealed class MqttDriver : IDeviceDriver, ITagAwareDriver, ITrafficAwareDriver
{
    private readonly DeviceConfig _config;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly object _gate = new();
    private IMqttClient? _client;
    private List<TagDefinition> _tags = new();
    private volatile bool _connected;

    public string DriverType => "Mqtt";
    public bool IsConnected => _connected;
    public Action<string>? LogAction { get; set; }
    public Action<TrafficFrame>? TrafficSink { get; set; }

    public MqttDriver(DeviceConfig config)
    {
        if (config.DriverType != DriverType)
            throw new ArgumentException($"驱动类型不匹配：期望 {DriverType}，实际 {config.DriverType}");
        _config = config;
    }

    /// <summary>采集引擎设置/更新 Tag 列表。若已连接（运行中添加/删除 Tag），立即重新订阅使新 Topic 生效。</summary>
    public void SetTags(IEnumerable<TagDefinition> tags)
    {
        _tags = tags.ToList();
        var client = _client;
        if (_connected && client is not null)
        {
            _ = Task.Run(async () =>
            {
                try { await SubscribeAllAsync(client, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { LogAction?.Invoke($"[MQTT] 重新订阅失败: {ex.Message}"); }
            });
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var broker = _config.Get("BrokerUrl", "mqtt://127.0.0.1:1883");
        if (!broker.Contains("://")) broker = "mqtt://" + broker; // 兼容不带协议前缀的输入
        var uri = new Uri(broker);
        var useTls = string.Equals(uri.Scheme, "mqtts", StringComparison.OrdinalIgnoreCase);
        var port = uri.IsDefaultPort ? useTls ? 8883 : 1883 : uri.Port;

        var options = new MqttClientOptionsBuilder().WithTcpServer(uri.Host, port);
        var clientId = _config.Get("ClientId");
        if (!string.IsNullOrWhiteSpace(clientId)) options.WithClientId(clientId);
        if (useTls) options.WithTlsOptions(o => o.UseTls());
        var username = _config.Get("Username");
        if (!string.IsNullOrWhiteSpace(username)) options.WithCredentials(username, _config.Get("Password"));

        var client = new MqttFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += OnMessageReceived;
        try
        {
            await client.ConnectAsync(options.Build(), ct).ConfigureAwait(false);
            LogAction?.Invoke($"[MQTT] 已连接到 {broker}");
        }
        catch (Exception ex)
        {
            client.Dispose();
            _connected = false;
            LogAction?.Invoke($"[MQTT] 连接失败: {ex.Message}");
            throw;
        }
        lock (_gate)
        {
            _client = client;
            _connected = true;
        }
        await SubscribeAllAsync(client, ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;
        IMqttClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
        }
        if (client is not null)
        {
            client.ApplicationMessageReceivedAsync -= OnMessageReceived;
            try { await client.DisconnectAsync(new MqttClientDisconnectOptions(), ct).ConfigureAwait(false); } catch { }
            client.Dispose();
        }
    }

    public Task<TagValue> ReadTagAsync(TagDefinition tag, CancellationToken ct = default)
    {
        // 事件驱动模型：读取即取最新缓存；连接已断开返回 Bad（触发引擎重连）
        if (!_connected || _client is null) return Task.FromResult(TagValue.Bad(tag.Id));
        if (_cache.TryGetValue(tag.Id, out var entry))
            return Task.FromResult(new TagValue(tag.Id, entry.Value, DataQuality.Good, entry.Timestamp));
        // 已连接但尚未收到该 Topic 的报文 → 无数据
        return Task.FromResult(TagValue.Stale(tag.Id));
    }

    public async Task<bool> WriteTagAsync(TagDefinition tag, object value, CancellationToken ct = default)
    {
        var client = _client;
        if (!_connected || client is null) return false;

        // 发布目标：Tag 单独配置的发送 Topic 优先（用户可把写值发到与订阅完全独立的通道，如 devices/01/ctrl）；
        // 未配置时回退「订阅 Topic + 写后缀」，避免订阅自身发布的回路。
        var custom = string.IsNullOrWhiteSpace(tag.WriteAddress) ? null : tag.WriteAddress.Trim();
        var topic = custom ?? tag.Address.Trim();
        if (string.IsNullOrEmpty(topic)) return false;
        if (topic.Contains('+') || topic.Contains('#')) return false; // 通配符 Topic 不能作为发布目标

        if (custom is null)
        {
            var suffix = _config.Get("SetTopicSuffix", "/set");
            if (!string.IsNullOrEmpty(suffix)) topic += suffix;
        }

        try
        {
            var text = FormatPayload(tag.DataType, value);
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(text)
                .WithQualityOfServiceLevel(ToQos(_config.GetInt("QoS", 0)))
                .Build();
            await client.PublishAsync(message, ct).ConfigureAwait(false);
            // 载荷捕获：发布出去的 payload（MQTT 消息体）
            EmitTraffic(TrafficDirection.Tx, $"PUB {topic}（Tag={tag.Name}）", Encoding.UTF8.GetBytes(text), text);
            return true;
        }
        catch
        {
            _connected = false; // 发布失败视为断线，让引擎感知并重连
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // ---------- 内部实现 ----------

    private async Task SubscribeAllAsync(IMqttClient client, CancellationToken ct)
    {
        var topics = _tags
            .Select(t => t.Address.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (topics.Count == 0)
        {
            LogAction?.Invoke("[MQTT] 警告：没有需要订阅的 Topic（Tag 列表为空）");
            return;
        }

        var qos = ToQos(_config.GetInt("QoS", 0));
        var builder = new MqttClientSubscribeOptionsBuilder();
        foreach (var topic in topics) builder.WithTopicFilter(topic, qos);
        await client.SubscribeAsync(builder.Build(), ct).ConfigureAwait(false);
        LogAction?.Invoke($"[MQTT] 已订阅 {topics.Count} 个 Topic: {string.Join(", ", topics)} (QoS={qos})");
    }

    /// <summary>收到报文：按 Topic 匹配所有 Tag（支持通配符），解析成功后更新缓存。</summary>
    private Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var seg = e.ApplicationMessage.PayloadSegment;
        // 原始 payload 字节（MQTT 消息体，可能非 UTF-8/含二进制）
        var raw = seg.Count == 0 ? Array.Empty<byte>() : seg.AsSpan().ToArray();
        var payload = raw.Length == 0 ? string.Empty : Encoding.UTF8.GetString(raw);
        var ts = DateTimeOffset.UtcNow;

        // 载荷捕获：订阅端收到的消息（无论是否匹配 Tag、payload 是否为空，报文监控都应展示）
        EmitTraffic(TrafficDirection.Rx, $"SUB {topic}", raw, payload);

        var matched = false;
        foreach (var tag in _tags)
        {
            if (!TopicMatches(tag.Address.Trim(), topic)) continue;
            matched = true;
            if (TryParsePayload(payload, tag.DataType, out var parsed))
            {
                _cache[tag.Id] = new CacheEntry(parsed, ts);
                LogAction?.Invoke($"[MQTT] 收到 {topic} = {parsed} (Tag={tag.Name})");
            }
            else
            {
                LogAction?.Invoke($"[MQTT] 收到 {topic} 但解析失败: {payload} (Tag={tag.Name}, 类型={tag.DataType})");
            }
        }
        if (!matched)
            LogAction?.Invoke($"[MQTT] 收到 {topic} 但没有匹配的 Tag (payload={payload})");
        return Task.CompletedTask;
    }

    /// <summary>上报一条报文（TX=发布，RX=订阅收到）；Text 为 UTF-8 解码文本，便于 UI 直接显示。</summary>
    private void EmitTraffic(TrafficDirection dir, string description, byte[] payload, string text)
    {
        var sink = TrafficSink;
        if (sink is null) return;
        sink(new TrafficFrame
        {
            Timestamp = DateTimeOffset.UtcNow,
            DeviceId = _config.Id,
            DriverType = DriverType,
            Direction = dir,
            Description = description,
            Payload = payload,
            Text = string.IsNullOrEmpty(text) ? "" : text,
        });
    }

    /// <summary>MQTT Topic 通配符匹配：# 匹配剩余所有层级，+ 匹配单个层级（均只出现在订阅端）。</summary>
    internal static bool TopicMatches(string pattern, string topic)
    {
        var p = pattern.Split('/');
        var t = topic.Split('/');
        for (int i = 0; i < p.Length; i++)
        {
            if (p[i] == "#") return true;
            if (i >= t.Length) return false;
            if (p[i] != "+" && !string.Equals(p[i], t[i], StringComparison.Ordinal)) return false;
        }
        return p.Length == t.Length;
    }

    /// <summary>payload 解析：优先 JSON（{"value": ...}），失败后按裸字符串自动推断（bool / 数值 / 字符串），无需 Tag 类型。</summary>
    internal static bool TryParsePayload(string payload, TagDataType type, out object? value)
    {
        if (!string.IsNullOrWhiteSpace(payload))
        {
            // 第一次：标准 JSON
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("value", out var v))
                {
                    value = ConvertJsonValue(v, type);
                    return value is not null;
                }
            }
            catch (JsonException) { }

            // 第二次：兼容 Python 字典字符串（str() 输出用大写 True/False/None，不是标准 JSON）
            try
            {
                var normalized = Regex.Replace(payload, @"(?<=:\s*)(True|False|None)(?=\s*[,}])", m => m.Value switch
                {
                    "True" => "true",
                    "False" => "false",
                    "None" => "null",
                    _ => m.Value
                }, RegexOptions.Compiled);
                using var doc = JsonDocument.Parse(normalized);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("value", out var v))
                {
                    value = ConvertJsonValue(v, type);
                    return value is not null;
                }
            }
            catch (JsonException) { }
        }

        var text = payload.Trim();
        // 裸值自动推断（MQTT 无类型配置）：布尔文本 → bool；数字 → 数值；其他 → 字符串。
        // 注意：type 参数已不再影响裸值解析，仅保留签名兼容。
        switch (text.ToUpperInvariant())
        {
            case "1" or "TRUE" or "ON" or "YES":
                value = true;
                return true;
            case "0" or "FALSE" or "OFF" or "NO":
                value = false;
                return true;
        }
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            value = l;
            return true;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            value = d;
            return true;
        }
        value = payload; // 其余一律按字符串
        return true;
    }

    /// <summary>
    /// JSON payload 的 value 字段是自描述的：按实际 JSON 类型自动推断（bool / 数值 / 字符串），
    /// 不依赖 Tag 配置类型——MQTT 场景用户无需手动配置数据类型。
    /// 注意：裸值 payload（非 JSON）仍按 Tag 配置类型解析，见 TryParsePayload 的 fallback 分支。
    /// </summary>
    private static object? ConvertJsonValue(JsonElement v, TagDataType _)
    {
        if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            return v.ValueKind == JsonValueKind.True;
        if (v.ValueKind == JsonValueKind.Number)
        {
            // 优先整数避免 57 → 57.0 的显示差异，失败再回退浮点
            if (v.TryGetInt64(out var l)) return l;
            if (v.TryGetDouble(out var d)) return d;
            return null;
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString() ?? "";
            // 布尔字符串 → bool；纯数字字符串 → 数值；其余按字符串
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return s;
        }
        if (v.ValueKind == JsonValueKind.Null) return null;
        return v.GetRawText();
    }

    /// <summary>写入 payload 统一为裸值：Bool 用 1/0（与 Modbus 语义一致），数值用不变文化格式，String 原样。</summary>
    private static string FormatPayload(TagDataType type, object value) => type switch
    {
        TagDataType.Bool => ToBool(value) ? "1" : "0",
        TagDataType.String => value?.ToString() ?? string.Empty,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        string s when s.Trim() is "1" or "true" or "True" or "yes" or "Yes" or "ON" or "on" => true,
        string s when s.Trim() is "0" or "false" or "False" or "no" or "No" or "OFF" or "off" => false,
        _ => false,
    };

    private static MQTTnet.Protocol.MqttQualityOfServiceLevel ToQos(int qos) => qos switch
    {
        1 => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce,
        2 => MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce,
        _ => MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce,
    };

    private sealed record CacheEntry(object? Value, DateTimeOffset Timestamp);
}
