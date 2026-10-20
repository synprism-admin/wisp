using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Wisp.UI;

/// <summary>A themed yes/no notice (privacy, system-change warnings). Lines starting with "• " render as bullets.</summary>
public partial class NoticeWindow : Window
{
    string? _link;

    NoticeWindow(string title, string body, string primary, string secondary, string? linkText, string? link)
    {
        InitializeComponent();
        TitleText.Text = title;
        foreach (var para in body.Split("\n\n"))
        {
            foreach (var line in para.Split('\n'))
            {
                bool bullet = line.StartsWith("• ");
                Body.Children.Add(new TextBlock
                {
                    Text = line,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                    Margin = new Thickness(bullet ? 8 : 0, 0, 0, bullet ? 3 : 0),
                    Foreground = (System.Windows.Media.Brush)FindResource(bullet ? "Text" : "TextDim"),
                });
            }
            Body.Children.Add(new Border { Height = 10 });
        }
        PrimaryButton.Content = primary;
        SecondaryButton.Content = secondary;
        if (linkText is not null && link is not null)
        {
            _link = link;
            LinkText.Text = linkText;
            LinkButton.Visibility = Visibility.Visible;
        }
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
    }

    /// <summary>Shows the notice; true if the user chose the primary button.</summary>
    public static bool Ask(string title, string body, string primary, string secondary, string? linkText = null, string? link = null)
    {
        var w = new NoticeWindow(title, body, primary, secondary, linkText, link);
        return w.ShowDialog() == true;
    }

    void Primary_Click(object s, RoutedEventArgs e) => DialogResult = true;
    void Secondary_Click(object s, RoutedEventArgs e) => DialogResult = false;
    void Header_Drag(object s, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }

    void Link_Click(object s, RoutedEventArgs e)
    {
        if (_link is null) return;
        try { Process.Start(new ProcessStartInfo(_link) { UseShellExecute = true }); } catch { }
    }
}
