using Serilog.Core;
using Serilog.Events;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace IndustrialProtocolAssistant.UI;

/// <summary>
/// Serilog 内存日志接收器：把日志实时收集到 UI 可绑定的集合（顶部"日志"选项卡展示）。
/// 最新日志在最上面，最多保留 <see cref="MaxEntries"/> 条。
/// </summary>
public sealed class LogViewerSink : ILogEventSink
{
    public const int MaxEntries = 1000;

    /// <summary>全局单例，App 启动时注册到 Serilog。</summary>
    public static readonly LogViewerSink Instance = new();

    /// <summary>UI 绑定的日志集合（只在 UI 线程修改）。</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    private readonly Dispatcher? _dispatcher;

    private LogViewerSink()
    {
        _dispatcher = Application.Current?.Dispatcher;
    }

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry
        {
            Timestamp = logEvent.Timestamp.ToString("HH:mm:ss.fff"),
            Level = logEvent.Level.ToString(),
            Message = logEvent.RenderMessage(),
        };

        var dispatcher = _dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // 日志可能来自后台线程，统一切到 UI 线程追加
            dispatcher.BeginInvoke(() => Add(entry));
        }
        else
        {
            Add(entry);
        }
    }

    private void Add(LogEntry entry)
    {
        Entries.Insert(0, entry);
        while (Entries.Count > MaxEntries)
            Entries.RemoveAt(Entries.Count - 1);
    }
}
