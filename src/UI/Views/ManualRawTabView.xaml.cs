using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IndustrialProtocolAssistant.Acquisition;
using IndustrialProtocolAssistant.Core;

namespace IndustrialProtocolAssistant.UI.Views;

/// <summary>
/// “手动收发”嵌入式面板（串口助手风格）：
/// 对线帧协议（SerialFree / Socket / ModbusRTU / ModbusTCP）任意 Hex 请求 → 立即显示原始应答。
/// 收发通过主窗采集引擎的设备 IO 闸门，与轮询读/写值/单点读互斥，不会应答串帧。
/// S7 / OPC UA / MQTT 为高层语义协议，不支持原始帧，进入本页会禁用发送并给出引导。
/// 引擎/驱动信息实时跟随主 ViewModel：顶部切换驱动或连接状态变化时立即刷新，无需切走再切回。
/// </summary>
public partial class ManualRawTabView : UserControl
{
    private const int MaxLogEntries = 500;

    private static readonly string[] FrameDrivers = { "SerialFree", "Socket", "ModbusRtu", "ModbusTcp" };

    private AcquisitionEngine? _engine;
    private string _deviceId = "dev1";
    private string _driverType = "";
    private readonly ObservableCollection<string> _logs = new();
    private bool _busy;
    private MainViewModel? _vm;

    public ManualRawTabView()
    {
        InitializeComponent();
        SessionLog.ItemsSource = _logs;
        SessionLog_ResetScroll();

        // 进入/离开本页、DataContext 就绪时刷新一次
        IsVisibleChanged += (_, _) => RefreshFromViewModel();
        // 订阅主 VM 的属性变化：顶部切换驱动、引擎启停等无需切走再切回即可实时刷新。
        // TabControl 切换会卸载/重载内容，因此用 Loaded/Unloaded 管理订阅生命周期。
        Loaded += (_, _) => BindViewModel(DataContext as MainViewModel);
        Unloaded += (_, _) => BindViewModel(null);
        DataContextChanged += (_, _) => BindViewModel(DataContext as MainViewModel);
    }

    /// <summary>绑定/解绑主 ViewModel 的属性变化通知（避免重复订阅与内存泄漏）。</summary>
    private void BindViewModel(MainViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelPropertyChanged;
        RefreshFromViewModel();
    }

