using System.Windows;
using Serilog;

namespace IndustrialProtocolAssistant.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File("logs/industrial-.log", rollingInterval: RollingInterval.Day)
            .WriteTo.Sink(LogViewerSink.Instance)
            .CreateLogger();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
