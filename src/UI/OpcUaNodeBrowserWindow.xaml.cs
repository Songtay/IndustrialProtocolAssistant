using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IndustrialProtocolAssistant.Core;
using IndustrialProtocolAssistant.Drivers.Driver;

namespace IndustrialProtocolAssistant.UI;

/// <summary>节点树中的一项（懒加载：首次展开时才从服务器加载子节点）。
/// 有子节点的节点会预置一个"占位子项"，强制 TreeView 显示展开箭头；
/// 首次展开时由 LoadChildrenAsync 清除占位项并加载真实子节点。</summary>
public sealed class OpcUaNodeItem
{
    public string NodeId { get; }
    public string Name { get; }
    public string NodeClass { get; }
    public string DataType { get; }
    public string ValueText { get; }
    public bool IsLoaded { get; set; }
    public ObservableCollection<OpcUaNodeItem> Children { get; } = new();

    /// <summary>树节点右侧的灰色说明，如 "Variable · Double"。</summary>
    public string Detail => string.IsNullOrEmpty(DataType) ? NodeClass : $"{NodeClass} · {DataType}";

    public OpcUaNodeItem(OpcUaNodeInfo info)
    {
        NodeId = info.NodeId;
        Name = info.DisplayName;
        NodeClass = info.NodeClass;
        DataType = info.DataType;
        ValueText = info.ValueText;
        // 占位子项：使 Children 非空，TreeView 才会渲染展开箭头
        if (info.HasChildren)
            Children.Add(new OpcUaNodeItem());
    }

    private OpcUaNodeItem()
    {
        NodeId = "";
        Name = "";
        NodeClass = "";
        DataType = "";
        ValueText = "";
    }
}

/// <summary>OPC UA 节点树浏览窗口：独立连接服务器，懒加载地址空间，可把选中节点填入 Tag 地址。</summary>
public partial class OpcUaNodeBrowserWindow : Window
{
    private readonly DeviceConfig _config;
    private readonly Action<string, string, string> _onPickNode;
    private readonly ObservableCollection<OpcUaNodeItem> _rootItems = new();
    private readonly CancellationTokenSource _cts = new();
    private OpcUaDriver? _driver;
    private OpcUaNodeInfo? _currentNode;