    /// <summary>顶部驱动下拉切换、连接状态变化时，本页的驱动标签 / 能力提示 / 发送可用性实时跟随。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.DriverType)
            or nameof(MainViewModel.StatusLevel)
            or nameof(MainViewModel.Status)))
            return;

        // 状态可能由引擎回调在后台线程更新，统一切回 UI 线程再刷新控件
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshFromViewModel);
            return;
        }
        RefreshFromViewModel();
    }

    private void RefreshFromViewModel()
    {
        if (!IsVisible || DataContext is not MainViewModel vm) return;
        _engine = vm.Engine;
        _deviceId = vm.DeviceId;
        _driverType = vm.DriverType;
        DriverLabel.Text = _driverType;
        SetHint();
    }

    /// <summary>
    /// 取引擎当前设备的手动收发能力；若引擎挂载的驱动与顶部下拉所选驱动不一致
    /// （切换了驱动但未重新“连接并启动”），按未注册返回 null，避免展示/放行旧驱动的能力。
    /// </summary>
    private ManualRawCapabilities? GetCurrentCapabilities()
    {
        ManualRawCapabilities? caps;
        try
        {
            // 尚未点击“连接并启动”时为 null（驱动未注册），面板内给出引导
            caps = _engine?.GetManualRawCapabilities(_deviceId);
        }
        catch
        {
            // 引擎状态未就绪时忽略，按未注册处理
            return null;
        }

        return caps is not null && string.Equals(caps.DriverType, _driverType, StringComparison.Ordinal)
            ? caps
            : null;
    }

    private void SetHint()
    {
        var frameProtocol = FrameDrivers.Contains(_driverType);
        var caps = GetCurrentCapabilities();

        if (caps is { ManualHint.Length: > 0 })
        {
            HintText.Text = caps.ManualHint;
        }
        else if (frameProtocol)
        {
            HintText.Text = "面板直连采集引擎的当前设备：发送前请先回到“设备连接”区点击“连接并启动”。若已启动仍提示不支持，"
                          + "说明该驱动实例未实现原始帧通道。";
        }
        else
        {
            HintText.Text = "S7 / OPC UA / MQTT 为高层语义协议，不存在可直接手写的“线帧”，请使用主界面的 Tag 读写与单点“读一次”。";
        }

        if (caps?.AutoFrameLabel is { } label)
        {
            AutoFrameCheck.Content = label;
            AutoFrameCheck.IsChecked = caps.AutoFrameDefault;
            AutoFrameCheck.Visibility = Visibility.Visible;
        }
        else
        {
            AutoFrameCheck.Content = "自动补全（当前不可用）";
            AutoFrameCheck.IsChecked = false;
            AutoFrameCheck.Visibility = Visibility.Collapsed;
        }

        // 仅“线帧协议 + 引擎已注册该驱动”可发送：否则禁用发送并在状态行给出引导
        var canSend = frameProtocol && caps is not null;
        SendButton.IsEnabled = canSend;
        if (!frameProtocol)
            SetResultStatus("当前驱动为高层语义协议，请先在顶部切换到线帧驱动（SerialFree / Socket / ModbusRTU / ModbusTCP）再使用本页。", isError: true);
        else if (!canSend)
            SetResultStatus("引擎尚未启动（驱动未注册原始帧通道）：请先点击“连接并启动”。", isError: false);
        else
            SetResultStatus("就绪：输入 Hex 请求帧后回车或点“发送”。", isError: false);
    }

    // ---------- 发送 ----------

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private void RequestHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private async Task SendAsync()
    {
        if (_busy || _engine is null || !FrameDrivers.Contains(_driverType)) return;

        var input = RequestHex.Text ?? "";
        byte[] request;
        try
        {
            request = ParseHex(input);
            if (request.Length == 0) throw new FormatException("请求帧为空，请先输入 Hex");
        }
        catch (Exception ex)
        {
            SetResultStatus($"请求解析失败：{ex.Message}", isError: true);
            return;
        }

        _busy = true;
        SendButton.IsEnabled = false;
        SetResultStatus($"正在发送 {request.Length}B 并等待应答…（轮询读暂停，不影响本轮其他 Tag）", isError: false);
        var sw = Stopwatch.StartNew();
        try
        {
            var applyAuto = AutoFrameCheck.IsChecked == true;
            // 注意：此处不能 ConfigureAwait(false)。本方法由 UI 事件触发，后续要更新 RxHexBox / 日志集合 / 按钮状态，
            // 必须回到 UI 线程；否则 await 之后的续体在线程池线程执行，访问 DependencyObject 会抛
            // "调用线程无法访问此对象，因为另一个线程拥有该对象"。
            var reply = await _engine.SendRawFrameAsync(_deviceId, request, applyAuto);
            sw.Stop();

            // 真实上线帧可能与输入不同（自动补 MBAP 头 / CRC / 设备级帧校验），以返回帧为准展示
            AppendLog($"→ TX  {FormatHex(reply.SentFrame)}");
            if (reply.NoResponse)
            {
                RxHexBox.Text = "";
                RxAsciiBox.Text = "";
                SetResultStatus($"已发出 {reply.SentFrame.Length}B，等待 {sw.ElapsedMilliseconds}ms 无应答（设备无响应 / 未连接）", isError: true);
                AppendLog($"←（无应答，{sw.ElapsedMilliseconds}ms）");
            }
            else
            {
                RxHexBox.Text = FormatHex(reply.ReceivedBytes);
                RxAsciiBox.Text = ToAsciiLabel(reply.ReceivedBytes);
                SetResultStatus($"收到应答 {reply.ReceivedBytes.Length}B，耗时 {sw.ElapsedMilliseconds}ms", isError: false);
                AppendLog($"← RX  {FormatHex(reply.ReceivedBytes)}");
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            SetResultStatus($"发送异常：{ex.Message}（{sw.ElapsedMilliseconds}ms）", isError: true);
            AppendLog($"✕ 异常：{ex.Message}");
        }
        finally
        {
            _busy = false;
            // 可能因驱动/引擎状态被本面板禁用，恢复时重新按能力计算，避免误启用
            SendButton.IsEnabled = FrameDrivers.Contains(_driverType) && GetCurrentCapabilities() is not null;
        }
    }

    private void ClearRequest_Click(object sender, RoutedEventArgs e)
    {
        RequestHex.Text = "";
        RxHexBox.Text = "";
        RxAsciiBox.Text = "";
        ResultStatusText.Text = "已清空";
        RequestHex.Focus();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _logs.Clear();

    // ---------- 小工具 ----------

    private void SetResultStatus(string text, bool isError)
    {
        ResultStatusText.Text = text;
        // 状态底色：错误淡红 / 正常淡蓝
        var parent = ResultStatusText.Parent as Border;
        if (parent is not null)
            parent.Background = new SolidColorBrush(isError
                ? Color.FromRgb(0xFD, 0xEC, 0xEA)
                : Color.FromRgb(0xE8, 0xF3, 0xFD));
    }

    private void AppendLog(string line)
    {
        _logs.Insert(0, $"[{DateTime.Now:HH:mm:ss.fff}] {line}");
        while (_logs.Count > MaxLogEntries) _logs.RemoveAt(_logs.Count - 1);
        SessionLog_ResetScroll();
    }

    /// <summary>最新记录在顶部：内容变化后滚回列表头部，避免停在旧位置误判。</summary>
    private void SessionLog_ResetScroll()
    {
        SessionLog.ScrollIntoView(SessionLog.Items.Count > 0 ? _logs[0] : null);
    }

    private static string FormatHex(byte[] data)
    {
        if (data.Length == 0) return "（空）";
        var sb = new StringBuilder(data.Length * 3);
        foreach (var b in data)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>ASCII 视图：全部为可打印字节时给出文本，否则提示去 Hex 区查看。</summary>
    private static string ToAsciiLabel(byte[] data)
    {
        if (data.Length == 0) return "";
        foreach (var b in data)
        {
            if (b is >= 0x20 and <= 0x7E) continue;
            if (b is (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return $"(第 {Array.IndexOf(data, b) + 1} 字节 {b:X2} 非可打印 ASCII，请在 Hex 区查看)";
        }
        return Encoding.ASCII.GetString(data);
    }

    /// <summary>
    /// 解析 Hex：支持连续串或空格 / 逗号 / 分号 / 换行分隔，单个 token 前的 “0x/0X” 前缀会被忽略（如 0x01 0x02）。
    /// </summary>
    private static byte[] ParseHex(string text)
    {
        var tokens = text.Split([' ', '\t', ',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var hex = new StringBuilder();
        foreach (var token in tokens)
        {
            var t = token.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
            hex.Append(t);
        }
        var s = hex.ToString();
        if (s.Length == 0) return Array.Empty<byte>();
        if (s.Length % 2 != 0)
            throw new FormatException($"十六进制字符数必须为偶数（当前 {s.Length} 个字符，请按字节补 0 前缀）");
        var bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            var two = s.Substring(i * 2, 2);
            if (!byte.TryParse(two, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                throw new FormatException($"第 {i + 1} 个字节 “{two}” 不是有效的十六进制");
            bytes[i] = b;
        }
        return bytes;
    }
}
