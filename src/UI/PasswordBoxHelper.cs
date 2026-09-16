using System.Windows;
using System.Windows.Controls;

namespace IndustrialProtocolAssistant.UI;

/// <summary>
/// PasswordBox.Password 不是依赖属性、无法直接绑定，
/// 通过附加属性在 Password 与 VM 之间双向转接。
/// </summary>
public static class PasswordBoxHelper
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword", typeof(string), typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(string.Empty, OnBoundPasswordChanged));

    public static string GetBoundPassword(DependencyObject d) => (string)d.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox pb) return;
        pb.PasswordChanged -= OnPasswordChanged;
        var next = (string?)e.NewValue ?? "";
        if (pb.Password != next) pb.Password = next;
        pb.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            SetBoundPassword(pb, pb.Password);
    }
}
