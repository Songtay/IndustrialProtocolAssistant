using System.Collections.Concurrent;
using System.Threading.Channels;
using IndustrialProtocolAssistant.Core;
using IndustrialProtocolAssistant.Drivers;
using IndustrialProtocolAssistant.Drivers.Driver;

namespace IndustrialProtocolAssistant.Acquisition;

/// <summary>
/// 采集调度引擎。
/// - 后台 Task 按设备轮询间隔读取各 Tag；
/// - 通过 Channel&lt;TagValue&gt; 输出采样流（解耦采集与存储/UI）；
/// - 内置阈值报警判断。
/// 面试要点：BackgroundService + Channel&lt;T&gt; 保证 UI 不卡顿、数据流可控背压。
/// </summary>
public sealed class AcquisitionEngine : IDisposable
{
    private readonly ConcurrentDictionary<string, (DeviceConfig Device, IDeviceDriver Driver, List<TagDefinition> Tags)> _devices = new();
    private readonly Channel<TagValue> _output = Channel.CreateUnbounded<TagValue>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Channel<AlarmEvent> _alarms = Channel.CreateUnbounded<AlarmEvent>();
    private readonly Channel<TrafficFrame> _traffic = Channel.CreateUnbounded<TrafficFrame>();
    private readonly object _lock = new();
    private CancellationTokenSource _cts = new();
    private bool _running;
    private readonly List<Task> _loops = new();
    private readonly ConcurrentDictionary<string, TagRuntimeState> _tagStates = new();
    // 设备级 IO 闸门：串口 / 自由协议 / Modbus 等"一问一答"协议必须整笔交换互斥，
    // 否则手动收发、单点读与轮询会互相读到对方的应答。TCP 事务型协议同样经由此闸门保持全局一致。
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ioGates = new();

    private SemaphoreSlim GetIoGate(string deviceId) => _ioGates.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// 值变化死区：相邻两轮采样值变化量小于该阈值时，该轮采样不落库、不刷新 UI。
    /// 数值型按变化量阈值过滤；Bool/String 无死区概念，按"值是否变化"过滤（恒定值不落库）。默认 0.5。
    /// </summary>
    public double ValueDeadband { get; set; } = 0.5;
    public Action<string>? LogAction { get; set; }

    private volatile bool _reading;

    /// <summary>
    /// 自动轮询读取开关：false 时引擎保持已连接但不采样（不读 Tag、不刷新 UI / 不落库 / 不判报警）。
    /// 由主界面“数据监控”页的“读取/停止”按钮控制；连接建立后默认关闭，避免“连接并启动”后立即自动读取。
    /// 仅影响轮询采样，写值 / 单点“读一次” / 手动原始帧收发不受影响，仍可正常使用。
    /// </summary>
    public bool ReadingEnabled { get => _reading; set => _reading = value; }

    public ChannelReader<TagValue> TagValueReader => _output.Reader;
    public ChannelReader<AlarmEvent> AlarmReader => _alarms.Reader;

    /// <summary>载荷报文流（驱动层上报，UI「报文监控」消费；最新记录由 UI 侧自行置顶）。</summary>
    public ChannelReader<TrafficFrame> TrafficReader => _traffic.Reader;

    /// <summary>连接状态变化通知：参数为 deviceId 和 是否已连接（false=断开，true=恢复）。</summary>
    public event Action<string, bool>? ConnectionChanged;

    /// <summary>
    /// 注册一个设备及其 Tag 列表。
    /// - 驱动类型不变 且 连接参数未变：仅更新 Tag 列表（不重建驱动），避免"应用配置"后添加 Tag 导致引擎内列表与实际不一致。
    /// - 驱动类型变化（如 Modbus→MQTT）或 连接参数变化（如改了端点 URL）：必须重建驱动实例，
    ///   否则会复用旧驱动实例继续按旧参数连接（旧驱动持有创建时的 DeviceConfig 快照）。
    /// </summary>
    public void RegisterDevice(DeviceConfig device, IEnumerable<TagDefinition> tags)
    {
        var tagList = tags.ToList();
        if (_devices.TryGetValue(device.Id, out var existing))
        {
            if (string.Equals(existing.Driver.DriverType, device.DriverType, StringComparison.Ordinal)
                && SameConnection(existing.Device, device))
            {
                _devices[device.Id] = (device, existing.Driver, tagList);
                if (existing.Driver is ITagAwareDriver tagAware) tagAware.SetTags(tagList);
            }
            else
            {
                LogAction?.Invoke($"设备 {device.Id} 连接参数变化（{existing.Driver.DriverType}），重建驱动实例");
                _ = existing.Driver.DisposeAsync();
                var driver = DriverFactory.Create(device);
                WireTrafficSink(driver);
                _devices[device.Id] = (device, driver, tagList);
            }
        }
        else
        {
            var driver = DriverFactory.Create(device);
            WireTrafficSink(driver);
            _devices[device.Id] = (device, driver, tagList);
        }
    }

