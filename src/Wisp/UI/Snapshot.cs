using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.Core;
using Wisp.Desktop;

namespace Wisp.UI;

/// <summary>
/// Developer aid: renders UI surfaces to PNG without showing or focusing anything, so visuals can be
/// checked (and themes previewed) without touching the live desktop.
///   Wisp.exe --snapshot &lt;target&gt; &lt;out.png&gt; [themeId]
/// targets: panel:&lt;tab&gt;, topbar, dock, launcher
/// </summary>
public static class Snapshot
{
    public static async Task RunAsync(WispController app, string target, string file, string? theme)
    {
        if (theme is not null && app.Themes.Find(theme) is { } pack) app.Themes.Apply(pack);

        FrameworkElement root;
        Window? host = null;
        double width, height;

        if (target.StartsWith("panel:"))
        {
            app.Island.ShowPanelForSnapshot(target["panel:".Length..]);
            await Task.Delay(900);
            root = (FrameworkElement)app.Island.Content;
            width = app.Island.ActualWidth; height = app.Island.ActualHeight;
        }
        else
        {
            host = target switch
            {
                "topbar" => new TopBarWindow(app),
                "dock" => new DockWindow(app, new DockModel(app.Settings) { }),
                "launcher" => new LauncherWindow(app),
                _ => throw new ArgumentException($"Unknown snapshot target {target}"),
            };
            if (target == "dock" && app.Settings.Desktop.Pins.Count == 0)
            {
                app.Settings.Desktop.Pins = DockModel.DefaultPins(app.Apps);
                ((DockWindow)host).Reload();
            }
            root = (FrameworkElement)host.Content;
            host.Content = null;
            (width, height) = target switch
            {
                "topbar" => (SystemParameters.PrimaryScreenWidth, TopBarWindow.BarHeight),
                "dock" => (1100.0, 82.0),
                _ => (680.0, 520.0),
            };
            if (host is LauncherWindow lw) lw.Prefill("spot");
            await Task.Delay(600);
        }

        // Paint on a desktop-ish backdrop so translucency reads like it would on screen.
        var backdrop = new System.Windows.Controls.Grid { Background = new SolidColorBrush(Color.FromRgb(0x23, 0x2A, 0x3A)) };
        if (root.Parent is System.Windows.Controls.Panel p) p.Children.Remove(root);
        else if (root.Parent is Window w) w.Content = null;
        backdrop.Children.Add(root);
        backdrop.Measure(new Size(width, height));
        backdrop.Arrange(new Rect(0, 0, width, height));
        backdrop.UpdateLayout();
        await Task.Delay(300);

        var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(backdrop);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        await using (var fs = File.Create(file)) enc.Save(fs);
        Log.Info($"Snapshot {target} -> {file}");
    }
}
