using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IndustrialProtocolAssistant.UI.Views;

public partial class TrafficTabView : UserControl
{
    public TrafficTabView() => InitializeComponent();

    /// <summary>报文右键菜单打开时：自动选中鼠标所在行，保证"复制 HEX / ASCII"作用于用户点中的那行。</summary>
    private void TrafficContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.DataContext is not MainViewModel vm) return;

        var hit = Mouse.DirectlyOver as DependencyObject;
        while (hit is not null && hit is not DataGridRow)
            hit = VisualTreeHelper.GetParent(hit);

        if (hit is DataGridRow row && row.Item is TrafficEntry entry)
        {
            row.IsSelected = true;
            vm.SelectedTraffic = entry;
        }
    }
}
