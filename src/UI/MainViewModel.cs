using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IndustrialProtocolAssistant.Acquisition;
using IndustrialProtocolAssistant.Core;
using IndustrialProtocolAssistant.Drivers;
using IndustrialProtocolAssistant.Drivers.Driver;
using IndustrialProtocolAssistant.Storage;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using Microsoft.Win32;
using Serilog;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace IndustrialProtocolAssistant.UI;

/// <summary>运行状态级别，用于驱动状态指示灯颜色。</summary>
public enum StatusLevel
{
    Idle,        // 空闲/未连接（灰）
    Connecting,  // 连接中（橙）
    Running,     // 运行中（绿）
    Error,       // 错误/失败（红）
    Stopped      // 已停止（蓝灰）
}

/// <summary>底部"运行提示"的级别：只用于 Tag 操作 / 参数校验 / 写值 / 异常等提示，与连接状态(StatusLevel)完全分离。</summary>
public enum NoticeLevel
{
    Info,     // 常规提示（灰）
    Success,  // 操作成功（绿）
    Warning,  // 需要留意的警告（橙）
    Error     // 操作/校验失败（红）
}

/// <summary>底部"运行提示"单条消息（时间 + 文本 + 级别）。</summary>
public sealed class NoticeItem
{
    public string Time { get; }
    public string Message { get; }
    public NoticeLevel Level { get; }

