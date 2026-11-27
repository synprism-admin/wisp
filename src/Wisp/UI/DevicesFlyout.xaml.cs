using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Wisp.Core;

namespace Wisp.UI;

/// <summary>
/// Drops down from the device batteries in the top bar: every paired device that reports a battery,
/// connected ones first, with a bar per device and when a disconnected one last checked in.
/// </summary>
public partial class DevicesFlyout : UserControl
{
    readonly WispController _app;

    public DevicesFlyout(WispController app)
    {
        _app = app;
        InitializeComponent();
    }

    /// <summary>Refreshes the list (called as it opens and whenever the devices change).</summary>
    public void Show()
    {
        var all = _app.Devices.All;
        EmptyText.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Rows.Children.Clear();
        foreach (var d in all) Rows.Children.Add(Row(d));
        Updated.Text = _app.Devices.Scanned is { } t ? $"Checked {t:t}" : "";
    }

    static FrameworkElement Row(BatteryDevice d)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12), Opacity = d.Connected ? 1 : 0.55 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var icon = new TextBlock { Text = DeviceBatteries.Glyph(d.Kind), FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 12, 0) };
        icon.SetResourceReference(StyleProperty, "Icon");
        icon.SetResourceReference(TextBlock.ForegroundProperty, d.Low && d.Connected ? "Danger" : "Accent");
        Grid.SetRowSpan(icon, 2);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = d.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var status = new TextBlock { Text = Status(d), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        text.Children.Add(status);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var pct = new TextBlock { Text = $"{d.Percent}%", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        pct.SetResourceReference(TextBlock.ForegroundProperty, d.Low && d.Connected ? "Danger" : "Text");
        Grid.SetColumn(pct, 2);
        grid.Children.Add(pct);

        // The level as a thin bar under the name.
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 6, 0, 0) };
        track.SetResourceReference(Border.BackgroundProperty, "Hairline");
        var fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
        fill.SetResourceReference(Border.BackgroundProperty, d.Low && d.Connected ? "Danger" : "Accent");
        track.Child = fill;
        track.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * d.Percent / 100.0;
        Grid.SetRow(track, 1);
        Grid.SetColumn(track, 1);
        Grid.SetColumnSpan(track, 2);
        grid.Children.Add(track);
        return grid;
    }

    static string Status(BatteryDevice d)
    {
        if (d.Connected) return d.Charging ? "Connected · charging" : d.Low ? "Connected · battery low" : "Connected";
        if (d.Reported is not { } r) return "Not connected";
        var when = r.Date == DateTime.Today ? $"today {r:t}" : r.Date == DateTime.Today.AddDays(-1) ? "yesterday" : r.ToString("MMM d");
        return $"Not connected · last reported {when}";
    }

    void Settings_Click(object s, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });

    async void Refresh_Click(object s, RoutedEventArgs e)
    {
        await _app.Devices.RefreshAsync();
        Show();
    }
}
