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
            await Task.Delay(target == "panel:wallpaper" ? 5000 : 900); // wallpaper thumbnails load in the background
            root = (FrameworkElement)app.Island.Content;
            width = app.Island.ActualWidth; height = app.Island.ActualHeight;
        }
        else
        {
            host = target switch
            {
                "topbar" => new TopBarWindow(app),
                _ when target.StartsWith("dock") => new DockWindow(app, new DockModel(app.Settings, app.Apps)),
                _ when target.StartsWith("launcher") => new LauncherWindow(app),
                _ => throw new ArgumentException($"Unknown snapshot target {target}"),
            };
            if (target.StartsWith("dock") && app.Settings.Desktop.Pins.Count == 0)
            {
                app.Settings.Desktop.Pins = DockModel.DefaultPins(app.Apps);
                ((DockWindow)host).Reload();
            }
            root = (FrameworkElement)host.Content;
            (width, height) = target switch
            {
                "topbar" => (SystemParameters.PrimaryScreenWidth, app.Themes.Current.TopBarHeight),
                _ when target.StartsWith("dock") && app.Themes.Current.DockPosition is "left" or "right" => (160.0, 1100.0),
                _ when target.StartsWith("dock") => (1300.0, (app.Themes.Current.DockIconSize + 50) * app.Themes.Current.DockMagnification + 20),
                _ => (680.0, 520.0),
            };
            // "launcher:slack" pre-types a search.
            if (host is LauncherWindow lw) lw.Prefill(target.Contains(':') ? target[(target.IndexOf(':') + 1)..] : "spot");
            await Task.Delay(600);
        }

        // Lay the content out in place (so bindings to its window still resolve), then paint it
        // over a desktop-ish backdrop so translucency reads like it would on screen.
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        await Task.Delay(300);

        // "dock:0.4" magnifies around 40% of the way along the icons.
        if (host is DockWindow dw && target.Contains(':') && double.TryParse(target[(target.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture, out var hover))
        {
            dw.SimulateHover(hover);
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
        }

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x23, 0x2A, 0x3A)), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, width, height));
        }
        var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        await using (var fs = File.Create(file)) enc.Save(fs);
        Log.Info($"Snapshot {target} -> {file}");
    }
}
