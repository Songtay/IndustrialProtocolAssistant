using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IndustrialProtocolAssistant.Core;

namespace IndustrialProtocolAssistant.UI;

/// <summary>
/// 动态表单中的一行连接参数：由 ConnectionField 元数据 + 当前输入值组成。
/// 驱动切换时 MainViewModel 重建整个集合，XAML 按 Kind 选择控件类型。
/// </summary>
public partial class ConnectionParamItem : ObservableObject
{
    public ConnectionField Field { get; }

    public string Key => Field.Key;
    public string Label => Field.Label;
    public ConnectionFieldKind Kind => Field.Kind;
    public bool IsRequired => Field.Required;
    public string? ToolTip => Field.ToolTip;
    public bool IsSelectable => Field.Kind is ConnectionFieldKind.Select or ConnectionFieldKind.SerialPort;
    public bool IsSelect => Field.Kind == ConnectionFieldKind.Select;
    public bool IsSerialPort => Field.Kind == ConnectionFieldKind.SerialPort;
    public bool IsPassword => Field.Kind == ConnectionFieldKind.Password;
    public bool IsPlainText => Field.Kind is ConnectionFieldKind.Text or ConnectionFieldKind.Int;
    public bool IsNumeric => Field.Kind == ConnectionFieldKind.Int;
    public int Width => Field.Width;

    /// <summary>下拉候选（Select 用固定 Options，SerialPort 用系统枚举并支持刷新）。</summary>
    public ObservableCollection<string> Options { get; } = [];

    [ObservableProperty] private string _value = "";

    public ConnectionParamItem(ConnectionField field)
    {
        Field = field;
        Value = field.DefaultValue ?? "";
        if (field.Options is not null)
            foreach (var o in field.Options) Options.Add(o);
    }
}
