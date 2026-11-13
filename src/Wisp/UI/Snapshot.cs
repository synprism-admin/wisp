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

        // "live:rain:8" shows the real live desktop for 8 seconds and captures the desktop behind the windows.
        if (target.StartsWith("live:"))
        {
            var parts = target.Split(':');
            var sky = Enum.Parse<Sky>(parts[1], ignoreCase: true);
            int seconds = parts.Length > 2 ? int.Parse(parts[2]) : 8;
            app.Live.AllowInSnapshot = true;
            app.Live.Preview = sky;
            await Task.Delay(1500);
            CaptureDesktop(file);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, seconds - 1.5)));
            app.Live.Preview = null;
            Log.Info($"Snapshot {target} -> {file}");
            return;
        }

        // "effect:snow" renders an effect over the current wallpaper offscreen.
        if (target.StartsWith("effect:"))
        {
            var sky = Enum.Parse<Sky>(target["effect:".Length..], ignoreCase: true);
            var (path, _, bg) = Wallpapers.CurrentFor(System.Windows.Forms.Screen.PrimaryScreen!.DeviceName);
            var grid = new System.Windows.Controls.Grid { Background = new SolidColorBrush(bg), ClipToBounds = true };
            if (path is not null && File.Exists(path))
                grid.Children.Add(new System.Windows.Controls.Image { Source = new BitmapImage(new Uri(path)), Stretch = Stretch.UniformToFill });
            grid.Children.Add(new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromRgb(5, 8, 18)), Opacity = WeatherScene.Gloom(sky) });
            var scene = new WeatherScene();
            grid.Children.Add(scene);
            grid.Measure(new Size(1280, 720));
            grid.Arrange(new Rect(0, 0, 1280, 720));
            grid.UpdateLayout();
            scene.SetSky(sky, 12, 250);
            scene.Stop();
            for (int f = 0; f < 90; f++) scene.Step(1 / 60.0);
            scene.SetSky(sky, 12, 250); // reseeds; settle again so the frame looks mid-shower
            scene.Stop();
            for (int f = 0; f < 90; f++) scene.Step(1 / 60.0);
            scene.Render();
            var bmp = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(grid);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            await using (var fs = File.Create(file)) png.Save(fs);
            Log.Info($"Snapshot {target} -> {file}");
            return;
        }

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
                "settings" => new SettingsWindow(app),
                "calendar" => new Window { Content = new CalendarFlyout(app) },
                "weather" => new Window { Content = new WeatherFlyout(app) },
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
                "settings" => (640.0, 760.0),
                "calendar" => (300.0, 640.0),
                "weather" => (330.0, 470.0),
                _ => (680.0, 520.0),
            };
            // "launcher:slack" pre-types a search.
            if (host.Content is CalendarFlyout cal) cal.Open();
            if (host.Content is WeatherFlyout wf) { app.Weather.UseSample(); wf.Show(); }
            if (target == "topbar") app.Weather.UseSample();
            if (host is LauncherWindow lw) lw.Prefill(target.Contains(':') ? target[(target.IndexOf(':') + 1)..] : "spot");
            await Task.Delay(target == "settings" ? 4000 : 600); // settings loads model lists from the providers
        }

        // Lay the content out in place (so bindings to its window still resolve), then paint it
        // over a desktop-ish backdrop so translucency reads like it would on screen.
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        await Task.Delay(300);

        if (host is DockWindow bw && target.EndsWith(":badges"))
        {
            bw.ShowSampleBadges();
            await Task.Delay(700); // let the pop-in finish
        }

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

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Wisp.Desktop.ShellChrome.RECT r);

    /// <summary>Captures the desktop itself (wallpaper layer and icons), even under open windows.</summary>
    static void CaptureDesktop(string file)
    {
        var shell = GetShellWindow();
        GetWindowRect(shell, out var r);
        using var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            PrintWindow(shell, hdc, 2); // PW_RENDERFULLCONTENT: include DirectX-drawn children
            g.ReleaseHdc(hdc);
        }
        using var half = new System.Drawing.Bitmap(bmp, bmp.Width / 2, bmp.Height / 2);
        half.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }
}