    public OpcUaNodeBrowserWindow(DeviceConfig config, Action<string, string, string> onPickNode)
    {
        InitializeComponent();
        _config = config;
        _onPickNode = onPickNode;
        NodeTree.ItemsSource = _rootItems;
        // 节点展开时懒加载子节点（Expanded 是 TreeViewItem 的冒泡路由事件，用 AddHandler 订阅）
        NodeTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnNodeExpanded));
        Loaded += async (_, _) => await ConnectAsync();
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
            var d = _driver;
            _driver = null;
            if (d is not null) _ = d.DisposeAsync();
        };
    }

    private async Task ConnectAsync()
    {
        StatusText.Text = "正在连接...";
        ReconnectButton.IsEnabled = RefreshButton.IsEnabled = false;
        try
        {
            var driver = new OpcUaDriver(_config);
            await driver.ConnectAsync(_cts.Token);
            var old = _driver;
            _driver = driver;
            if (old is not null) await old.DisposeAsync();
            StatusText.Text = "已连接，正在加载节点...";
            await LoadRootAsync();
            StatusText.Text = "已连接";
        }
        catch (OperationCanceledException)
        {
            // 窗口已关闭
        }
        catch (Exception ex)
        {
            StatusText.Text = $"连接失败：{ex.Message}";
        }
        finally
        {
            ReconnectButton.IsEnabled = RefreshButton.IsEnabled = true;
        }
    }

    private async Task LoadRootAsync()
    {
        _rootItems.Clear();
        SetCurrentNode(null);
        var root = new OpcUaNodeItem(new OpcUaNodeInfo
        {
            NodeId = "",
            DisplayName = "Root (ObjectsFolder)",
            NodeClass = "Object",
            HasChildren = true,
        });
        _rootItems.Add(root);
        await LoadChildrenAsync(root);
        NodeTree.UpdateLayout();
        if (NodeTree.ItemContainerGenerator.ContainerFromItem(root) is TreeViewItem tvi)
            tvi.IsExpanded = true;
    }

    private async Task LoadChildrenAsync(OpcUaNodeItem item)
    {
        if (item.IsLoaded) return;
        item.IsLoaded = true;
        item.Children.Clear(); // 移除占位子项，开始真实加载
        try
        {
            var (children, error) = await Task.Run(() =>
            {
                var driver = _driver;
                return driver is null
                    ? (Array.Empty<OpcUaNodeInfo>(), "会话未连接")
                    : (driver.BrowseChildren(item.NodeId, out var err), err);
            });
            if (!string.IsNullOrEmpty(error) && children.Count == 0)
                StatusText.Text = $"加载子节点失败：{error}";
            foreach (var child in children)
                item.Children.Add(new OpcUaNodeItem(child));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载子节点异常：{ex.Message}";
        }
    }

    private void SetCurrentNode(OpcUaNodeInfo? info)
    {
        _currentNode = info;
        UseButton.IsEnabled = info is not null && !string.IsNullOrEmpty(info.NodeId);
        if (info is null)
        {
            DetailName.Text = "未选择节点";
            DetailMeta.Text = "";
            DetailValue.Text = "";
            return;
        }
        DetailName.Text = info.DisplayName;
        DetailMeta.Text = $"{info.NodeClass}{(string.IsNullOrEmpty(info.DataType) ? "" : " · " + info.DataType)}  ·  {info.NodeId}";
        DetailValue.Text = string.IsNullOrEmpty(info.ValueText) ? "" : $"当前值：{info.ValueText}";
    }

    private void PickNode(OpcUaNodeInfo info)
    {
        if (string.IsNullOrEmpty(info.NodeId)) return;
        _onPickNode?.Invoke(info.NodeId, info.DisplayName, info.DataType);
        Close();
    }

    private async void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem tvi && tvi.Header is OpcUaNodeItem item)
            await LoadChildrenAsync(item);
    }

    private void NodeTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OpcUaNodeItem item)
        {
            SetCurrentNode(new OpcUaNodeInfo
            {
                NodeId = item.NodeId,
                DisplayName = item.Name,
                NodeClass = item.NodeClass,
                DataType = item.DataType,
                ValueText = item.ValueText,
            });
        }
        else
        {
            SetCurrentNode(null);
        }
    }

    private void NodeTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NodeTree.SelectedItem is OpcUaNodeItem item && !string.IsNullOrEmpty(item.NodeId))
        {
            PickNode(new OpcUaNodeInfo
            {
                NodeId = item.NodeId,
                DisplayName = item.Name,
                NodeClass = item.NodeClass,
                DataType = item.DataType,
                ValueText = item.ValueText,
            });
        }
    }

    private async void InspectButton_Click(object sender, RoutedEventArgs e)
    {
        var text = InspectBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        StatusText.Text = "正在检查节点...";
        try
        {
            var (info, error) = await Task.Run(() =>
            {
                var driver = _driver;
                return driver is null
                    ? ((OpcUaNodeInfo?)null, "会话未连接")
                    : (driver.ReadNodeInfo(text, out var err), err);
            });
            if (info is null)
                StatusText.Text = $"检查失败:{error}";
            else
            {
                SetCurrentNode(info);
                StatusText.Text = "";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"检查节点异常：{ex.Message}";
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_driver is null) return;
        StatusText.Text = "刷新中...";
        await LoadRootAsync();
        StatusText.Text = "已连接";
    }

    private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        var old = _driver;
        _driver = null;
        if (old is not null) await old.DisposeAsync();
        _rootItems.Clear();
        await ConnectAsync();
    }

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNode is not null) PickNode(_currentNode);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
