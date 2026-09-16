using CommunityToolkit.Mvvm.ComponentModel;
using IndustrialProtocolAssistant.Core;

namespace IndustrialProtocolAssistant.UI;

/// <summary>
/// Tag 运行时可观察项：包装 TagDefinition，提供 UI 可绑定的实时值/质量。
/// 使用方式：绑定 Name/Address/DataType 取自 Definition，CurrentValue/CurrentQuality 为实时刷新。
/// </summary>
public partial class TagItem : ObservableObject
{
    /// <summary>底层数据定义；支持在"就地编辑"时整体替换（保留 Id 以便图表/历史/写入引用不失效）。</summary>
    public TagDefinition Definition { get; private set; }

    [ObservableProperty] private string _currentValue = "-";
    [ObservableProperty] private string _currentQuality = "-";
    [ObservableProperty] private string _updatedTime = "-";

    public TagItem(TagDefinition definition) => Definition = definition;

    /// <summary>
    /// 「就地编辑」保存：替换底层定义并通知所有派生展示属性刷新。
    /// 调用方需保证 newDefinition.Id 与原 Id 一致（保留图表/历史/写值引用），
    /// 完成后自行调用持久化与采集引擎同步。
    /// </summary>
    public void ApplyDefinition(TagDefinition definition)
    {
        Definition = definition;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(DataType));
        OnPropertyChanged(nameof(Length));
        OnPropertyChanged(nameof(HighAlarmText));
        OnPropertyChanged(nameof(LowAlarmText));
        OnPropertyChanged(nameof(WriteAddressText));
        OnPropertyChanged(nameof(HasWriteAddress));
        OnPropertyChanged(nameof(AreaText));
        OnPropertyChanged(nameof(AreaAddress));
        OnPropertyChanged(nameof(DisplayName));
    }

    public string Name => Definition.Name;
    public string Address => Definition.Address;
    public TagDataType DataType => Definition.DataType;
    public string Id => Definition.Id;
    public ushort Length => Definition.Length;
    public string HighAlarmText => Definition.HighAlarm?.ToString("0.###") ?? "-";
    public string LowAlarmText => Definition.LowAlarm?.ToString("0.###") ?? "-";

    /// <summary>独立发送地址（MQTT 发送 Topic），未配置时为 null/空。</summary>
    public string WriteAddressText => Definition.WriteAddress ?? "";
    public bool HasWriteAddress => !string.IsNullOrWhiteSpace(WriteAddressText);

    /// <summary>是否为 Modbus 数字地址（纯数字）；NodeId / Topic 等字符串地址不适用 Modbus 区域概念。</summary>
    private bool IsNumericAddress => ushort.TryParse(Definition.Address, out _);

    /// <summary>区域/类别描述：根据地址格式推断协议语义，Modbus 显示区名，MQTT 显示 Topic，OPC UA 显示 NodeId，S7 显示数据块/位存储/输入/输出，SerialFree 显示命令帧。</summary>
    public string AreaText
    {
        get
        {
            if (ushort.TryParse(Definition.Address, out _))
                return Definition.DataType == TagDataType.Bool ? "线圈 (Coil)" : "保持寄存器 (Holding)";

            var addr = Definition.Address.Trim();
            if (addr.Contains('/') || addr.Contains('+') || addr.Contains('#'))
                return "Topic";
            if (addr.Contains('=') || addr.StartsWith("ns=", StringComparison.OrdinalIgnoreCase))
                return "NodeId";

            // S7 地址启发式：DBn.xxx → 数据块；M/I/Q 开头 → 位存储/输入/输出
            if (addr.Length > 2 && addr.StartsWith("DB", StringComparison.OrdinalIgnoreCase) && char.IsDigit(addr[2]))
                return "数据块";
            if (addr.Length > 0)
            {
                var head = addr[0];
                if (head is 'M' or 'm') return "位存储";
                if (head is 'I' or 'i') return "输入";
                if (head is 'Q' or 'q') return "输出";
            }

            // 串口自由协议命令帧启发式：由 hex 字节 / 占位符组成，含空格、逗号、|| 或 @偏移
            if (LooksLikeCommandFrame(addr))
                return "命令帧";
            return "地址";
        }
    }

    private static bool LooksLikeCommandFrame(string addr)
    {
        if (!addr.Contains(' ') && !addr.Contains(',') && !addr.Contains("||") && !addr.Contains('@'))
            return false;
        foreach (var token in addr.Split([' ', ',', '|', '@'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 3 && token.StartsWith('{') && token.EndsWith('}'))
                continue;                          // 占位符，如 {value} / {crc16}
            if (token.Length == 2 && token.All(Uri.IsHexDigit))
                continue;                          // 1 字节 hex
            if (token.All(char.IsDigit))
                continue;                          // @ 后的偏移数字
            return false;
        }
        return true;
    }

    /// <summary>地址显示：Modbus 按区域加前缀（线圈 0x、保持寄存器 4x）；其余协议原样显示地址字符串。</summary>
    public string AreaAddress => IsNumericAddress
        ? Definition.DataType == TagDataType.Bool ? $"0x{Definition.Address}" : $"4x{Definition.Address}"
        : Definition.Address;

    /// <summary>下拉列表显示文本：名称 [地址 · 类型]</summary>
    public string DisplayName => $"{Name} [{Address} · {DataType}]";

    public void SetCurrent(string value, string quality)
    {
        CurrentValue = value;
        CurrentQuality = quality;
        UpdatedTime = DateTime.Now.ToString("HH:mm:ss");
    }
}
