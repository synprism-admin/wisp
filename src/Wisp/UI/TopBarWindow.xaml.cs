using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop;
using Wisp.Native;

namespace Wisp.UI;

/// <summary>
/// The top bar: brand/launcher and focused app on the left, the island in the middle (it floats
/// over the bar), status on the right. An app bar, so maximized windows start below it.
/// </summary>
public partial class TopBarWindow : Window
{
    readonly WispController _app;
    readonly System.Windows.Forms.Screen _screen;
    readonly bool _primary;
    IntPtr _monitor;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1.5) };
    AppBar? _bar;
    IntPtr _hwnd;
    double _height;
    DispatcherTimer? _redock;
    string _lastFocused = "";

    public TopBarWindow(WispController app, System.Windows.Forms.Screen? screen = null)
    {
        _app = app;
        _screen = screen ?? System.Windows.Forms.Screen.PrimaryScreen!;
        _primary = _screen.Primary;
        _monitor = Win32.MonitorOf(_screen);
        InitializeComponent();
        // The island lives on the main screen; other screens get a clock in the middle instead.
        CenterClock.Visibility = _primary ? Visibility.Collapsed : Visibility.Visible;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            WindowTracker.IgnoreWindow(_hwnd);
            Dock();
        };
        Closed += (_, _) => { _bar?.Dispose(); _tick.Stop(); };

        _tick.Tick += (_, _) => Refresh();
        _tick.Start();
        _app.Media.Changed += () => Dispatcher.BeginInvoke(async () => await RefreshMediaAsync());
        _app.Themes.Applied += p => Dispatcher.BeginInvoke(() =>
        {
            BrandText.Text = p.Brand;
            if (Math.Abs(p.TopBarHeight - _height) > 0.5)
            {
                _redock?.Stop();
                _redock = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
                    (_, _) => { _redock!.Stop(); Dock(); }, Dispatcher);
                _redock.Start();
            }
        });
        BrandText.Text = _app.Themes.Current.Brand;
        Refresh();
        _ = RefreshMediaAsync();
    }

    /// <summary>(Re)registers the bar at the theme's height.</summary>
    void Dock()
    {
        if (_hwnd == IntPtr.Zero) return;
        _height = Math.Clamp(_app.Themes.Current.TopBarHeight, 30, 60);
        _bar?.Dispose();
        _bar = new AppBar(this, AppBar.Edge.Top, _height, _screen);
        if (!_primary) return; // the island and its z-order belong to the main screen's bar
        _bar.Positioned += _app.Island.KeepOnTop;
        _app.Island.KeepOnTop();
        _bar.FullScreenChanged += full => _app.Island.Visibility = full ? Visibility.Hidden : Visibility.Visible;
        _app.Island.SetBarMode(true, _height);
    }

    void Refresh()
    {
        DateText.Text = DateTime.Now.ToString("ddd MMM d");
        CenterClock.Text = DateTime.Now.ToString("h:mm");

        var fg = Win32.GetForegroundWindow();
        if (fg != _hwnd)
        {
            // Each bar names the focused app only when it's on that bar's screen.
            var name = Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST) == _monitor ? AppName(fg) : "";
            if (name != _lastFocused) { FocusedApp.Text = name; _lastFocused = name; }
        }

        try
        {
            int vol = _app.Media.Volume;
            bool muted = _app.Media.Muted;
            VolText.Text = muted ? "" : $"{vol}";
            VolIcon.Text = muted || vol == 0 ? "" : vol < 34 ? "" : vol < 67 ? "" : "";
        }
        catch { }

        bool online = NetworkInterface.GetIsNetworkAvailable();
        bool wifi = online && NetworkInterface.GetAllNetworkInterfaces().Any(n =>
            n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        NetIcon.Text = !online ? "" : wifi ? "" : "";

        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        if (power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery))
            BatteryPanel.Visibility = Visibility.Collapsed;
        else
        {
            BatteryPanel.Visibility = Visibility.Visible;
            int pct = (int)Math.Round(power.BatteryLifePercent * 100);
            BatteryText.Text = $"{pct}%";
            bool charging = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            BatteryIcon.Text = charging ? "" : ((char)(0xE850 + Math.Clamp(pct / 10, 0, 10))).ToString();
        }

        TrayButton.Visibility = ShellChrome.TaskbarHidden || _app.Desktop.TaskbarPeeking ? Visibility.Visible : Visibility.Collapsed;
    }

    static string AppName(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        Win32.GetWindowThreadProcessId(h, out uint pid);
        try
        {
            var p = Process.GetProcessById((int)pid);
            if (p.ProcessName is "explorer" && Win32.WindowTitle(h) is "" or "Program Manager") return "Desktop";
            var desc = p.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(desc) ? p.ProcessName : desc;
        }
        catch { return Win32.WindowTitle(h); }
    }

    async Task RefreshMediaAsync()
    {
        var np = await _app.Media.CurrentAsync();
        if (np is null || string.IsNullOrEmpty(np.Title))
        {
            MediaButton.Visibility = Visibility.Collapsed;
            return;
        }
        MediaButton.Visibility = Visibility.Visible;
        MediaText.Text = string.IsNullOrEmpty(np.Artist) ? np.Title : $"{np.Title} · {np.Artist}";
        MediaIcon.Text = np.Playing ? "" : "";
    }

    void Brand_Click(object s, RoutedEventArgs e) => _app.ShowLauncher();
    void Media_Click(object s, RoutedEventArgs e) => _app.Media.PlayPause();
    void Power_Click(object s, RoutedEventArgs e) => _app.Island.OpenPanel("power");
    void Network_Click(object s, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });
    void Tray_Click(object s, RoutedEventArgs e) => _app.Desktop.ToggleTaskbarPeek();

    void Volume_Click(object s, RoutedEventArgs e)
    {
        try { _app.Media.Muted = !_app.Media.Muted; } catch { }
        Refresh();
    }

    void Volume_Wheel(object s, MouseWheelEventArgs e)
    {
        try { _app.Media.Volume += e.Delta > 0 ? 4 : -4; } catch { }
        Refresh();
        e.Handled = true;
    }
}
