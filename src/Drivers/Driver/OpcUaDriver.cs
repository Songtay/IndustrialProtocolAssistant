using System.Collections.Concurrent;
using System.Text;
using IndustrialProtocolAssistant.Core;
using Opc.Ua;
using Opc.Ua.Client;

namespace IndustrialProtocolAssistant.Drivers.Driver;

/// <summary>
/// OPC UA 客户端驱动（基于 OPC Foundation .NET Standard）。
/// - Tag.Address 即节点的 NodeId 字符串：ns=2;s=温度 / ns=2;i=1005 / i=2256（前缀可省略）；
/// - 默认采用订阅推送（Subscription + MonitoredItem）：服务器推送值写入内部缓存，
///   ReadTagAsync 命中缓存即返回（零网络往返），订阅未建立/未命中时才回退主动读取，
///   因此采集引擎的轮询语义与 IDeviceDriver 接口完全不变；设备参数 EnableSubscription=0 可关闭订阅退回纯轮询；
/// - 写入通过 Session.WriteAsync 下发，按 Tag 配置类型转换（UI 对 OPC UA 隐藏类型，默认 Float）；
/// - 断线由 KeepAlive 保活检测，IsConnected 置 false 后由采集引擎自动重连，重连成功后自动重建订阅。
/// 注意：UA 会话对象非线程安全，所有会话调用统一经 _gate 串行化。
/// </summary>
public sealed class OpcUaDriver : IDeviceDriver, ITagAwareDriver, ITrafficAwareDriver
{
    private readonly DeviceConfig _config;
    private readonly object _gate = new();
    private Session? _session;
    private volatile bool _connected;

    // ---- 订阅推送（方案 A：驱动内部消化，接口与采集引擎零改动） ----
    private readonly bool _enableSubscription;
    private readonly int _subscriptionInterval;                          // 订阅发布间隔 ms
    private readonly ConcurrentDictionary<string, TagValue> _cache = new(); // tagId → 服务器最新推送值
    private readonly List<TagDefinition> _tags = new();                  // 最近一次 SetTags 快照（重建订阅用）
    private Subscription? _subscription;                                 // 当前活动订阅

    public string DriverType => "OpcUa";
    public bool IsConnected => _connected;
    public Action<string>? LogAction { get; set; }
    public Action<TrafficFrame>? TrafficSink { get; set; }

