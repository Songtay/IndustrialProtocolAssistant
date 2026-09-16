using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Windows.Controls;

namespace IndustrialProtocolAssistant.UI.Views;

public partial class MonitorTabView : UserControl
{
    public MonitorTabView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // 让两个图表的悬浮提示使用中文字体（原 MainWindow.OnContentRendered 逻辑随拆分迁移至此）
            var paint = new SolidColorPaint(SKColors.Black) { FontFamily = "Microsoft YaHei" };
            RealTimeChart.TooltipTextPaint = paint;
            HistoryChart.TooltipTextPaint = paint;
        };
    }
}
