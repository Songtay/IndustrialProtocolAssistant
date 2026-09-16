using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace IndustrialProtocolAssistant.UI;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>作者信息栏的链接点击：用系统默认浏览器打开作者 GitHub 主页。</summary>
    private void AuthorLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开链接：{e.Uri.AbsoluteUri}\n{ex.Message}",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        e.Handled = true;
    }
}
