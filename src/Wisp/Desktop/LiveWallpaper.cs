using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Wisp.Core;
using Wisp.Native;
using Wisp.UI;

namespace Wisp.Desktop;

/// <summary>
/// The live desktop: the weather drawn over your wallpaper (rain, snow, storms, fog). Each screen
/// gets a see-through, click-through layer kept just above the desktop and below every app window,
/// with a tint (a darker sky in rain, haze in fog, lightning flashes) and the falling rain or snow.
/// The wallpaper and icons stay visible and clickable underneath.
///
/// Why not behind the icons, like live-wallpaper apps? Current Windows 11 builds composite the
/// desktop so that windows placed inside Explorer's desktop are never drawn. Layers over the desktop
/// work on every build and change nothing in Explorer; they close when Wisp stops.
/// Animation pauses on any screen covered by a maximized or full-screen window.
/// </summary>
public sealed class LiveWallpaper : IDisposable
{
    readonly WispController _app;
    readonly List<Host> _hosts = [];
    readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1.5) };
    Sky? _preview;
    Sky? _shown;

    sealed record Host(HwndSource Layer, Rectangle Tint, WeatherScene Scene, System.Windows.Forms.Screen Screen);

    public LiveWallpaper(WispController app)
    {
        _app = app;
        _tick.Tick += (_, _) => { KeepAtBottom(); PauseCovered(); };
        _app.Weather.Changed += Apply;
    }

    /// <summary>Try an effect regardless of the weather (until cleared with null or Wisp restarts).</summary>
    public Sky? Preview
    {
        get => _preview;
        set { _preview = value; Apply(); }
    }

    /// <summary>Lets the "--snapshot live:..." test show the real thing alongside an installed Wisp.</summary>
    public bool AllowInSnapshot { get; set; }

    /// <summary>What the desktop is showing now, if anything.</summary>
    public Sky? Showing => _shown;

    /// <summary>Shows, updates or removes the live desktop to match the weather and settings.</summary>
    public void Apply()
    {
        if (_app.SnapshotMode && !AllowInSnapshot) return;
        var now = _app.Weather.Current;
        Sky? sky = _preview ?? (_app.Settings.Weather.LiveWallpaper ? now?.Sky : null);
        if (sky is null or Sky.Clear) { Close(); return; }

        try
        {
            if (_hosts.Count == 0) Open();
            var (color, alpha) = TintFor(sky.Value);
            foreach (var h in _hosts)
            {
                h.Tint.Fill = new SolidColorBrush(color);
                h.Tint.Opacity = alpha;
                h.Scene.SetSky(sky.Value, now?.Wind ?? 8, now?.WindFrom ?? 270);
            }
            if (_shown != sky) Log.Info($"Live desktop: {sky}");
            _shown = sky;
            KeepAtBottom();
            PauseCovered();
        }
        catch (Exception ex)
        {
            Log.Error("Live desktop failed", ex);
            Close();
        }
    }

    /// <summary>Rebuilds after Explorer restarts or the monitors change.</summary>
    public void Rebuild()
    {
        Close();
        Apply();
    }

    static (Color, double) TintFor(Sky sky) => sky == Sky.Fog
        ? (Color.FromRgb(0xC8, 0xCD, 0xD4), 0.30)
        : (Color.FromRgb(0x05, 0x08, 0x12), WeatherScene.Gloom(sky));

    void Open()
    {
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var tint = new Rectangle { Opacity = 0 };
            var scene = new WeatherScene { MaxFps = 30 };
            var root = new Grid { Background = Brushes.Transparent, IsHitTestVisible = false };
            root.Children.Add(tint);
            root.Children.Add(scene);

            var b = screen.Bounds;
            var layer = new HwndSource(new HwndSourceParameters("WispLiveDesktop")
            {
                WindowStyle = WS_POPUP | WS_VISIBLE,
                ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
                UsesPerPixelOpacity = true, // see-through except where the weather is drawn
                PositionX = b.Left, PositionY = b.Top, Width = b.Width, Height = b.Height,
            })
            { RootVisual = root };
            WindowTracker.IgnoreWindow(layer.Handle);
            _hosts.Add(new Host(layer, tint, scene, screen));
        }
        _tick.Start();
    }

    /// <summary>Keeps the layers directly above the desktop, under every app window.</summary>
    void KeepAtBottom()
    {
        var desktop = GetShellWindow();
        if (desktop == IntPtr.Zero) return;
        foreach (var h in _hosts)
        {
            // Right above the desktop: after whatever window currently sits on it.
            var layer = h.Layer.Handle;
            var above = GetWindow(desktop, GW_HWNDPREV);
            if (above == layer) continue;
            SetWindowPos(layer, above == IntPtr.Zero ? HWND_TOP : above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    void Close()
    {
        _tick.Stop();
        foreach (var h in _hosts)
        {
            h.Scene.Stop();
            h.Layer.Dispose();
        }
        if (_hosts.Count > 0) Log.Info("Live desktop off");
        _hosts.Clear();
        _shown = null;
    }

    /// <summary>Stops animating screens nobody can see (a maximized or full-screen window is in front).</summary>
    void PauseCovered()
    {
        var fg = Win32.GetForegroundWindow();
        var fgMonitor = Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST);
        bool covering = fg != IntPtr.Zero && fg != GetShellWindow() && (IsZoomed(fg) || FillsMonitor(fg));
        foreach (var h in _hosts)
        {
            bool hidden = covering && Win32.MonitorOf(h.Screen) == fgMonitor;
            if (hidden || _shown is not { } sky || !WeatherScene.Animates(sky)) h.Scene.Stop();
            else h.Scene.Start();
        }
    }

    static bool FillsMonitor(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        var s = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
        return r.Left <= s.Left && r.Top <= s.Top && r.Right >= s.Right && r.Bottom >= s.Bottom;
    }

    public void Dispose()
    {
        _app.Weather.Changed -= Apply;
        Close();
    }

    const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint GW_HWNDPREV = 3, SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_TOP = IntPtr.Zero;
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out ShellChrome.RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