    public NoticeItem(DateTime stamp, string message, NoticeLevel level)
    {
        Time = stamp.ToString("HH:mm:ss");
        Message = message;
        Level = level;
    }
}

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const string TrafficDirectionAny = "全部方向";
    private const string TrafficDriverAny = "全部驱动";

    private readonly AcquisitionEngine _engine = new();
    private readonly TimeSeriesStore _store = new();
    private readonly ConfigStore _configStore = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly Dictionary<string, ObservableValue> _chartValues = new();

    /// <summary>供主窗体嵌入式“手动收发”面板直连采集引擎（同程序集，仅 internal 可见）。</summary>
    internal AcquisitionEngine Engine => _engine;
    /// <summary>当前设备 Id（手动收发面板直连同一台设备 IO 闸门）。</summary>
    internal string DeviceId => _deviceId;

    /// <summary>底部作者信息栏显示的版本号（取自程序集版本，兜底 1.0.0）。</summary>
    public string VersionText
    {
        get
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    /// <summary>顶部"日志"选项卡数据源（Serilog 内存接收器实时收集，最新在上）。</summary>
    public ObservableCollection<LogEntry> Logs => LogViewerSink.Instance.Entries;
    private readonly object _chartLock = new();
    private readonly object _tagsGate = new();

    // ---- 报文监控（Raw Traffic）：驱动载荷流 → 后台队列 → 定时批量刷到 UI ----
    private const int TrafficMaxEntries = 1000;
    private readonly ConcurrentQueue<TrafficFrame> _trafficQueue = new();
    private readonly DispatcherTimer _trafficTimer;

    /// <summary>最近一帧已显示报文的时间锚（用于计算 Δt 帧间隔）；清空/首帧时为 null。</summary>
    private DateTimeOffset? _lastTrafficTs;

    /// <summary>报文监控 Tab 数据源（最新在上）。</summary>
    public ObservableCollection<TrafficEntry> TrafficEntries { get; } = new();

    /// <summary>报文表格的绑定视图：支持关键字/方向/驱动过滤（过滤条件为空时退化为全量直连，零开销）。</summary>
    public ListCollectionView TrafficView { get; }

    /// <summary>暂停接收新报文（已入队未刷新的丢弃；用于定格观察某一段流量）。</summary>
    [ObservableProperty] private bool _trafficPaused;

    /// <summary>报文表格当前选中行（下方 Hex/ASCII 详情联动）。</summary>
    [ObservableProperty] private TrafficEntry? _selectedTraffic;

    /// <summary>报文监控状态提示文字。</summary>
    [ObservableProperty] private string _trafficStatus = "等待采集运行…";

    /// <summary>报文过滤关键字（匹配 操作/驱动/文本/Hex）。</summary>
    [ObservableProperty] private string _trafficFilterText = "";

    /// <summary>报文方向过滤：全部方向 / TX / RX。</summary>
    [ObservableProperty] private string _trafficDirectionFilter = TrafficDirectionAny;

    /// <summary>报文驱动过滤：全部驱动 / 具体驱动。</summary>
    [ObservableProperty] private string _trafficDriverFilter = TrafficDriverAny;

    /// <summary>方向过滤下拉候选。</summary>
    public string[] TrafficDirectionOptions { get; } = { TrafficDirectionAny, "TX", "RX" };

    /// <summary>驱动过滤下拉候选（动态拼当前支持的协议列表）。</summary>
    public string[] TrafficDriverOptions { get; }

    partial void OnTrafficFilterTextChanged(string value) => ApplyTrafficFilter();
    partial void OnTrafficDirectionFilterChanged(string value) => ApplyTrafficFilter();
    partial void OnTrafficDriverFilterChanged(string value) => ApplyTrafficFilter();

    /// <summary>可选驱动类型列表（来自驱动工厂声明，新增协议无需改 UI）。</summary>
    public string[] DriverTypes { get; } = DriverFactory.SupportedTypes;

    [ObservableProperty] private int _pollIntervalMs = 1000;
    [ObservableProperty] private string _status = "未连接";
    [ObservableProperty] private StatusLevel _statusLevel = StatusLevel.Idle;
    [ObservableProperty] private string _driverType = "ModbusTcp";

    /// <summary>采集引擎是否处于运行状态（含断线自动重连期间）。用于禁止运行中导入覆盖配置。</summary>
    private bool _collecting;

    /// <summary>是否正在自动轮询读取。连接建立后默认不读取，由“数据监控”页“读取/停止”按钮控制。</summary>
    [ObservableProperty] private bool _isReading;

    /// <summary>导入配置切换驱动时临时抑制 OnDriverTypeChanged 里的 Tag 加载，避免旧配置闪现。</summary>
    private bool _suppressDriverChangeTagLoad;

    /// <summary>动态连接参数表单（随驱动类型重建，XAML 绑定此集合）。</summary>
    public ObservableCollection<ConnectionParamItem> ConnectionParams { get; } = new();

    /// <summary>Tag 地址输入框的提示文字（随协议变化：Modbus 寄存器号 / OPC UA NodeId / MQTT Topic）。</summary>
    [ObservableProperty] private string _tagAddressHint = "0~65535（Bool 为线圈地址）";

    /// <summary>Tag 配置区"类型"下拉框是否可见（Modbus/SerialFree 必须指定；OPC UA 默认"自动"识别、也可手动指定；S7 支持"自动"按地址后缀推断；MQTT 由 payload 自动推断）。</summary>
    public bool IsTagTypeVisible => DriverType is "ModbusTcp" or "ModbusRtu" or "SerialFree" or "Socket" or "OpcUa" or "S7";

    /// <summary>Tag 配置区"浏览…"按钮是否可见（仅 OPC UA 提供节点树浏览）。</summary>
    public bool IsBrowseButtonVisible => DriverType == "OpcUa";

    /// <summary>Tag 配置区"长度"输入框是否可见（Modbus=寄存器数；SerialFree=String 最大字节数；S7=固定长度字符串的字符数；OPC UA / MQTT 隐藏）。</summary>
    public bool IsTagLengthVisible => DriverType is "ModbusTcp" or "ModbusRtu" or "SerialFree" or "Socket" or "S7";

    /// <summary>Tag 表格"区/类别"列的标题，随驱动类型动态变化（Modbus→区、MQTT→Topic、OPC UA→NodeId、S7→地址、SerialFree→命令帧）。</summary>
    public string AreaColumnHeader => DriverType switch
    {
        "Mqtt" => "Topic",
        "OpcUa" => "NodeId",
        "ModbusTcp" or "ModbusRtu" => "区",
        "S7" => "地址",
        "SerialFree" or "Socket" => "命令帧",
        _ => "类别"
    };

    /// <summary>Tag 配置区"类型"下拉框的 ToolTip，随协议变化。</summary>
    public string TagTypeHint => DriverType switch
    {
        "OpcUa" => "选“自动”由服务器自动识别节点类型；也可手动指定（推荐自动）",
        "S7" => "选“自动”按地址后缀推断（B→字节 / W→字 / D→双字 / X→位）；String 需配合下方“长度”",
        "Mqtt" => "MQTT 由 payload 自动推断类型，无需指定",
        "SerialFree" or "Socket" => "必须指定：决定从回复帧中提取的字节数（Bool=1 / Int16·UInt16=2 / Int32·UInt32·Float=4 / Double=8；String 需配合下方“长度”）",
        _ => "Modbus 必须指定类型：Bool=线圈 / Int16~Double=寄存器 / String=字符区",
    };

    /// <summary>Tag 配置区"长度"输入框的 ToolTip，随协议变化（Modbus=寄存器数；SerialFree=字节数；S7=字符数）。</summary>
    public string TagLengthHint => DriverType switch
    {
        "S7" => "仅 String 类型有效：固定长度字符串的字符数（ASCII 字符 1 字节），默认 8",
        "SerialFree" or "Socket" => "仅 String 类型有效：从回复帧中最多读取的 ASCII 字节数（遇 0x00 截断），默认 8",
        _ => "仅 String 类型有效：寄存器数，1 个寄存器存 2 个 ASCII 字符，默认 8（16 字符）",
    };

    /// <summary>Tag 配置区"发送 Topic"输入行是否可见（仅 MQTT 支持：为 Tag 单独指定发布 Topic，写入发到它而不再拼写后缀）。</summary>
    public bool IsTagWriteAddressVisible => DriverType == "Mqtt";

    /// <summary>Tag 配置区"发送 Topic"输入框的提示文字。</summary>
    public string TagWriteAddressHint =>
        "填写后写入直接发布到该 Topic（不拼写后缀，如 devices/01/ctrl）；留空则按「订阅 Topic + 连接参数中的写后缀」下发";

    /// <summary>上下限报警是否对当前新 Tag 数据类型生效：String/Bool 无法数值比较，报警不生效，输入框应禁用。</summary>
    public bool IsAlarmEnabled => NewTagType switch
    {
        TagDataType.Bool or TagDataType.String => false,
        _ => true,
    };

    /// <summary>上限/下限输入框 ToolTip：类型不支持数值比较时提示报警不可用，其余情况说明填写方式。</summary>
    public string AlarmHint => NewTagType switch
    {
        TagDataType.String or TagDataType.Bool =>
            "字符串 / Bool 无法数值比较，上下限报警不生效；如需报警请改选数值类型（Int16~Double）",
        _ => "数值型 Tag 生效：超过该值触发高报，留空不启用；Auto 将按运行时的实际类型决定",
    };

    partial void OnNewTagTypeChanged(TagDataType value)
    {
        OnPropertyChanged(nameof(IsAlarmEnabled));
        OnPropertyChanged(nameof(AlarmHint));
    }

    [ObservableProperty] private string _newTagName = "温度";
    [ObservableProperty] private string _newTagAddress = "0";
    [ObservableProperty] private TagDataType _newTagType = TagDataType.Auto;
    [ObservableProperty] private string _newTagHighAlarm = "";
    [ObservableProperty] private string _newTagLowAlarm = "";
    [ObservableProperty] private string _newTagLength = "8";
    [ObservableProperty] private string _newTagWriteAddress = "";   // 仅 MQTT 使用：该 Tag 独立的发送 Topic
    [ObservableProperty] private string _writeValue = "";
    [ObservableProperty] private TagItem? _selectedTag;
    [ObservableProperty] private TagItem? _writeTargetTag;
    [ObservableProperty] private string _writeTargetHint = "从下拉列表选择要写入的 Tag";
    [ObservableProperty] private double _deadband = 0.5;
    [ObservableProperty] private TagItem? _historyTag;
    [ObservableProperty] private int _historyMinutes = 30;
    [ObservableProperty] private string _historyStatus = "选择 Tag 后点击查询";

    /// <summary>当前正在"就地编辑"的 Tag（非 null 时下方表单进入编辑态，"添加 Tag"变为"保存修改"）。</summary>
    [ObservableProperty] private TagItem? _editingTag;

    /// <summary>是否处于 Tag 就地编辑模式（表单按钮文案 / 编辑按钮可用性据此切换）。</summary>
    public bool IsEditingTagVisible => EditingTag is not null;

    /// <summary>Tag 管理按钮区提示文案：普通/编辑两种状态。</summary>
    public string TagEditorHint => EditingTag is null
        ? "在下方录入后点“添加 Tag”；选中表格某行后点“编辑选中”即可就地修改"
        : $"正在就地编辑：{EditingTag.Name}（保留原 Id，仅改其余字段）— 点“保存修改”提交 / “取消编辑”放弃";

    partial void OnEditingTagChanged(TagItem? value)
    {
        OnPropertyChanged(nameof(IsEditingTagVisible));
        OnPropertyChanged(nameof(TagEditorHint));
    }

    /// <summary>底部"运行提示"当前条（单行覆盖式）：仅保留最近一条重要操作反馈，下一条提示会整体替换本条。
    /// Tag 操作/校验/写值等提示专用，不占用顶部连接状态；历史细节请查看右侧系统日志。</summary>
    private NoticeItem? _currentNotice;
    public NoticeItem? CurrentNotice
    {
        get => _currentNotice;
        private set
        {
            if (ReferenceEquals(_currentNotice, value)) return;
            _currentNotice = value;
            OnPropertyChanged();
        }
    }

    /// <summary>推送一条"运行提示"（单行覆盖式，跨线程安全）。该通道与顶部连接状态(Status/StatusLevel)完全分离：
    /// 顶部只显示连接/启动/停止相关状态；其余提示一律通过此方法下发，仅保留最后一条。</summary>
    public void PushNotice(string message, NoticeLevel level = NoticeLevel.Info)
    {
        var item = new NoticeItem(DateTime.Now, message, level);
        if (App.Current.Dispatcher.CheckAccess())
            CurrentNotice = item;
        else
            App.Current.Dispatcher.BeginInvoke(new Action(() => CurrentNotice = item));
    }

    /// <summary>更新顶部连接状态文字与指示灯级别。仅连接/启动/停止/重连流程使用；
    /// Tag 操作、参数校验、写值、节点浏览等提示请用 <see cref="PushNotice"/>，不要混用连接状态栏。</summary>
    private void SetStatus(string message, StatusLevel? level = null)
    {
        Status = message;
        if (level.HasValue) StatusLevel = level.Value;
    }

    /// <summary>写入目标 Tag（下拉列表）变更：清空下发值框并更新提示。</summary>
    partial void OnWriteTargetTagChanged(TagItem? value)
    {
        WriteValue = "";
        WriteTargetHint = value is null
            ? "从下拉列表选择要写入的 Tag"
            : DescribeWriteTarget(value);
        // 写入目标提示仅记日志，不占用状态栏
        if (value is not null)
            Log.Information("写入目标已选择：{Name} ({Type})", value.Name, value.DataType);
    }

    /// <summary>写入目标提示文本：MQTT 且配置了独立发送 Topic 时额外标明实际发布目标。</summary>
    private string DescribeWriteTarget(TagItem value)
    {
        var head = value.DataType == TagDataType.String
            ? DriverType switch
            {
                "S7" => $"类型 String · 地址 {value.Address} · 最多 {value.Length} 个字符",
                "SerialFree" or "Socket" => $"类型 String · 地址 {value.Address} · 最多 {value.Length} 个 ASCII 字节",
                _ => $"类型 String · 地址 {value.Address} · 最多 {value.Length * 2} 个 ASCII 字符",
            }
            : $"类型 {value.DataType} · 地址 {value.Address} · 实时值 {value.CurrentValue}";
        if (DriverType == "Mqtt" && !string.IsNullOrWhiteSpace(value.WriteAddressText))
            head += $" · 发送 {value.WriteAddressText}";
        return head;
    }

    public ObservableCollection<TagItem> Tags { get; } = new();
    public ObservableCollection<AlarmEvent> Alarms { get; } = new();
    /// <summary>Tag 配置区"类型"下拉选项，随协议动态刷新（OPC UA 含"自动"项，Modbus/MQTT 不含）。</summary>
    public ObservableCollection<TagDataType> DataTypes { get; } = new();
    public ObservableCollection<string> AvailableSerialPorts { get; } = new();

    /// <summary>枚举系统当前可用的串口并刷新下拉列表（动态表单中的串口下拉同步更新）。</summary>
    [RelayCommand]
    private void RefreshSerialPorts()
    {
        var ports = System.IO.Ports.SerialPort.GetPortNames();
        AvailableSerialPorts.Clear();
        foreach (var p in ports) AvailableSerialPorts.Add(p);
        foreach (var item in ConnectionParams.Where(i => i.IsSerialPort))
        {
            item.Options.Clear();
            foreach (var p in ports) item.Options.Add(p);
        }
        if (AvailableSerialPorts.Count == 0)
            Log.Warning("未检测到可用串口，请检查 USB 转串口驱动或虚拟串口工具");
        else
            Log.Information("检测到可用串口：{Ports}", string.Join(", ", AvailableSerialPorts));
    }

    /// <summary>按当前驱动类型重建动态参数表单（含默认值；串口类型填充系统枚举）。</summary>
    private void RebuildConnectionParams()
    {
        ConnectionParams.Clear();
        foreach (var field in DriverFactory.GetFields(DriverType))
        {
            var item = new ConnectionParamItem(field);
            if (item.IsSerialPort)
                foreach (var p in AvailableSerialPorts) item.Options.Add(p);
            ConnectionParams.Add(item);
        }
        TagAddressHint = DriverType switch
        {
            "OpcUa" => "NodeId，如 ns=2;s=温度",
            "Mqtt" => "订阅 Topic，如 sensor/temp",
            "S7" => "DB1.DBD0 / MW0 / M0.0 等 S7 地址",
            "SerialFree" or "Socket" => "读帧hex[@偏移] || 写帧hex；帧校验可在连接参数“帧校验”下拉自动补帧尾（CRC16/XOR/SUM，读/写帧均生效），也可在地址内显式写 {crc16} 等占位符",
            _ => "0~65535（Bool 为线圈地址）",
        };
        // 切换协议后地址框给出该协议示例（仅当残留地址与协议明显不符时重置，避免覆盖用户输入）
        if (DriverType == "S7" && ushort.TryParse(NewTagAddress, out _))
            NewTagAddress = "DB1.DBD0";
        else if (DriverType is "ModbusTcp" or "ModbusRtu" && !ushort.TryParse(NewTagAddress, out _))
            NewTagAddress = "0";
        else if (DriverType is "SerialFree" or "Socket" && ushort.TryParse(NewTagAddress, out _))
            NewTagAddress = "01 03 00 00 00 01 84 0A@4";
        if (DriverType != "Mqtt") NewTagWriteAddress = ""; // 发送 Topic 仅 MQTT 概念，切换协议时清空避免误填
        OnPropertyChanged(nameof(IsTagLengthVisible));
        OnPropertyChanged(nameof(IsTagTypeVisible));
        OnPropertyChanged(nameof(IsBrowseButtonVisible));
        OnPropertyChanged(nameof(IsTagWriteAddressVisible));
        OnPropertyChanged(nameof(TagWriteAddressHint));
        OnPropertyChanged(nameof(TagTypeHint));
        OnPropertyChanged(nameof(TagLengthHint));
        RefreshDataTypes();
    }

    /// <summary>按当前协议重建类型下拉内容：OPC UA / S7 含"自动"（服务器识别 / 地址后缀推断）；Modbus/MQTT 不含（必须/无需显式类型）。</summary>
    private void RefreshDataTypes()
    {
        var needAuto = DriverType is "OpcUa" or "S7";
        DataTypes.Clear();
        if (needAuto) DataTypes.Add(TagDataType.Auto);
        foreach (var t in Enum.GetValues<TagDataType>())
            if (t != TagDataType.Auto) DataTypes.Add(t);

        if (!DataTypes.Contains(NewTagType))
            NewTagType = needAuto ? TagDataType.Auto : TagDataType.Float;
    }

    /// <summary>驱动切换时重建表单，并自动加载该协议下已保存的 Tag 配置（按协议维度持久化恢复）。
    /// 正在编辑的 Tag 会被取消（所属协议已变）。导入配置时会通过 _suppressDriverChangeTagLoad 临时跳过加载。</summary>
    partial void OnDriverTypeChanged(string value)
    {
        if (EditingTag is not null) EditingTag = null; // 协议变了，表单回填已无意义
        RebuildConnectionParams();
        OnPropertyChanged(nameof(AreaColumnHeader));
        if (_suppressDriverChangeTagLoad)
        {
            PushNotice($"驱动已切换为 {value}（配置导入中，Tag 稍后覆盖）");
        }
        else
        {
            LoadTagsForCurrentDriver();
            PushNotice(Tags.Count > 0
                ? $"已加载 {value} 协议下 {Tags.Count} 个已保存的 Tag"
                : $"驱动已切换为 {value}：无已保存的 Tag，请添加");
        }
    }

    /// <summary>从数据库加载当前协议下已保存的 Tag（替换现有列表），并同步图表序列。</summary>
    private void LoadTagsForCurrentDriver()
    {
        lock (_tagsGate) Tags.Clear();
        _chartValues.Clear();
        ChartSeries.Clear();
        var defs = _configStore.LoadTags(DriverType);
        foreach (var d in defs) AddTagInternal(d);
        if (defs.Count > 0)
            Log.Information("已加载 {Driver} 协议下的 {Count} 个 Tag 配置", DriverType, defs.Count);
    }

    /// <summary>把已保存的设备连接参数填回动态表单（按 Key 匹配）。</summary>
    private void RestoreConnectionParams(DeviceConfig cfg)
    {
        foreach (var item in ConnectionParams)
        {
            if (cfg.Params.TryGetValue(item.Key, out var v))
                item.Value = v;
        }
    }

    /// <summary>生成连接描述的简短文本（用于状态栏与日志）。</summary>
    private static string DescribeConfig(DeviceConfig cfg) => cfg.DriverType switch
    {
        "OpcUa" => cfg.Get("EndpointUrl", "opc.tcp://..."),
        "ModbusRtu" => $"{cfg.Get("SerialPort")} @ {cfg.GetInt("BaudRate", 9600)}bps",
        "SerialFree" => $"{cfg.Get("SerialPort")} @ {cfg.GetInt("BaudRate", 9600)}bps",
        "Mqtt" => $"{cfg.Get("BrokerUrl", "mqtt://...")} QoS={cfg.GetInt("QoS", 0)}",
        "S7" => $"{cfg.Get("Host")}:{cfg.GetInt("Port", 102)} ({cfg.Get("CpuType", "S7300")} Rack={cfg.GetByte("Rack", 0)} Slot={cfg.GetByte("Slot", 1)})",
        "Socket" => $"{cfg.Get("Host")}:{cfg.GetInt("Port", 502)}",
        _ => $"{cfg.Get("Host")}:{cfg.GetInt("Port", 502)} 从站={cfg.GetByte("SlaveId", 1)}",
    };

    // 图表（实时趋势）
    public ObservableCollection<ISeries> ChartSeries { get; } = new();
    public Axis[] ChartXAxis { get; } = { new Axis { IsVisible = false } };

    // 图表（历史曲线）
    public ObservableCollection<ISeries> HistorySeries { get; } = new();
    public Axis[] HistoryXAxis { get; } =
    {
        new Axis
        {
            Labeler = value => DateTimeOffset.FromUnixTimeMilliseconds((long)value).ToString("HH:mm:ss"),
            MinStep = TimeSpan.FromMinutes(1).TotalMilliseconds
        }
    };

    public ICommand StartCommand => new RelayCommand(Start);
    public ICommand StopCommand => new RelayCommand(async () => await StopAsync());
    public ICommand AddTagCommand => new RelayCommand(AddTag);
    public ICommand EditTagCommand => new RelayCommand(BeginEditTag);
    public ICommand CancelEditTagCommand => new RelayCommand(CancelEditTag);
    public ICommand WriteCommand => new RelayCommand(async () => await WriteAsync());
    public ICommand DeleteTagCommand => new RelayCommand(DeleteSelectedTag);
    public ICommand LoadHistoryCommand => new RelayCommand(async () => await LoadHistoryAsync());
    public ICommand ExportHistoryCommand => new RelayCommand(ExportHistoryCsv);
    public ICommand ClearLogsCommand => new RelayCommand(() => Logs.Clear());
    public ICommand OpenNodeBrowserCommand => new RelayCommand(OpenNodeBrowser);
    public ICommand ToggleTrafficPauseCommand => new RelayCommand(() => TrafficPaused = !TrafficPaused);
    public ICommand ClearTrafficCommand => new RelayCommand(ClearTraffic);
    public ICommand ResetTrafficFilterCommand => new RelayCommand(ResetTrafficFilter);
    public ICommand ExportTrafficCommand => new RelayCommand(ExportTraffic);
    public ICommand CopyHexCommand => new RelayCommand<TrafficEntry?>(e => CopyTrafficPayload(e, ascii: false));
    public ICommand CopyAsciiCommand => new RelayCommand<TrafficEntry?>(e => CopyTrafficPayload(e, ascii: true));
    public ICommand ExportConfigCommand => new RelayCommand(ExportDeviceProfile);
    public ICommand ImportConfigCommand => new RelayCommand(ImportDeviceProfile);
    /// <summary>Tag 行“读一次”：立即执行一次该 Tag 的读取，不等轮询周期。</summary>
    public ICommand ReadOnceCommand => new RelayCommand<TagItem>(async tag => await ReadTagOnceAsync(tag));
    /// <summary>“数据监控”页读取按钮：连接就绪后开始自动轮询采样。</summary>
    public ICommand StartReadingCommand => new RelayCommand(StartReading);
    /// <summary>“数据监控”页停止按钮：暂停自动轮询采样（保持已连接，仍可写值 / 手动收发 / 单点读一次）。</summary>
    public ICommand StopReadingCommand => new RelayCommand(StopReading);

    private DeviceConfig? _deviceConfig;
    private string _deviceId = "dev1";
    private List<ObservablePoint>? _lastHistoryPoints;
    private string? _lastHistoryTagName;

    public MainViewModel()
    {
        TrafficDriverOptions = new[] { TrafficDriverAny }.Concat(DriverFactory.SupportedTypes).ToArray();
        TrafficView = new ListCollectionView(TrafficEntries);
        // 无过滤时 Filter 保持 null（列表直连，插入零开销）；设置过滤条件时才挂谓词
        TrafficView.Filter = null;

        // 初始化可用串口列表（首次打开 UI 即自动枚举）
        RefreshSerialPorts();

        // 按默认驱动（ModbusTcp）生成初始动态表单；切换驱动时由 OnDriverTypeChanged 重建
        RebuildConnectionParams();

        // 引擎日志通过 Serilog 输出，方便排查 MQTT 等驱动问题
        _engine.LogAction = msg => Log.Information(msg);

        // 订阅引擎断线/恢复通知，更新状态栏指示灯
        _engine.ConnectionChanged += OnConnectionChanged;

        // 启动后台读取：从 Channel 消费采样值，更新 UI（回到 UI 线程）
        Task.Run(ConsumeValuesAsync);
        Task.Run(ConsumeAlarmsAsync);

        // 报文监控：驱动上报 → 后台 Channel 消费进队列 → 定时器按小批量刷新 UI（避免高频报文阻塞 UI 线程）
        Task.Run(ConsumeTrafficAsync);
        _trafficTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _trafficTimer.Tick += (_, _) => DrainTrafficToUi();
        _trafficTimer.Start();

        // 从数据库恢复上次保存的设备配置（协议、连接参数、轮询间隔）与该协议下已保存的 Tag。
        // DriverType 赋值与当前值不同时会触发 OnDriverTypeChanged（重建表单 + 加载对应协议 Tag）。
        var saved = _configStore.LoadDevice();
        if (saved is not null)
        {
            DriverType = saved.DriverType;
            PollIntervalMs = saved.PollIntervalMs;
            RestoreConnectionParams(saved);
            if (saved.DriverType == "ModbusTcp") // 与默认值相同，OnDriverTypeChanged 未触发，手动加载一次
                LoadTagsForCurrentDriver();
            Log.Information("已恢复上次保存的配置：{Driver}，加载 {Count} 个 Tag", saved.DriverType, Tags.Count);
        }
        else
        {
            Log.Information("初始化完成：请选择驱动、填写连接参数并添加 Tag，再点击“连接并启动”。");
        }
    }

    private void OpenNodeBrowser()
    {
        if (DriverType != "OpcUa") return;
        try
        {
            var paramDict = ConnectionParams.ToDictionary(p => p.Key, p => p.Value.Trim());
            var config = new DeviceConfig(_deviceId, "设备1", DriverType, PollIntervalMs, paramDict);
            var window = new OpcUaNodeBrowserWindow(config, (nodeId, name, dataType) =>
            {
                // 把浏览器选中的节点回填到 Tag 配置：地址 = NodeId，名称 = 节点浏览名，类型 = 服务器数据类型
                NewTagAddress = nodeId;
                if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, nodeId, StringComparison.Ordinal))
                    NewTagName = name;
                if (!string.IsNullOrWhiteSpace(dataType))
                    NewTagType = MapDataTypeName(dataType);
            })
            {
                Owner = App.Current.MainWindow,
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            PushNotice($"打开节点浏览器失败：{ex.Message}", NoticeLevel.Error);
            Log.Error(ex, "打开节点浏览器失败");
        }
    }

    /// <summary>
    /// Tag 行“读一次”：立即执行一次读取（不等轮询周期），结果立即刷新到该行值/质量并落库。
    /// 校验性读取与轮询共用同一把设备 IO 闸门，不会造成应答串帧。
    /// </summary>
    private async Task ReadTagOnceAsync(TagItem tag)
    {
        if (!_collecting)
        {
            PushNotice($"手动读 {tag.Name}：请先在主界面点击“连接并启动”（当前引擎未运行）", NoticeLevel.Warning);
            return;
        }
        try
        {
            var tv = await _engine.ReadTagOnceAsync(_deviceId, tag.Definition).ConfigureAwait(false);
            var text = tv.Value?.ToString() ?? "-";
            var quality = tv.Quality.ToString();
            // 无论结果好坏都立即回写该行（Good 之外的值引擎不推采样流，需就地展示）
            App.Current.Dispatcher.Invoke(() => tag.SetCurrent(text, quality));
            if (tv.Quality == DataQuality.Stale)
                PushNotice($"手动读 {tag.Name}（{tag.Address}）无读地址（只写/订阅型 Tag）", NoticeLevel.Warning);
            else if (tv.Quality == DataQuality.Bad)
                PushNotice($"手动读 {tag.Name}（{tag.Address}）失败：无应答或设备未连接", NoticeLevel.Warning);
            else
            {
                PushNotice($"手动读 OK：{tag.Name} = {text}", NoticeLevel.Success);
                Log.Information("手动读成功：{Name} = {Value}", tag.Name, text);
            }
        }
        catch (Exception ex)
        {
            PushNotice($"手动读 {tag.Name} 异常：{ex.Message}", NoticeLevel.Error);
            Log.Error(ex, "手动读异常 {Name}", tag.Name);
        }
    }

    /// <summary>把 OPC UA 服务器数据类型映射为 TagDataType（无对应项时留"自动"）。</summary>
    private static TagDataType MapDataTypeName(string dataType)
    {
        return dataType switch
        {
            "Boolean" => TagDataType.Bool,
            "SByte" or "Int16" or "Int32" or "Int64" => TagDataType.Int32,
            "Byte" or "UInt16" or "UInt32" or "UInt64" => TagDataType.UInt32,
            "Float" => TagDataType.Float,
            "Double" => TagDataType.Double,
            "String" => TagDataType.String,
            _ => TagDataType.Auto,
        };
    }

    private void Start()
    {
        try
        {
            var paramDict = ConnectionParams.ToDictionary(p => p.Key, p => p.Value.Trim());
            _deviceConfig = new DeviceConfig(_deviceId, "设备1", DriverType, PollIntervalMs, paramDict);
            _engine.ValueDeadband = Deadband;
            _engine.RegisterDevice(_deviceConfig, Tags.Select(t => t.Definition).ToList());
            // 启动时持久化配置到数据库（下次启动自动恢复）
            _configStore.SaveDevice(_deviceConfig);
            _configStore.SaveTags(Tags.Select(t => t.Definition).ToList(), DriverType);
            SetStatus("正在连接...", StatusLevel.Connecting);
            var endpoint = DescribeConfig(_deviceConfig);
            Log.Information("正在连接：{Driver} {Endpoint} 间隔={Poll}ms", DriverType, endpoint, PollIntervalMs);
            _collecting = true;
            IsReading = false;
            _ = Task.Run(async () =>
            {
                try
                {
                    // 只建立连接与保持，不自动开始轮询读取；采样由“数据监控”页的“读取”按钮开启
                    await _engine.StartAsync(startReading: false).ConfigureAwait(false);
                    SetStatus("已连接", StatusLevel.Running);
                    PushNotice($"连接成功：{DriverType}。到“数据监控”页点“读取”即开始采样", NoticeLevel.Success);
                    Log.Information("采集引擎已连接（待“读取”启动采样）：{Driver} {Endpoint}", DriverType, endpoint);
                }
                catch (Exception ex)
                {
                    _collecting = false;
                    SetStatus($"启动失败：{ex.Message}", StatusLevel.Error);
                    Log.Error(ex, "采集启动失败");
                }
            });
        }
        catch (Exception ex)
        {
            _collecting = false;
            SetStatus($"启动失败：{ex.Message}", StatusLevel.Error);
            Log.Error(ex, "启动失败");
        }
    }

    /// <summary>连接就绪后点击“读取”：开启自动轮询采样（此后数值/曲线/报警按轮询间隔更新）。</summary>
    private void StartReading()
    {
        if (!_collecting)
        {
            PushNotice("请先点击“连接并启动”建立连接，再开始读取", NoticeLevel.Warning);
            return;
        }
        if (IsReading) return;
        try
        {
            _engine.ReadingEnabled = true;
            IsReading = true;
            SetStatus("运行中 · 读取中", StatusLevel.Running);
            PushNotice($"已开始读取：每 {PollIntervalMs} ms 采样一次全部 Tag", NoticeLevel.Success);
            Log.Information("开始自动轮询读取（间隔 {Poll}ms，{Count} 个 Tag）", PollIntervalMs, Tags.Count);
        }
        catch (Exception ex)
        {
            PushNotice($"开启读取失败：{ex.Message}", NoticeLevel.Error);
            Log.Error(ex, "开启读取失败");
        }
    }

    /// <summary>点击“停止”：暂停自动轮询采样，保持连接（仍可下发写入 / 手动收发 / 单点“读一次”）。</summary>
    private void StopReading()
    {
        if (!IsReading) return;
        try
        {
            _engine.ReadingEnabled = false;
            IsReading = false;
            SetStatus("已连接 · 读取已暂停", StatusLevel.Running);
            PushNotice("已暂停自动读取（连接保持）：需要时点“读取”恢复，或下发写入 / 手动收发仍可用");
            Log.Information("已暂停自动轮询读取（连接保持）");
        }
        catch (Exception ex)
        {
            PushNotice($"停止读取失败：{ex.Message}", NoticeLevel.Error);
            Log.Error(ex, "停止读取失败");
        }
    }

    /// <summary>引擎断线/恢复通知（后台线程触发，WPF 绑定自动封送）。</summary>
    private void OnConnectionChanged(string deviceId, bool connected)
    {
        if (deviceId != _deviceId) return;
        if (connected)
        {
            SetStatus("运行中", StatusLevel.Running);
            Log.Information("设备已恢复连接：{DeviceId}", deviceId);
        }
        else
        {
            SetStatus("连接中断，正在重连...", StatusLevel.Error);
            Log.Warning("设备连接中断：{DeviceId}，尝试自动重连", deviceId);
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await _engine.StopAsync().ConfigureAwait(false);
            _collecting = false;
            IsReading = false;
            SetStatus("已停止", StatusLevel.Stopped);
            Log.Information("采集已停止");
        }
        catch (Exception ex)
        {
            SetStatus($"停止异常：{ex.Message}", StatusLevel.Error);
            Log.Error(ex, "停止异常");
        }
    }

    /// <summary>「添加 Tag」入口：普通状态走新增；编辑状态下同一按钮变为"保存修改"。</summary>
    private void AddTag()
    {
        if (EditingTag is not null)
        {
            CommitEditTag();
            return;
        }

        if (!TryBuildTagFromForm(out var def, out var error))
        {
            PushNotice(error, NoticeLevel.Error);
            return;
        }
        if (def is null) return; // 防御：成功分支不应为 null
        AddTagInternal(def);
        SaveTagsAndSyncEngine(DriverType);
        Log.Information("添加 Tag：{Name} 地址={Addr} 类型={Type} 长度={Len} 上限={High} 下限={Low} 发送Topic={Write}",
            NewTagName, def.Address, NewTagType, def.Length, def.HighAlarm?.ToString() ?? "-", def.LowAlarm?.ToString() ?? "-",
            NewTagWriteAddress.Length > 0 ? NewTagWriteAddress : "-");
    }

    /// <summary>把当前表单内容 + 协议校验合成为一条 TagDefinition（新增/编辑共用同一套校验与解析规则）。</summary>
    private bool TryBuildTagFromForm(out TagDefinition? def, out string error)
    {
        def = null;
        error = "";

        var addr = NewTagAddress.Trim();
        if (string.IsNullOrWhiteSpace(addr))
        {
            error = "请输入 Tag 地址";
            return false;
        }
        // 地址校验随协议分流：Modbus 需要 0~65535 的数字；S7 需要合法的 S7 地址；OPC UA / MQTT 接受任意地址字符串
        if (DriverType == "S7")
        {
            if (!S7Driver.TryValidateAddress(addr, out var s7Error))
            {
                error = $"地址无效：{addr}（{s7Error}）";
                return false;
            }
        }
        else if (DriverType is "ModbusTcp" or "ModbusRtu" && !ushort.TryParse(addr, out _))
        {
            error = $"地址无效：{addr}（{DriverType} 需要 0~65535 的数字）";
            return false;
        }
        else if (DriverType == "SerialFree" && !SerialFreeDriver.TryValidateAddress(addr, out var freeError))
        {
            error = $"地址无效：{addr}（{freeError}）";
            return false;
        }
        else if (DriverType == "Socket" && !SocketDriver.TryValidateAddress(addr, out var sockError))
        {
            error = $"地址无效：{addr}（{sockError}）";
            return false;
        }
        // MQTT 独立发送 Topic：可选；填写后写入直接发布到该 Topic，不能含通配符（否则无法作为发布目标）
        var writeAddr = DriverType == "Mqtt" ? NewTagWriteAddress.Trim() : string.Empty;
        if (DriverType == "Mqtt" && (writeAddr.Contains('+') || writeAddr.Contains('#')))
        {
            error = "发送 Topic 不能包含通配符 + 或 #";
            return false;
        }
        // 报警上下限：留空表示不启用该方向报警；只填一个则只启用对应方向
        double? high = double.TryParse(NewTagHighAlarm, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h
            : double.TryParse(NewTagHighAlarm, out h) ? h : null;
        double? low = double.TryParse(NewTagLowAlarm, NumberStyles.Float, CultureInfo.InvariantCulture, out var l) ? l
            : double.TryParse(NewTagLowAlarm, out l) ? l : null;
        // 长度仅 String 类型有效（寄存器数，1 寄存器 = 2 个 ASCII 字符）；非法/未填时用默认 8
        int? regLen = NewTagType == TagDataType.String && int.TryParse(NewTagLength, out var n) && n > 0
            ? n
            : null;
        def = TagDefinition.Create(NewTagName, NewTagType, _deviceId, addr, high, low, regLen,
            string.IsNullOrEmpty(writeAddr) ? null : writeAddr);
        return true;
    }

    /// <summary>把选中行 Tag 回填到编辑表单并进入编辑模式（"添加 Tag"按钮变为"保存修改"）。</summary>
    private void BeginEditTag()
    {
        var t = SelectedTag;
        if (t is null) return;
        NewTagName = t.Name;
        NewTagAddress = t.Address;
        NewTagType = t.DataType;
        NewTagLength = t.Length.ToString(CultureInfo.InvariantCulture);
        NewTagHighAlarm = t.Definition.HighAlarm?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        NewTagLowAlarm = t.Definition.LowAlarm?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        NewTagWriteAddress = t.WriteAddressText;
        EditingTag = t;
        PushNotice($"正在编辑 Tag：{t.Name}（修改后点击“保存修改”，协议与历史图表引用保持不变）");
    }

    private void CancelEditTag()
    {
        if (EditingTag is null) return;
        EditingTag = null;
        PushNotice("已取消编辑");
    }

    /// <summary>提交就地编辑：保留原 Tag.Id（图表/历史/写值引用不失效），其余字段按表单替换并同步引擎与持久化。</summary>
    private void CommitEditTag()
    {
        var target = EditingTag;
        if (target is null) return;

        if (!TryBuildTagFromForm(out var def, out var error))
        {
            PushNotice(error, NoticeLevel.Error);
            return;
        }
        if (def is null) return;

        if (SameTagConfig(target.Definition, def))
        {
            EditingTag = null;
            PushNotice($"未检测到修改：{target.Name} 保持原样");
            return;
        }

        var old = target.Definition;
        var oldName = old.Name;
        var newDef = def with { Id = old.Id, DeviceId = old.DeviceId };
        target.ApplyDefinition(newDef);

        // 图表曲线名称跟随新名称（曲线值与 _chartValues[Id] 绑定，Id 不变故无需重建）
        foreach (var s in ChartSeries)
            if (s is LineSeries<ObservableValue> line && string.Equals(line.Name, oldName, StringComparison.Ordinal))
            {
                line.Name = newDef.Name;
                break;
            }

        SaveTagsAndSyncEngine(DriverType);
        EditingTag = null;
        PushNotice($"已更新 Tag：{newDef.Name}（{old.DataType}→{newDef.DataType}）", NoticeLevel.Success);
        Log.Information("编辑 Tag：{Name} 地址={Addr} 类型={Type} 长度={Len} 上限={High} 下限={Low}",
            newDef.Name, newDef.Address, newDef.DataType, newDef.Length,
            newDef.HighAlarm?.ToString() ?? "-", newDef.LowAlarm?.ToString() ?? "-");
    }

    private static bool SameTagConfig(TagDefinition a, TagDefinition b)
    {
        return string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            && a.DataType == b.DataType
            && string.Equals(a.Address, b.Address, StringComparison.Ordinal)
            && a.Length == b.Length
            && a.HighAlarm == b.HighAlarm
            && a.LowAlarm == b.LowAlarm
            && string.Equals(a.WriteAddress ?? "", b.WriteAddress ?? "", StringComparison.Ordinal);
    }

    private void AddTagInternal(TagDefinition tag)
    {
        var item = new TagItem(tag);
        lock (_tagsGate) Tags.Add(item);
        // 为图表绑定一个 ObservableValue
        var ov = new ObservableValue(0);
        _chartValues[tag.Id] = ov;
        ChartSeries.Add(new LineSeries<ObservableValue> { Values = new[] { ov }, Name = tag.Name, Fill = null });
    }

    /// <summary>Tag 增删改后统一入口：持久化到当前协议 + 运行中实时同步采集引擎（MQTT 等订阅型驱动立即生效）。</summary>
    private void SaveTagsAndSyncEngine(string driverType)
    {
        _configStore.SaveTags(Tags.Select(t => t.Definition).ToList(), driverType);
        if (_deviceConfig is not null)
            _engine.RegisterDevice(_deviceConfig, Tags.Select(t => t.Definition).ToList());
    }

    private async Task WriteAsync()
    {
        if (WriteTargetTag is null || string.IsNullOrWhiteSpace(WriteValue)) return;
        try
        {
            var ok = await _engine.WriteTagAsync(_deviceId, WriteTargetTag.Definition, WriteValue).ConfigureAwait(false);
            if (ok)
            {
                PushNotice($"写值成功：{WriteTargetTag.Name} = {WriteValue}", NoticeLevel.Success);
                Log.Information("写值成功：{Name} = {Value}", WriteTargetTag.Name, WriteValue);
            }
            else
            {
                PushNotice($"写值失败：{WriteTargetTag.Name}（未连接或类型不支持）", NoticeLevel.Warning);
                Log.Warning("写值失败：{Name}（未连接或类型不支持）", WriteTargetTag.Name);
            }
        }
        catch (Exception ex)
        {
            PushNotice($"写值异常：{ex.Message}", NoticeLevel.Error);
            Log.Error(ex, "写值异常 {Name}={Value}", WriteTargetTag.Name, WriteValue);
        }
    }

    /// <summary>删除表格中选中的 Tag（并同步移除图表曲线）。运行时删除需重新连接后才对采集生效。</summary>
    private void DeleteSelectedTag()
    {
        if (SelectedTag is null) return;
        var tag = SelectedTag;
        if (EditingTag == tag) EditingTag = null; // 删除正在编辑的行时退出编辑态
        lock (_tagsGate) Tags.Remove(tag);
        _chartValues.Remove(tag.Id);
        var series = ChartSeries.FirstOrDefault(s => s.Name == tag.Name);
        if (series is not null) ChartSeries.Remove(series);
        if (WriteTargetTag == tag) WriteTargetTag = null;
        if (HistoryTag == tag) HistoryTag = null;
        SaveTagsAndSyncEngine(DriverType);
        PushNotice($"已删除 Tag：{tag.Name}");
        Log.Information("已删除 Tag：{Name}", tag.Name);
    }

    private async Task ConsumeValuesAsync()
    {
        try
        {
            await foreach (var tv in _engine.TagValueReader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    TagItem? tag;
                    lock (_tagsGate) tag = Tags.FirstOrDefault(t => t.Id == tv.TagId);
                    if (tag is null) continue;
                    tag.SetCurrent(tv.Value?.ToString() ?? "-", tv.Quality.ToString());
                    _store.Save(tv);
                    // 更新图表（仅数值型）
                    if (_chartValues.TryGetValue(tv.TagId, out var ov) && tv.Value is not null)
                    {
                        if (double.TryParse(tv.Value.ToString(), out var d))
                        {
                            App.Current.Dispatcher.Invoke(() => ov.Value = d);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 单个 TagValue 处理失败不终止循环，避免 UI 永久停止刷新
                    Log.Warning(ex, "处理 TagValue {TagId} 失败（已跳过）", tv.TagId);
                }
            }
        }
        catch (Exception ex)
        {
            // 防止后台消费循环因异常静默退出，导致 UI 永久不刷新
            Log.Error(ex, "值消费循环异常退出，界面将不再刷新 Tag 值");
            PushNotice($"值消费异常：{ex.Message}", NoticeLevel.Error);
        }
    }

    /// <summary>查询历史曲线：从 SQLite 读取最近 N 分钟的采样点并绘制。</summary>
    private async Task LoadHistoryAsync()
    {
        if (HistoryTag is null)
        {
            HistoryStatus = "请先选择要查询的 Tag";
            return;
        }
        var tag = HistoryTag;
        HistoryStatus = "查询中...";
        var from = DateTimeOffset.Now.AddMinutes(-HistoryMinutes);
        var to = DateTimeOffset.Now;
        try
        {
            var points = await Task.Run(() =>
            {
                var list = new List<ObservablePoint>();
                foreach (var (ts, v) in _store.QueryRange(tag.Id, from, to))
                    list.Add(new ObservablePoint(ts.ToUnixTimeMilliseconds(), v));
                return list;
            }).ConfigureAwait(false);

            App.Current.Dispatcher.Invoke(() =>
            {
                HistorySeries.Clear();
                HistorySeries.Add(new LineSeries<ObservablePoint>
                {
                    Values = points,
                    Name = tag.Name,
                    Fill = null,
                    GeometrySize = 0
                });
                _lastHistoryPoints = points;
                _lastHistoryTagName = tag.Name;
                HistoryStatus = points.Count == 0
                    ? "该时间范围内无数据（Bool 类型无历史曲线）"
                    : BuildHistorySummary(points);
            });
            Log.Information("历史曲线查询：{Name} 最近 {Minutes} 分钟，{Count} 个点", tag.Name, HistoryMinutes, points.Count);
        }
        catch (Exception ex)
        {
            HistoryStatus = $"查询失败：{ex.Message}";
            Log.Error(ex, "历史曲线查询失败");
        }
    }

    /// <summary>生成查询结果的统计摘要（点数 / 最小 / 最大 / 平均）。</summary>
    private static string BuildHistorySummary(List<ObservablePoint> points)
    {
        var vals = points.Select(p => p.Y ?? 0d).ToList();
        return $"共 {points.Count} 点 · 最小 {vals.Min():F3} · 最大 {vals.Max():F3} · 平均 {vals.Average():F3}";
    }

    private void ExportHistoryCsv()
    {
        if (_lastHistoryPoints is null || _lastHistoryPoints.Count == 0)
        {
            HistoryStatus = "请先执行一次历史查询再导出";
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "导出历史数据",
            FileName = $"{_lastHistoryTagName ?? "history"}_历史数据.csv",
            Filter = "CSV 文件 (*.csv)|*.csv",
            DefaultExt = ".csv"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var sw = new StreamWriter(dlg.FileName, false, Encoding.UTF8);
            sw.WriteLine("时间,值");
            foreach (var p in _lastHistoryPoints)
                sw.WriteLine($"{DateTimeOffset.FromUnixTimeMilliseconds((long)(p.X ?? 0)):yyyy-MM-dd HH:mm:ss.fff},{p.Y}");
            var count = _lastHistoryPoints.Count;
            HistoryStatus = $"已导出 {count} 点到 {Path.GetFileName(dlg.FileName)}";
            Log.Information("历史数据导出：{File}，{Count} 点", dlg.FileName, count);
        }
        catch (Exception ex)
        {
            HistoryStatus = $"导出失败：{ex.Message}";
            Log.Error(ex, "历史数据导出失败");
        }
    }

    // ==================== 配置导出 / 导入（JSON 备份与迁移） ====================

    /// <summary>把当前"驱动 + 连接参数 + 轮询间隔 + 该协议 Tag"导出为 JSON 文件。</summary>
    private void ExportDeviceProfile()
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出设备配置（驱动参数 + 整组 Tag）",
            FileName = $"设备配置_{DriverType}_{DateTime.Now:yyyyMMdd_HHmmss}.json",
            Filter = "JSON 配置 (*.json)|*.json",
            DefaultExt = ".json"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var profile = new DeviceProfileDto
            {
                DriverType = DriverType,
                PollIntervalMs = PollIntervalMs,
                Params = ConnectionParams.ToDictionary(p => p.Key, p => p.Value.Trim()),
                Tags = Tags.Select(t => new TagProfileDto
                {
                    Name = t.Name,
                    DataType = t.DataType.ToString(),
                    Address = t.Address,
                    Length = t.Length,
                    HighAlarm = t.Definition.HighAlarm,
                    LowAlarm = t.Definition.LowAlarm,
                    WriteAddress = t.WriteAddressText.Length > 0 ? t.WriteAddressText : null,
                }).ToList(),
            };
            DeviceProfileFile.Save(dlg.FileName, profile);
            PushNotice($"已导出配置：{DriverType}，共 {profile.Tags.Count} 个 Tag → {Path.GetFileName(dlg.FileName)}", NoticeLevel.Success);
            Log.Information("导出配置：{File}（{Driver}，Tag {N}）", dlg.FileName, DriverType, profile.Tags.Count);
        }
        catch (Exception ex)
        {
            PushNotice($"导出失败：{ex.Message}", NoticeLevel.Error);
            Log.Error(ex, "导出配置失败");
        }
    }

    /// <summary>从 JSON 文件恢复整套配置：驱动 / 连接参数 / 轮询间隔 / Tag（导入前需先停止采集）。</summary>
    private void ImportDeviceProfile()
    {
        if (_collecting)
        {
            PushNotice("采集运行中：请先点击“停止”再导入配置，避免连接与配置不一致", NoticeLevel.Warning);
            return;
        }
        var dlg = new OpenFileDialog
        {
            Title = "导入设备配置（JSON）",
            Filter = "JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            DefaultExt = ".json"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var profile = DeviceProfileFile.Load(dlg.FileName, out var err);
            if (profile is null)
            {
                PushNotice($"导入失败：{err}", NoticeLevel.Error);
                return;
            }
            ApplyDeviceProfile(profile);
            PushNotice($"已导入配置：{profile.DriverType}，共 {profile.Tags.Count} 个 Tag", NoticeLevel.Success);
            Log.Information("导入配置：{File} → {Driver}，Tag {N}", Path.GetFileName(dlg.FileName), profile.DriverType, profile.Tags.Count);
        }
        catch (Exception ex)
        {
            PushNotice($"导入失败：{ex.Message}", NoticeLevel.Error);
            Log.Error(ex, "导入配置失败");
        }
    }

    /// <summary>应用配置文件到 UI（先完整校验再落状态，失败时不污染当前配置）。</summary>
    private void ApplyDeviceProfile(DeviceProfileDto profile)
    {
        // 0) 校验驱动
        if (!DriverFactory.SupportedTypes.Contains(profile.DriverType, StringComparer.Ordinal))
            throw new FormatException($"不支持的驱动类型：{profile.DriverType}");

        // 1) 先解析 Tag 并逐项校验（失败抛异常，什么都不改动）
        var defs = new List<TagDefinition>();
        foreach (var tp in profile.Tags ?? new List<TagProfileDto>())
        {
            if (!Enum.TryParse<TagDataType>(tp.DataType, ignoreCase: true, out var type))
                throw new FormatException($"Tag「{tp.Name ?? "(无名)"}」的数据类型 '{tp.DataType}' 无效");
            if (string.IsNullOrWhiteSpace(tp.Address))
                throw new FormatException($"Tag「{tp.Name ?? "(无名)"}」缺少地址");
            var length = (ushort)Math.Clamp(tp.Length, 1, ushort.MaxValue);
            defs.Add(TagDefinition.Create(
                tp.Name, type, _deviceId, tp.Address.Trim(),
                tp.HighAlarm, tp.LowAlarm, length,
                string.IsNullOrWhiteSpace(tp.WriteAddress) ? null : tp.WriteAddress.Trim()));
        }

        // 2) 切换到配置的协议（触发重建动态表单；临时抑制其内部的 Tag 加载，稍后整体覆盖）
        PollIntervalMs = Math.Max(100, profile.PollIntervalMs);
        var cfg = new DeviceConfig(_deviceId, "设备1", profile.DriverType, PollIntervalMs,
            new Dictionary<string, string>(profile.Params ?? new Dictionary<string, string>(), StringComparer.Ordinal));
        if (!string.Equals(DriverType, profile.DriverType, StringComparison.Ordinal))
        {
            _suppressDriverChangeTagLoad = true;
            try { DriverType = profile.DriverType; }
            finally { _suppressDriverChangeTagLoad = false; }
        }
        RestoreConnectionParams(cfg);

        // 3) 整体替换 Tag 列表与图表（退出编辑态、清空旧引用）
        EditingTag = null;
        SelectedTag = null;
        WriteTargetTag = null;
        HistoryTag = null;
        lock (_tagsGate) Tags.Clear();
        _chartValues.Clear();
        ChartSeries.Clear();
        foreach (var d in defs) AddTagInternal(d);

        // 4) 持久化（下次启动自动恢复）
        _configStore.SaveDevice(cfg);
        _configStore.SaveTags(defs, DriverType);
    }

    // ==================== 报文监控：过滤 / Δt / 导出 / 复制 ====================

    /// <summary>是否有任意过滤条件生效。</summary>
    private bool IsTrafficFilterActive =>
        !string.Equals(TrafficDirectionFilter, TrafficDirectionAny, StringComparison.Ordinal)
        || !string.Equals(TrafficDriverFilter, TrafficDriverAny, StringComparison.Ordinal)
        || !string.IsNullOrWhiteSpace(TrafficFilterText);

    /// <summary>过滤条件变化：挂上/摘除过滤谓词并刷新（谓词为 null 时列表直连，零开销）。</summary>
    private void ApplyTrafficFilter()
    {
        TrafficView.Filter = IsTrafficFilterActive ? TrafficFilterPredicate : null;
        UpdateTrafficStatus();
    }

    private bool TrafficFilterPredicate(object item)
    {
        if (item is not TrafficEntry e) return false;
        if (!string.Equals(TrafficDirectionFilter, TrafficDirectionAny, StringComparison.Ordinal)
            && !string.Equals(e.Direction, TrafficDirectionFilter, StringComparison.Ordinal))
            return false;
        if (!string.Equals(TrafficDriverFilter, TrafficDriverAny, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(e.DriverType, TrafficDriverFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        var kw = TrafficFilterText?.Trim();
        if (string.IsNullOrEmpty(kw)) return true;
        if (e.Description.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.DriverType.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.PayloadText.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.FullAscii.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        // Hex 关键字：支持带空格（01 03）与不带空格（0103）两种写法
        if (e.FullHex.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        if (kw.All(Uri.IsHexDigit) && e.FullHex.Replace(" ", "").Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void ResetTrafficFilter()
    {
        TrafficDirectionFilter = TrafficDirectionAny;
        TrafficDriverFilter = TrafficDriverAny;
        TrafficFilterText = "";
        ApplyTrafficFilter();
        PushNotice("已清除报文过滤条件");
    }

    /// <summary>后台线程消费引擎转发的报文帧，放入待刷新队列（高频来源，不在 UI 线程逐条处理）。</summary>
    private async Task ConsumeTrafficAsync()
    {
        try
        {
            await foreach (var f in _engine.TrafficReader.ReadAllAsync().ConfigureAwait(false))
            {
                _trafficQueue.Enqueue(f);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "报文通道读取异常终止");
        }
    }

    /// <summary>UI 定时器回调：把队列里的报文批量转成 <see cref="TrafficEntry"/> 并插入表格顶部（保留上限）。
    /// 计算每帧相对上一帧的 Δt（时间锚仅在真正入表的帧上推进，暂停丢弃的帧不污染锚点）。
    /// 有过滤条件时用 DeferRefresh 包住整批插入，避免逐条触发全表重过滤。</summary>
    private void DrainTrafficToUi()
    {
        var hasFilter = TrafficView.Filter is not null;
        IDisposable? defer = null;
        try
        {
            if (_trafficQueue.IsEmpty)
            {
                UpdateTrafficStatus();
                return;
            }

            var batch = new List<TrafficEntry>(Math.Min(_trafficQueue.Count, 64));
            while (_trafficQueue.TryDequeue(out var frame))
            {
                if (TrafficPaused) continue;   // 暂停：丢弃新到报文（定格观察旧流量）
                double? delta = null;
                if (_lastTrafficTs.HasValue)
                    delta = (frame.Timestamp - _lastTrafficTs.Value).TotalMilliseconds;
                _lastTrafficTs = frame.Timestamp;
                batch.Add(TrafficEntry.FromFrame(frame, delta));
            }

            if (batch.Count > 0)
            {
                if (hasFilter) defer = TrafficView.DeferRefresh();
                // 最新在上：后插入的排前面，故倒序插入头部
                for (int i = batch.Count - 1; i >= 0; i--) TrafficEntries.Insert(0, batch[i]);
                while (TrafficEntries.Count > TrafficMaxEntries) TrafficEntries.RemoveAt(TrafficEntries.Count - 1);
            }
        }
        finally
        {
            defer?.Dispose();
        }
        UpdateTrafficStatus();
    }

    /// <summary>刷新报文监控状态行文案（计数跟随过滤视图）。</summary>
    private void UpdateTrafficStatus()
    {
        var shown = TrafficView.Count;
        var total = TrafficEntries.Count;
        var filtering = TrafficView.Filter is not null;
        TrafficStatus = TrafficPaused
            ? $"已暂停（新报文将丢弃）· 显示 {shown} / {total} 条"
            : filtering
                ? $"实时捕获中 · 显示 {shown} / {total} 条（已按条件过滤）"
                : $"实时捕获中 · 已显示 {shown} / {TrafficMaxEntries} 条";
    }

    private void ClearTraffic()
    {
        TrafficEntries.Clear();
        while (_trafficQueue.TryDequeue(out _)) { }
        _lastTrafficTs = null; // 清空后首帧重新从 "-" 起算 Δt
        UpdateTrafficStatus();
    }

    partial void OnTrafficPausedChanged(bool value)
    {
        UpdateTrafficStatus();
    }

    /// <summary>导出当前可见报文（受过滤条件影响）：按扩展名区分 CSV / TXT / HEX。</summary>
    private void ExportTraffic()
    {
        var rows = TrafficView.Cast<TrafficEntry>().ToList();
        if (rows.Count == 0)
        {
            PushNotice("当前列表为空，没有可导出的报文", NoticeLevel.Warning);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "导出报文监控（当前可见列表）",
            FileName = $"报文监控_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            Filter = "CSV 文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt|Hex 文件 (*.hex)|*.hex",
            DefaultExt = ".csv"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var sw = new StreamWriter(dlg.FileName, false, Encoding.UTF8);
            switch (Path.GetExtension(dlg.FileName).ToLowerInvariant())
            {
                case ".csv":
                    sw.WriteLine("时间,方向,驱动,操作/对象,Δt(ms),长度(字节),载荷Hex,载荷ASCII");
                    foreach (var e in rows)
                        sw.WriteLine(string.Join(",",
                            CsvField(e.Timestamp), CsvField(e.Direction), CsvField(e.DriverType),
                            CsvField(e.Description), CsvField(e.DeltaText), CsvField(e.Length.ToString()),
                            CsvField(e.FullHex), CsvField(e.FullAscii)));
                    break;
                case ".hex":
                    foreach (var e in rows)
                        sw.WriteLine($"{e.Timestamp}\t{e.Direction}\t{e.DriverType}\t{e.Description}\t{e.FullHex}");
                    break;
                default: // .txt
                    sw.WriteLine("时间\t方向\t驱动\t操作/对象\tΔt(ms)\t长度(字节)\t载荷Hex\t载荷ASCII");
                    foreach (var e in rows)
                        sw.WriteLine($"{e.Timestamp}\t{e.Direction}\t{e.DriverType}\t{e.Description}\t{e.DeltaText}\t{e.Length}\t{e.FullHex}\t{e.FullAscii}");
                    break;
            }
            PushNotice($"已导出 {rows.Count} 条报文 → {Path.GetFileName(dlg.FileName)}", NoticeLevel.Success);
            Log.Information("报文导出：{File}，{Count} 条", dlg.FileName, rows.Count);
        }
        catch (Exception ex)
        {
            PushNotice($"导出失败：{ex.Message}", NoticeLevel.Error);
            Log.Error(ex, "报文导出失败");
        }
    }

    /// <summary>CSV 字段转义：含逗号/引号/换行时用双引号包裹并转义内部引号。</summary>
    private static string CsvField(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>右键复制选中报文的 HEX / ASCII 到剪贴板。</summary>
    private void CopyTrafficPayload(TrafficEntry? entry, bool ascii)
    {
        if (entry is null) return;
        var text = ascii ? entry.FullAscii : entry.FullHex;
        if (string.IsNullOrEmpty(text))
        {
            PushNotice("该报文无载荷内容，无内容可复制", NoticeLevel.Warning);
            return;
        }
        try
        {
            Clipboard.SetText(text);
            var kind = ascii ? "ASCII" : "HEX";
            PushNotice($"已复制 {entry.Length} 字节的 {kind}（{text.Length} 字符）", NoticeLevel.Success);
            Log.Information("复制报文 {Kind}：{Desc} {Len}B", kind, entry.Description, entry.Length);
        }
        catch (Exception ex)
        {
            PushNotice($"复制失败：{ex.Message}", NoticeLevel.Error);
            Log.Error(ex, "复制报文失败");
        }
    }

    private async Task ConsumeAlarmsAsync()
    {
        await foreach (var a in _engine.AlarmReader.ReadAllAsync().ConfigureAwait(false))
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                Alarms.Insert(0, a);
                while (Alarms.Count > 1000) Alarms.RemoveAt(Alarms.Count - 1);
            });
            Log.Warning("报警：{Msg} 值={Val}", a.Message, a.Value);
        }
    }

    public void Dispose()
    {
        _trafficTimer?.Stop();
        _engine.Dispose();
        _store.Dispose();
        _configStore.Dispose();
    }
}