    public OpcUaDriver(DeviceConfig config)
    {
        if (config.DriverType != DriverType)
            throw new ArgumentException($"驱动类型不匹配：期望 {DriverType}，实际 {config.DriverType}");
        _config = config;
        _enableSubscription = config.GetBool("EnableSubscription", true);
        _subscriptionInterval = Math.Max(100, config.GetInt("SubscriptionInterval", 200));
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // 清理上一次的会话（引擎重连时可能残留未释放的 session）
        Session? old;
        lock (_gate) old = _session;
        if (old is not null)
        {
            try { old.Dispose(); } catch { }
            lock (_gate) _session = null;
        }

        var endpointUrl = _config.Get("EndpointUrl", "opc.tcp://127.0.0.1:4840");
        var policy = _config.Get("SecurityPolicy", "None");
        var useSecurity = !string.Equals(policy, "None", StringComparison.OrdinalIgnoreCase);

        try
        {
            var appConfig = BuildApplicationConfiguration();
            await appConfig.Validate(ApplicationType.Client);   // 该 SDK 版本 Validate 返回 Task，需等待配置验证完成
            appConfig.CertificateValidator.CertificateValidation += (_, e) => e.Accept = true; // 演示环境接受未信任证书

            var endpointDescription = CoreClientUtils.SelectEndpoint(endpointUrl, useSecurity, 10000);
            var endpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(appConfig));

            IUserIdentity identity = new UserIdentity();
            var username = _config.Get("Username");
            if (!string.IsNullOrEmpty(username))
                identity = new UserIdentity(username, _config.Get("Password"));

            var session = await Session.Create(appConfig, endpoint, false, false, "IPA Client", 60000, identity, null, ct).ConfigureAwait(false);
            session.KeepAlive += OnKeepAlive;
            lock (_gate) _session = session;
            _connected = true;
            LogAction?.Invoke($"[OPC UA] 已连接到 {endpointUrl} (SecurityPolicy={policy})");
            RebuildSubscription();   // 连接/重连成功后按最近一次 Tag 列表重建订阅
        }
        catch (Exception ex)
        {
            _connected = false;
            LogAction?.Invoke($"[OPC UA] 连接失败: {ex.Message}");
            throw;
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;
        CleanupSubscription();   // 先拆订阅（含清缓存），再关闭会话
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is not null)
        {
            session.KeepAlive -= OnKeepAlive;
            try { session.Close(); } catch { }
            session.Dispose();
        }
        return Task.CompletedTask;
    }

    public Task<TagValue> ReadTagAsync(TagDefinition tag, CancellationToken ct = default)
    {
        var session = _session;
        if (!_connected || session is null)
        {
            LogAction?.Invoke($"[OPC UA] 读取 {tag.Name} 跳过：会话未连接");
            return Task.FromResult(TagValue.Bad(tag.Id));
        }

        // 订阅推送模式下优先返回服务器最新推送值（零网络往返；引擎轮询间隔可因此调小）
        if (_cache.TryGetValue(tag.Id, out var cached))
            return Task.FromResult(cached);

        var address = tag.Address.Trim();
        // 某些 UA 浏览器复制 NodeId 时会在前面加上 "NodeId " 前缀，需清理
        const string nodeIdPrefix = "NodeId ";
        if (address.StartsWith(nodeIdPrefix, StringComparison.OrdinalIgnoreCase))
            address = address[nodeIdPrefix.Length..];

        NodeId nodeId;
        try { nodeId = NodeId.Parse(address); }
        catch
        {
            LogAction?.Invoke($"[OPC UA] {tag.Name} 的地址不是合法 NodeId: {tag.Address}（格式如 ns=2;s=温度 或 i=2256）");
            return Task.FromResult(TagValue.Bad(tag.Id));
        }

        try
        {
           // LogAction?.Invoke($"[OPC UA] 开始读取 {tag.Name} ({tag.Address}) …");
            DataValue dv;
            lock (_gate) { dv = session.ReadValue(nodeId); }
            if (dv.StatusCode.Code != StatusCodes.Good)
            {
                // 关键诊断信息：BadNodeIdUnknown=节点不存在/命名空间号不对，BadNotReadable=节点不可读，
                // BadNoAccess/BadUserAccessDenied=无权限，BadTypeMismatch=类型不匹配
               // LogAction?.Invoke($"[OPC UA] 读取 {tag.Name} ({tag.Address}) 返回 {dv.StatusCode}");
                return Task.FromResult(TagValue.Bad(tag.Id));
            }

            // 载荷捕获：原始值（ByteString 保留原始字节，其余按 UTF-8 文本），读成功才上报
            EmitTraffic(TrafficDirection.Rx, $"读 {tag.Address}（{tag.Name}）", EncodeValuePayload(dv.Value));

            object? value;
            if (tag.DataType != TagDataType.Auto && dv.Value is not null)
            {
                // 用户显式指定了节点类型 → 把服务器返回值按该类型转换（如 ByteString 按数值显示）
                try { value = ConvertForWrite(tag.DataType, dv.Value); }
                catch { value = UnwrapValue(dv.Value); }   // 转换失败回退服务器原生值
            }
            else
            {
                value = UnwrapValue(dv.Value);             // Auto：服务器原生类型
            }
           // LogAction?.Invoke($"[OPC UA] 读取 {tag.Name} ({tag.Address}) 成功，值={value ?? "(null)"}");
            return Task.FromResult(new TagValue(tag.Id, value, DataQuality.Good, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"[OPC UA] 读取 {tag.Name} ({tag.Address}) 失败: {ex.Message}");
            return Task.FromResult(TagValue.Bad(tag.Id));
        }
    }

    public Task<bool> WriteTagAsync(TagDefinition tag, object value, CancellationToken ct = default)
    {
        var session = _session;
        if (!_connected || session is null) return Task.FromResult(false);

        var address = tag.Address.Trim();
        const string nodeIdPrefix = "NodeId ";
        if (address.StartsWith(nodeIdPrefix, StringComparison.OrdinalIgnoreCase))
            address = address[nodeIdPrefix.Length..];

        NodeId nodeId;
        try { nodeId = NodeId.Parse(address); }
        catch
        {
            LogAction?.Invoke($"[OPC UA] {tag.Name} 的地址不是合法 NodeId: {tag.Address}");
            return Task.FromResult(false);
        }

        try
        {
            object? converted;
            if (tag.DataType != TagDataType.Auto)
            {
                // 用户显式指定了节点类型 → 以用户指定为准
                converted = ConvertForWrite(tag.DataType, value);
            }
            else
            {
                // Auto：先读节点当前值 → 用服务器真实类型转换输入（OPC UA 对类型敏感，BadTypeMismatch 是写入失败最常见原因）
                DataValue current;
                lock (_gate) { current = session.ReadValue(nodeId); }

                if (StatusCode.IsGood(current.StatusCode) && current.Value is not null)
                {
                    converted = ConvertForNodeValue(current.Value, value);
                }
                else
                {
                    // 当前值不可用（如从未写入过）→ 读取 DataType 属性推断标准类型
                    var dataTypeId = ReadNodeDataType(session, nodeId);
                    var clrType = dataTypeId is null ? null : MapBuiltInType(dataTypeId);
                    converted = clrType is null
                        ? ConvertForWrite(tag.DataType, value)   // 兜底：Tag 配置类型
                        : ConvertByClrType(clrType, value);
                }
            }

            var writeValue = new WriteValue
            {
                NodeId = nodeId,
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(converted)),
            };

            StatusCodeCollection results;
            lock (_gate)
            {
                session.Write(new RequestHeader(), new WriteValueCollection { writeValue }, out results, out _);
            }

            if (results is { Count: > 0 } && StatusCode.IsGood(results[0]))
            {
                LogAction?.Invoke($"[OPC UA] 写入 {tag.Name} ({tag.Address}) = {converted}（类型 {converted?.GetType().Name}）成功");
                // 载荷捕获：写入的服务端值载荷（ByteString 保留原始字节，其余按 UTF-8 文本）
                EmitTraffic(TrafficDirection.Tx, $"写 {tag.Address}（{tag.Name}）", EncodeValuePayload(converted));
                return Task.FromResult(true);
            }

            var code = results is { Count: > 0 } ? results[0] : StatusCodes.BadUnexpectedError;
            LogAction?.Invoke($"[OPC UA] 写入 {tag.Name} ({tag.Address}) 失败: {code}");
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            LogAction?.Invoke($"[OPC UA] 写入 {tag.Name} 异常: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    // ---------- 节点浏览（供"节点树浏览"窗口使用） ----------

    /// <summary>浏览指定节点的直接子节点（HierarchicalReferences 正向引用）。
    /// parentNodeId 为空/空白时从根节点（ObjectsFolder）开始。失败时返回空列表并在 error 中给出原因。</summary>
    public IReadOnlyList<OpcUaNodeInfo> BrowseChildren(string? parentNodeId, out string error)
    {
        error = string.Empty;
        var session = _session;
        if (!_connected || session is null)
        {
            error = "会话未连接";
            return [];
        }

        NodeId parent;
        try
        {
            parent = string.IsNullOrWhiteSpace(parentNodeId)
                ? new NodeId(ObjectIds.ObjectsFolder)
                : NodeId.Parse(CleanNodeId(parentNodeId));
        }
        catch (Exception ex)
        {
            error = $"NodeId 解析失败：{ex.Message}";
            return [];
        }

        try
        {
            lock (_gate)
            {
                // 1) 浏览直接子节点（支持分页续取）
                var entries = new List<(NodeId NodeId, string Name, NodeClass Class, bool HasChildren)>();
                const NodeClass allClasses = NodeClass.Object | NodeClass.Variable | NodeClass.Method |
                                             NodeClass.ObjectType | NodeClass.VariableType | NodeClass.ReferenceType |
                                             NodeClass.DataType | NodeClass.View;
                var browse = new BrowseDescription
                {
                    NodeId = parent,
                    BrowseDirection = BrowseDirection.Forward,
                    ReferenceTypeId = new NodeId(ReferenceTypeIds.HierarchicalReferences),
                    IncludeSubtypes = true,
                    NodeClassMask = (uint)allClasses,
                    ResultMask = (uint)(BrowseResultMask.DisplayName | BrowseResultMask.NodeClass),
                };

                session.Browse(null, null, 0, new BrowseDescriptionCollection { browse }, out var pages, out _);
                foreach (var page in pages)
                {
                    CollectReferences(page.References, session.NamespaceUris, entries);
                    var cp = page.ContinuationPoint;
                    while (cp is not null && cp.Length > 0)
                    {
                        session.BrowseNext(null, false, new ByteStringCollection { cp }, out var next, out _);
                        if (next.Count == 0) break;
                        var nr = next[0];
                        cp = nr.ContinuationPoint;
                        CollectReferences(nr.References, session.NamespaceUris, entries);
                    }
                }

                // 2) 探测每个子节点是否还有子节点（每节点最多取 1 个引用；节点过多时跳过探测）
                if (entries.Count > 0 && entries.Count <= 200)
                {
                    var probes = new BrowseDescriptionCollection();
                    foreach (var (childId, _, _, _) in entries)
                        probes.Add(new BrowseDescription
                        {
                            NodeId = childId,
                            BrowseDirection = BrowseDirection.Forward,
                            ReferenceTypeId = new NodeId(ReferenceTypeIds.HierarchicalReferences),
                            IncludeSubtypes = true,
                            NodeClassMask = (uint)allClasses,
                            ResultMask = (uint)BrowseResultMask.NodeClass,
                        });
                    session.Browse(null, null, 1, probes, out var probeResults, out _);
                    for (var i = 0; i < probeResults.Count && i < entries.Count; i++)
                    {
                        var e = entries[i];
                        var hasChildren = StatusCode.IsGood(probeResults[i].StatusCode)
                            && probeResults[i].References is { Count: > 0 };
                        // Object 类型节点默认显示展开箭头（探测失败/空时兜底；
                        // 若实际无子节点，展开后 WPF 会自动隐藏箭头）
                        if (!hasChildren && e.Class == NodeClass.Object)
                            hasChildren = true;
                        entries[i] = (e.NodeId, e.Name, e.Class, hasChildren);
                    }
                }

                // 3) 批量读取 Variable 节点的 DataType 属性与当前值（一次往返）
                var reads = new ReadValueIdCollection();
                var readTargets = new List<int>();
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Class == NodeClass.Variable)
                    {
                        reads.Add(new ReadValueId { NodeId = entries[i].NodeId, AttributeId = Attributes.DataType });
                        reads.Add(new ReadValueId { NodeId = entries[i].NodeId, AttributeId = Attributes.Value });
                        readTargets.Add(i);
                    }
                }
                var typeNames = new Dictionary<NodeId, string>();
                var valueTexts = new Dictionary<NodeId, string>();
                if (reads.Count > 0)
                {
                    session.Read(new RequestHeader(), 0, TimestampsToReturn.Neither, reads, out var values, out _);
                    for (var i = 0; i < readTargets.Count; i++)
                    {
                        var dtIdx = i * 2;
                        var valIdx = dtIdx + 1;
                        if (dtIdx >= values.Count) break;
                        if (values[dtIdx].Value is NodeId dtId)
                            typeNames[entries[readTargets[i]].NodeId] = GetDataTypeName(dtId);
                        if (valIdx < values.Count && StatusCode.IsGood(values[valIdx].StatusCode) && values[valIdx].Value is not null)
                            valueTexts[entries[readTargets[i]].NodeId] = FormatValueForDisplay(values[valIdx].Value);
                    }
                }

                // 4) 组装结果（Object 类型节点默认显示展开箭头）
                var result = new List<OpcUaNodeInfo>(entries.Count);
                foreach (var (childId, name, cls, hasChildren) in entries)
                {
                    result.Add(new OpcUaNodeInfo
                    {
                        NodeId = childId.ToString(),
                        DisplayName = name,
                        NodeClass = cls.ToString(),
                        DataType = typeNames.TryGetValue(childId, out var tn) ? tn : "",
                        ValueText = valueTexts.TryGetValue(childId, out var vt) ? vt : "",
                        HasChildren = hasChildren || cls == NodeClass.Object,
                    });
                }
                return result;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return [];
        }
    }

    /// <summary>按 NodeId 直接检查单个节点（读取浏览名/节点类/数据类型/当前值），供快速定位。</summary>
    public OpcUaNodeInfo? ReadNodeInfo(string nodeIdText, out string error)
    {
        error = string.Empty;
        var session = _session;
        if (!_connected || session is null)
        {
            error = "会话未连接";
            return null;
        }

        NodeId nodeId;
        try { nodeId = NodeId.Parse(CleanNodeId(nodeIdText)); }
        catch (Exception ex)
        {
            error = $"NodeId 解析失败：{ex.Message}（格式如 ns=2;s=温度 或 i=2256）";
            return null;
        }

        try
        {
            lock (_gate)
            {
                var reads = new ReadValueIdCollection
                {
                    new() { NodeId = nodeId, AttributeId = Attributes.BrowseName },
                    new() { NodeId = nodeId, AttributeId = Attributes.NodeClass },
                    new() { NodeId = nodeId, AttributeId = Attributes.DataType },
                    new() { NodeId = nodeId, AttributeId = Attributes.Value },
                };
                session.Read(new RequestHeader(), 0, TimestampsToReturn.Neither, reads, out var values, out _);
                if (values.Count == 0)
                {
                    error = "服务器无响应";
                    return null;
                }
                if (!StatusCode.IsGood(values[0].StatusCode))
                {
                    error = $"节点不存在或不可访问：{values[0].StatusCode}";
                    return null;
                }

                var name = values[0].Value is QualifiedName qn && !string.IsNullOrEmpty(qn.Name)
                    ? qn.Name
                    : nodeId.ToString();
                var nodeClass = values[1].Value is null
                    ? ""
                    : Enum.GetName(typeof(NodeClass), values[1].Value) ?? values[1].Value.ToString() ?? "";
                var dataType = values[2].Value is NodeId dt ? GetDataTypeName(dt) : "";
                var valueText = values[3].Value is not null ? FormatValueForDisplay(values[3].Value) : "";
                return new OpcUaNodeInfo
                {
                    NodeId = nodeId.ToString(),
                    DisplayName = name,
                    NodeClass = nodeClass,
                    DataType = dataType,
                    ValueText = valueText,
                    HasChildren = false,
                };
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>把引用描述收进列表（同时过滤无法解析到本地命名空间的引用）。</summary>
    private static void CollectReferences(
        ReferenceDescriptionCollection? references,
        NamespaceTable namespaceUris,
        List<(NodeId NodeId, string Name, NodeClass Class, bool HasChildren)> sink)
    {
        if (references is null) return;
        foreach (var r in references)
        {
            var childId = ExpandedNodeId.ToNodeId(r.NodeId, namespaceUris);
            if (childId is null) continue;
            sink.Add((childId, string.IsNullOrEmpty(r.DisplayName.Text) ? childId.ToString() : r.DisplayName.Text, r.NodeClass, false));
        }
    }

    /// <summary>把内置数据类型 NodeId 映射为友好名称，非内置类型显示 NodeId 本身。</summary>
    private static string GetDataTypeName(NodeId dataTypeId)
    {
        if (dataTypeId.NamespaceIndex != 0 || dataTypeId.Identifier is not uint id) return dataTypeId.ToString();
        return id switch
        {
            1 => "Boolean", 2 => "SByte", 3 => "Byte", 4 => "Int16", 5 => "UInt16",
            6 => "Int32", 7 => "UInt32", 8 => "Int64", 9 => "UInt64", 10 => "Float",
            11 => "Double", 12 => "String", 13 => "DateTime", 14 => "Guid", 15 => "ByteString",
            16 => "XmlElement", 17 => "NodeId", 18 => "ExpandedNodeId", 19 => "StatusCode",
            20 => "QualifiedName", 21 => "LocalizedText", 22 => "Structure", 23 => "DataValue",
            24 => "BaseDataType", 25 => "DiagnosticInfo", 26 => "Number", 27 => "Integer",
            28 => "UInteger", 29 => "Enumeration", 30 => "Image", 31 => "Decimal",
            _ => dataTypeId.ToString(),
        };
    }

    /// <summary>值转显示文本（复用读取路径的解码逻辑，并截断超长内容）。</summary>
    private static string FormatValueForDisplay(object? value)
    {
        if (value is null) return "";
        var text = UnwrapValue(value)?.ToString() ?? "";
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    /// <summary>清理 UA 浏览器复制的 NodeId（去掉常见 "NodeId " 前缀）。</summary>
    private static string CleanNodeId(string address)
    {
        var s = address.Trim();
        const string prefix = "NodeId ";
        return s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? s[prefix.Length..] : s;
    }

    /// <summary>根据服务器节点当前值（SDK 已还原为真实 CLR 类型）转换写入输入。</summary>
    private static object? ConvertForNodeValue(object currentValue, object input) => currentValue switch
    {
        bool => ToBool(input),
        sbyte => Convert.ToSByte(input),
        byte => Convert.ToByte(input),
        short => Convert.ToInt16(input),
        ushort => Convert.ToUInt16(input),
        int => Convert.ToInt32(input),
        uint => Convert.ToUInt32(input),
        long => Convert.ToInt64(input),
        ulong => Convert.ToUInt64(input),
        float => Convert.ToSingle(input),
        double => Convert.ToDouble(input),
        DateTime => DateTime.TryParse(input?.ToString(), out var dt) ? dt : DateTime.MinValue,
        Guid => Guid.TryParse(input?.ToString(), out var g) ? g : Guid.Empty,
        byte[] => input?.ToString() is { } s ? Encoding.UTF8.GetBytes(s) : Array.Empty<byte>(),
        _ => input,
    };

    /// <summary>读取节点的 DataType 属性（返回 DataType 节点自身的 NodeId）。</summary>
    private static NodeId? ReadNodeDataType(ISession session, NodeId nodeId)
    {
        var readValueId = new ReadValueId { NodeId = nodeId, AttributeId = Attributes.DataType };
        session.Read(new RequestHeader(), 0, TimestampsToReturn.Neither,
            new ReadValueIdCollection { readValueId }, out var values, out _);
        return values is { Count: > 0 } && StatusCode.IsGood(values[0].StatusCode)
            ? values[0].Value as NodeId
            : null;
    }

    /// <summary>把标准内置类型的 DataType NodeId（ns=0;i=xx）映射到 CLR 类型。</summary>
    private static Type? MapBuiltInType(NodeId dataTypeId)
    {
        if (dataTypeId.NamespaceIndex != 0 || dataTypeId.Identifier is not uint id) return null;
        return id switch
        {
            1 => typeof(bool), 2 => typeof(sbyte), 3 => typeof(byte),
            4 => typeof(short), 5 => typeof(ushort), 6 => typeof(int), 7 => typeof(uint),
            8 => typeof(long), 9 => typeof(ulong), 10 => typeof(float), 11 => typeof(double),
            12 => typeof(string), 13 => typeof(DateTime), 14 => typeof(Guid), 15 => typeof(byte[]),
            _ => null,
        };
    }

    /// <summary>按 CLR 类型转换写入输入。</summary>
    private static object? ConvertByClrType(Type type, object value)
    {
        if (type == typeof(bool)) return ToBool(value);
        if (type == typeof(sbyte)) return Convert.ToSByte(value);
        if (type == typeof(byte)) return Convert.ToByte(value);
        if (type == typeof(short)) return Convert.ToInt16(value);
        if (type == typeof(ushort)) return Convert.ToUInt16(value);
        if (type == typeof(int)) return Convert.ToInt32(value);
        if (type == typeof(uint)) return Convert.ToUInt32(value);
        if (type == typeof(long)) return Convert.ToInt64(value);
        if (type == typeof(ulong)) return Convert.ToUInt64(value);
        if (type == typeof(float)) return Convert.ToSingle(value);
        if (type == typeof(double)) return Convert.ToDouble(value);
        if (type == typeof(DateTime)) return DateTime.TryParse(value?.ToString(), out var dt) ? dt : DateTime.MinValue;
        if (type == typeof(Guid)) return Guid.TryParse(value?.ToString(), out var g) ? g : Guid.Empty;
        if (type == typeof(byte[])) return value?.ToString() is { } s ? Encoding.UTF8.GetBytes(s) : Array.Empty<byte>();
        if (type == typeof(string)) return value?.ToString() ?? string.Empty;
        return value;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // ---------- 内部实现 ----------

    /// <summary>UA 会话保活失败视为断线（引擎轮询循环据此触发重连）。
    /// 注意：SDK 在首次保活回调时 Status 可能为 null，此时不能判为断线；
    /// 连接真正断开时 Status 必为 Bad。</summary>
    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (_connected && e.Status is not null && e.Status.Code != StatusCodes.Good)
        {
            _connected = false;
            LogAction?.Invoke($"[OPC UA] 会话保活失败: {e.Status}");
        }
    }

    /// <summary>
    /// 上报一条值载荷报文（TX=写入，RX=读/推送）。
    /// 载荷编码规则：ByteString 保留原始字节；其余按 UTF-8 文本编码（OPC UA 二进制线上帧不在此层展示）。
    /// </summary>
    private void EmitTraffic(TrafficDirection dir, string description, byte[] payload)
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
            Text = ToReadableText(payload),
        });
    }

    /// <summary>把服务器返回值编码为载荷字节（byte[] 原样保留，其余 UTF-8 文本）。</summary>
    private static byte[] EncodeValuePayload(object? value) => value switch
    {
        null => [],
        byte[] b => b,
        string s => Encoding.UTF8.GetBytes(s),
        Array a when a.Length == 0 => [],
        Array a => Encoding.UTF8.GetBytes(string.Join(", ", a.Cast<object?>())),
        _ => Encoding.UTF8.GetBytes(Convert.ToString(value) ?? ""),
    };

    /// <summary>载荷可读文本：UTF-8 解码成功且不含控制字符才返回（ByteString/数值载荷展示为空由 UI 按 Hex 显示）。</summary>
    private static string ToReadableText(byte[] data)
    {
        if (data.Length == 0) return "";
        try
        {
            var text = Encoding.UTF8.GetString(data);
            return text.Any(c => c < 0x20 && c != '\t' && c != '\n' && c != '\r') ? "" : text;
        }
        catch
        {
            return "";
        }
    }

    // ---------- 订阅推送（ITagAwareDriver） ----------

    /// <summary>采集引擎在注册设备/启动采集时通知 Tag 列表；已连接则同步重建订阅。</summary>
    public void SetTags(IEnumerable<TagDefinition> tags)
    {
        lock (_gate)
        {
            _tags.Clear();
            _tags.AddRange(tags);

            // 缓存里已不属于当前 Tag 列表的旧值清掉，避免残留
            var valid = new HashSet<string>(_tags.Select(t => t.Id));
            foreach (var key in _cache.Keys)
                if (!valid.Contains(key)) _cache.TryRemove(key, out _);
        }
        if (_connected) RebuildSubscription();
    }

    /// <summary>按最近一次 Tag 列表重建订阅（连接/重连成功、SetTags 时调用）。失败时回退轮询读取。</summary>
    private void RebuildSubscription()
    {
        lock (_gate)
        {
            CleanupSubscriptionCore();
            if (!_connected || !_enableSubscription) return;
            var session = _session;
            if (session is null) return;

            var tags = _tags.Where(t => !string.IsNullOrWhiteSpace(t.Address)).ToList();
            if (tags.Count == 0) return;

            try
            {
                var interval = _subscriptionInterval;
                var subscription = new Subscription   // 该 SDK 版本 Subscription 无 (ISession) 构造函数，经 AddSubscription 关联会话
                {
                    PublishingInterval = interval,
                    KeepAliveCount = 10,
                    LifetimeCount = 30,
                    DisplayName = "IPA-Tags",
                };
                session.AddSubscription(subscription);
                subscription.Create();

                var items = new List<MonitoredItem>(tags.Count);
                foreach (var tag in tags)
                {
                    NodeId nodeId;
                    try { nodeId = NodeId.Parse(CleanNodeId(tag.Address)); }
                    catch { continue; }   // 地址非法 → 跳过该节点（主动读取路径已有明确报错提示）
                    var item = new MonitoredItem(subscription.DefaultItem)
                    {
                        StartNodeId = nodeId,
                        AttributeId = Attributes.Value,
                        SamplingInterval = interval,
                        QueueSize = 1,
                        DiscardOldest = true,
                        Handle = tag,     // 回调里经 Handle 找回 TagDefinition
                    };
                    item.Notification += OnMonitoredItemNotification;
                    subscription.AddItem(item);
                    items.Add(item);
                }

                subscription.ApplyChanges();
                _subscription = subscription;

                var failed = items.Count(i => i.Status?.Error is { } err && !StatusCode.IsGood(err.StatusCode));
                LogAction?.Invoke($"[OPC UA] 订阅已建立：{items.Count} 个节点，发布间隔 {interval}ms"
                    + (failed > 0 ? $"，其中 {failed} 个节点订阅失败（将回退轮询读取）" : ""));
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"[OPC UA] 订阅建立失败，回退轮询读取: {ex.Message}");
                CleanupSubscriptionCore();
            }
        }
    }

    /// <summary>移除并释放当前订阅，清空推送缓存（线程安全入口）。</summary>
    private void CleanupSubscription()
    {
        lock (_gate) CleanupSubscriptionCore();
    }

    /// <summary>移除并释放当前订阅，清空推送缓存（调用方须已持有 _gate）。</summary>
    private void CleanupSubscriptionCore()
    {
        var sub = _subscription;
        _subscription = null;
        if (sub is not null)
        {
            try
            {
                var session = sub.Session;
                if (session is not null) session.RemoveSubscription(sub);
            }
            catch { }
            try { sub.Dispose(); } catch { }
        }
        _cache.Clear();
    }

    /// <summary>订阅推送回调：服务器值变化（含初始值）→ 转换后写入缓存，ReadTagAsync 直接命中。</summary>
    private void OnMonitoredItemNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        if (item.Handle is not TagDefinition tag) return;
        if (e.NotificationValue is not MonitoredItemNotification notification) return;
        var dv = notification.Value;
        if (dv is null) return;
        // 载荷捕获：订阅推送收到的值（驱动入站，视为 RX；仅当状态 Good 时上报）
        if (StatusCode.IsGood(dv.StatusCode))
            EmitTraffic(TrafficDirection.Rx, $"推送 {tag.Address}（{tag.Name}）", EncodeValuePayload(dv.Value));
        _cache[tag.Id] = BuildTagValue(tag, dv);
    }

    /// <summary>把服务器推送的 DataValue 转成统一 TagValue（转换逻辑与主动读取路径一致）。</summary>
    private static TagValue BuildTagValue(TagDefinition tag, DataValue dv)
    {
        var timestamp = dv.SourceTimestamp == DateTime.MinValue
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(dv.SourceTimestamp, TimeSpan.Zero);

        if (!StatusCode.IsGood(dv.StatusCode))
            return new TagValue(tag.Id, null, DataQuality.Bad, timestamp);

        object? value;
        if (tag.DataType != TagDataType.Auto && dv.Value is not null)
        {
            // 用户显式指定了节点类型 → 按该类型转换（与主动读取路径一致）
            try { value = ConvertForWrite(tag.DataType, dv.Value); }
            catch { value = UnwrapValue(dv.Value); }
        }
        else
        {
            value = UnwrapValue(dv.Value);   // Auto：服务器原生类型
        }
        return new TagValue(tag.Id, value, DataQuality.Good, timestamp);
    }

    /// <summary>把 UA 读取结果转成 UI 可直接显示的 CLR 值（数组转字符串，DateTime 格式化）。</summary>
    private static object? UnwrapValue(object? value)
    {
        if (value is null) return null;
        if (value is byte[] bytes)
        {
            // ByteString：优先按 UTF-8 文本解码（服务器常用它承载字符串/二进制协议帧）
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Any(c => c < 0x20 && c != '\t' && c != '\n' && c != '\r'))
            {
                // 含不可打印字符 → 显示十六进制，避免超长刷屏
                var hex = Convert.ToHexString(bytes);
                return bytes.Length > 64 ? hex[..64] + "…" : hex;
            }
            return text;
        }
        if (value is Array arr)
            return arr.Length == 0 ? null : string.Join(", ", arr.Cast<object?>());
        if (value is DateTime dt)
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        return value;
    }

    /// <summary>写入值按 Tag 配置类型转换（与 Modbus/MQTT 驱动语义一致）。</summary>
    private static object ConvertForWrite(TagDataType type, object value) => type switch
    {
        TagDataType.Bool => ToBool(value),
        TagDataType.Int16 => Convert.ToInt16(value),
        TagDataType.UInt16 => Convert.ToUInt16(value),
        TagDataType.Int32 => Convert.ToInt32(value),
        TagDataType.UInt32 => Convert.ToUInt32(value),
        TagDataType.Float => Convert.ToSingle(value),
        TagDataType.Double => Convert.ToDouble(value),
        TagDataType.String => value?.ToString() ?? string.Empty,
        _ => value ?? string.Empty,
    };

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        string s when s.Trim() is "1" or "true" or "True" or "yes" or "Yes" or "ON" or "on" => true,
        string s when s.Trim() is "0" or "false" or "False" or "no" or "No" or "OFF" or "off" => false,
        _ => false,
    };

    /// <summary>演示/工具场景的 UA 客户端配置：证书放程序目录 pki 文件夹，自动接受未信任证书。</summary>
    private static ApplicationConfiguration BuildApplicationConfiguration() => new()
    {
        ApplicationName = "IndustrialProtocolAssistant",
        ApplicationUri = Utils.Format("urn:{0}:IndustrialProtocolAssistant", Environment.MachineName),
        ApplicationType = ApplicationType.Client,
        SecurityConfiguration = new SecurityConfiguration
        {
            ApplicationCertificate = new CertificateIdentifier
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(AppContext.BaseDirectory, "OPC UA", "pki", "own"),
                SubjectName = "CN=IndustrialProtocolAssistant",
            },
            TrustedPeerCertificates = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(AppContext.BaseDirectory, "OPC UA", "pki", "trusted"),
            },
            TrustedIssuerCertificates = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(AppContext.BaseDirectory, "OPC UA", "pki", "issuer"),
            },
            RejectedCertificateStore = new CertificateTrustList
            {
                StoreType = CertificateStoreType.Directory,
                StorePath = Path.Combine(AppContext.BaseDirectory, "OPC UA", "pki", "rejected"),
            },
            AutoAcceptUntrustedCertificates = true,
        },
        TransportConfigurations = new TransportConfigurationCollection(),
        TransportQuotas = new TransportQuotas { OperationTimeout = 10000, MaxStringLength = 1024 * 1024 },
        ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
        TraceConfiguration = new TraceConfiguration(),
    };
}

/// <summary>OPC UA 节点树中的一个节点（浏览结果，与 UI 解耦的纯数据）。</summary>
public sealed class OpcUaNodeInfo
{
    public string NodeId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string NodeClass { get; init; } = "";
    public string DataType { get; init; } = "";
    public string ValueText { get; init; } = "";
    public bool HasChildren { get; init; }
}