    /// <summary>若驱动具备载荷捕获能力，把报文上报回调指向引擎的转发通道。</summary>
    private void WireTrafficSink(IDeviceDriver driver)
    {
        if (driver is ITrafficAwareDriver trafficAware)
            trafficAware.TrafficSink = frame => _traffic.Writer.TryWrite(frame);
    }

    /// <summary>连接参数是否完全一致：驱动类型（调用方已比较）+ 轮询间隔 + 参数字典内容。</summary>
    private static bool SameConnection(DeviceConfig a, DeviceConfig b)
    {
        if (a.PollIntervalMs != b.PollIntervalMs || a.Params.Count != b.Params.Count)
            return false;
        foreach (var (k, v) in a.Params)
            if (!b.Params.TryGetValue(k, out var bv) || bv != v)
                return false;
        return true;
    }

    /// <summary>
    /// 启动所有设备连接与采集循环。
    /// <paramref name="startReading"/> 为 false 时只建立/保持连接，轮询循环处于“待命”状态不采样，
    /// 直到调用方把 <see cref="ReadingEnabled"/> 置 true 才开始自动读取（避免启动即读取）。
    /// 每次调用使用全新的取消令牌，支持停止后重新启动。
    /// </summary>
    public async Task StartAsync(bool startReading = true)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_running) return; // 已在运行，防止重复点击重复启动
            _running = true;
            _reading = startReading;
            cts = new CancellationTokenSource();
            _cts = cts;
        }

        var failures = new List<string>();
        foreach (var kv in _devices)
        {
            var (device, driver, tags) = kv.Value;
            try
            {
                // 事件驱动型驱动（MQTT）需要先知道 Tag 列表才能完成订阅
                if (driver is ITagAwareDriver tagAware) tagAware.SetTags(tags);
                driver.LogAction = LogAction; // 所有驱动统一绑定日志
                await driver.ConnectAsync(cts.Token).ConfigureAwait(false);
                var loop = Task.Run(() => PollLoopAsync(device, driver, cts.Token));
                lock (_lock) _loops.Add(loop);
            }
            catch (Exception ex)
            {
                failures.Add($"{device.Name}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            lock (_lock) _running = false;
            await cts.CancelAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"设备连接失败：{string.Join("; ", failures)}");
        }
    }

    private async Task PollLoopAsync(DeviceConfig device, IDeviceDriver driver, CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(100, device.PollIntervalMs));
        // 轮询循环启动前 StartAsync 已成功连接，故初始视为已连接，否则首次断线无法触发通知
        var wasConnected = true;
        while (!ct.IsCancellationRequested)
        {
            if (!driver.IsConnected)
            {
                if (wasConnected)
                {
                    wasConnected = false;
                    ConnectionChanged?.Invoke(device.Id, false);
                }
                try { await driver.ConnectAsync(ct).ConfigureAwait(false); }
                catch { await Task.Delay(2000, ct).ConfigureAwait(false); continue; }
                wasConnected = true;
                ConnectionChanged?.Invoke(device.Id, true);
            }
            // 读取开关：关闭时保持连接（含断线自动重连）但不采样，等待用户点“读取”再开始轮询读
            if (!_reading)
            {
                try { await Task.Delay(interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            // 每次循环都从 _devices 取最新 Tag 列表，支持运行中添加/删除 Tag 后无需重启即可生效。
            // 修复：PollLoopAsync 原先固定捕获启动时的 tags，运行中 RegisterDevice 更新了 Tag 列表后
            // 轮询仍读旧列表，导致 MQTT 等事件驱动驱动虽已收到数据（缓存已更新）但 UI 不刷新。
            if (_devices.TryGetValue(device.Id, out var current) && ReferenceEquals(current.Driver, driver))
            {
              //  LogAction?.Invoke($"[Engine] 轮询 {device.Name}，Tag 数={current.Tags.Count}，驱动={driver.DriverType}");
                var gate = GetIoGate(device.Id);
                foreach (var tag in current.Tags)
                {
                    if (ct.IsCancellationRequested) break;
                    // 整笔"读 Tag"原子化：与 写值/单点读/手动收发 共享同一把 IO 闸门，互不串扰
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    TagValue tv;
                    try
                    {
                        tv = await driver.ReadTagAsync(tag, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                    // 变化死区过滤：变化过小的采样不落库、不刷新 UI
                    if (PassesDeadband(tag, tv)) _output.Writer.TryWrite(tv);
                    // 报警独立判断（状态机去抖，不依赖死区过滤）
                    EvaluateAlarm(tag, tv);
                }
            }
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Tag 运行时状态：变化死区上次值 + 报警状态（0=正常 1=高报 2=低报）。</summary>
    private sealed class TagRuntimeState
    {
        public bool HasLastValue;
        public double LastValue;
        public string? LastValueText;
        public byte AlarmState;
    }

    /// <summary>值变化检测：数值型相邻变化小于死区时返回 false（丢弃本轮采样）；Bool/String 值未变化时返回 false。坏值不过滤，保证 Bad 能透传。</summary>
    private bool PassesDeadband(TagDefinition tag, TagValue tv)
    {
        if (ValueDeadband <= 0) return true; // 死区关闭（0 或负数）：每轮都落库
        if (tv.Quality != DataQuality.Good || tv.Value is null) return true;

        var state = _tagStates.GetOrAdd(tag.Id, _ => new TagRuntimeState());

        // 数值型：相邻两轮变化量 >= 死区才放行
        if (tag.DataType != TagDataType.Bool && double.TryParse(tv.Value.ToString(), out var d))
        {
            lock (state)
            {
                if (!state.HasLastValue)
                {
                    state.HasLastValue = true;
                    state.LastValue = d;
                    return true;
                }
                if (Math.Abs(d - state.LastValue) < ValueDeadband) return false;
                state.LastValue = d;
                return true;
            }
        }

        // Bool / String：值不同才放行（恒定值不落库）
        var text = tv.Value.ToString();
        lock (state)
        {
            if (!state.HasLastValue)
            {
                state.HasLastValue = true;
                state.LastValueText = text;
                return true;
            }
            if (string.Equals(state.LastValueText, text, StringComparison.Ordinal)) return false;
            state.LastValueText = text;
            return true;
        }
    }

    private void EvaluateAlarm(TagDefinition tag, TagValue tv)
    {
        if (tv.Quality != DataQuality.Good || tv.Value is null) return;
        if (!double.TryParse(tv.Value.ToString(), out var d)) return;

        byte newState = 0;
        if (tag.HighAlarm.HasValue && d >= tag.HighAlarm.Value) newState = 1;
        else if (tag.LowAlarm.HasValue && d <= tag.LowAlarm.Value) newState = 2;

        var state = _tagStates.GetOrAdd(tag.Id, _ => new TagRuntimeState());
        lock (state)
        {
            // 去抖：同一 Tag 仅在报警状态发生转换时产生一条报警，避免每轮刷屏
            if (newState == state.AlarmState) return;
            state.AlarmState = newState;
            if (newState == 1)
                _alarms.Writer.TryWrite(new AlarmEvent(tag.Id, tag.Name, d, $"{tag.Name} 超上限 {tag.HighAlarm}", tv.Timestamp));
            else if (newState == 2)
                _alarms.Writer.TryWrite(new AlarmEvent(tag.Id, tag.Name, d, $"{tag.Name} 低于下限 {tag.LowAlarm}", tv.Timestamp));
        }
    }

    /// <summary>下发写值（参数设置）。与轮询/手动收发共享设备 IO 闸门，避免线帧串扰。</summary>
    public async Task<bool> WriteTagAsync(string deviceId, TagDefinition tag, object value)
    {
        if (!_devices.TryGetValue(deviceId, out var item))
            return false;
        var ct = _cts.Token;
        var gate = GetIoGate(deviceId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await item.Driver.WriteTagAsync(tag, value, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 单点手动读：立即执行一次指定 Tag 的读取，不必等下一轮轮询。
    /// 结果跳过变化死区直接进入采样流（UI 立即刷新 + 落库），并执行一次报警评估。
    /// </summary>
    public async Task<TagValue> ReadTagOnceAsync(string deviceId, TagDefinition tag)
    {
        if (!_devices.TryGetValue(deviceId, out var item))
            return TagValue.Bad(tag.Id);
        var ct = _cts.Token;
        var gate = GetIoGate(deviceId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tv = await item.Driver.ReadTagAsync(tag, ct).ConfigureAwait(false);
            // Stale 表示无读地址 / 纯订阅（MQTT）等，不推采样流
            if (tv.Quality != DataQuality.Stale)
            {
                _output.Writer.TryWrite(tv);
                EvaluateAlarm(tag, tv);
            }
            return tv;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>当前已注册驱动的手动原始收发能力；驱动未实现线帧接口返回 null（高层语义协议）。</summary>
    public ManualRawCapabilities? GetManualRawCapabilities(string deviceId)
    {
        if (_devices.TryGetValue(deviceId, out var item) && item.Driver is IManualRawDriver raw)
            return new ManualRawCapabilities(item.Driver.DriverType, raw.ManualHint, raw.AutoFrameLabel, raw.AutoFrameDefault);
        return null;
    }

    /// <summary>
    /// 手动原始帧收发：请求按 applyAutoFrame 决定是否由驱动自动补 MBAP/CRC/设备级校验。
    /// 通过设备 IO 闸门与轮询互斥。应答为空表示等待窗口内无数据（超时 / 未连接）。
    /// </summary>
    public async Task<ManualRawReply> SendRawFrameAsync(string deviceId, byte[] request, bool applyAutoFrame, CancellationToken ct = default)
    {
        if (!_devices.TryGetValue(deviceId, out var item))
            throw new InvalidOperationException("设备尚未注册（请先回到主界面“连接并启动”）");
        if (item.Driver is not IManualRawDriver raw)
            throw new NotSupportedException($"{item.Driver.DriverType} 为高层语义协议，不支持任意 Hex 原始帧收发，请改用 Tag 读写");

        var gate = GetIoGate(deviceId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await raw.SendRawFrameAsync(request, applyAutoFrame, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (!_running) return; // 未在运行
            _running = false;
            _reading = false;
            cts = _cts;
            _cts = new CancellationTokenSource(); // 提前换新，保证再次 Start 时拿到未取消的令牌
        }

        await cts.CancelAsync().ConfigureAwait(false);

        // 先停、再释放：等到所有轮询循环彻底退出后才 Dispose 驱动，
        // 否则循环内正在进行的一次收发会与 _ioLock.Dispose() 竞态，抛出 "Cannot access a disposed object"。
        List<Task> loops;
        lock (_lock)
        {
            loops = _loops.ToList();
            _loops.Clear();
        }
        try { await Task.WhenAll(loops).ConfigureAwait(false); }
        catch { /* 轮询循环可能因取消令牌抛出，忽略 */ }

        // Disposal: 驱动被释放（含内部 SemaphoreSlim/串口/TcpClient）。
        // 必须同时用 DeviceConfig 重建一个全新驱动实例写回 _devices，
        // 否则再次“连接并启动”会复用已释放驱动，导致 StartAsync 在 ConnectAsync 里抛
        // "Cannot access a disposed object. Object name: 'System.Threading.SemaphoreSlim'"。
        foreach (var kv in _devices)
        {
            var (device, driver, tags) = kv.Value;
            await driver.DisposeAsync().ConfigureAwait(false);
            var fresh = DriverFactory.Create(device);
            WireTrafficSink(fresh);
            _devices[kv.Key] = (device, fresh, tags);
        }

        // 注意：不 Complete 输出 channel，否则重连后无法再写入数据
        cts.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
