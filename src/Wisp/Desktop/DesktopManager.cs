using System.Windows;
using System.Windows.Interop;
using Wisp.Core;
using Wisp.UI;

namespace Wisp.Desktop;

/// <summary>
/// Turns the desktop pieces on and off from settings: taskbar, top bar, dock, Windows theme,
/// desktop icons. Everything is reversible, and the taskbar always comes back when Wisp exits.
/// </summary>
public sealed class DesktopManager : IDisposable
{
    readonly WispController _app;
    readonly DesktopSettings _d;
    readonly List<TopBarWindow> _topBars = [];
    readonly List<DockWindow> _docks = [];
    HwndSource? _listener;
    string _screenLayout = "";
    Tray? _tray;
    readonly System.Windows.Threading.DispatcherTimer _taskbarWatch = new() { Interval = TimeSpan.FromSeconds(1) };

    public bool TaskbarPeeking { get; private set; }

    public DesktopManager(WispController app)
    {
        _app = app;
        _d = app.Settings.Desktop;

        // Explorer re-creates the taskbar when it restarts; hide it again if we're meant to.
        _listener = new HwndSource(new HwndSourceParameters("WispShellListener") { Width = 0, Height = 0, WindowStyle = 0 });
        _listener.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == ShellChrome.TaskbarCreatedMessage)
                Application.Current.Dispatcher.BeginInvoke(async () =>
                {
                    await Task.Delay(1500);
                    if (_d.HideTaskbar && !TaskbarPeeking) ShellChrome.HideTaskbar();
                });
            return IntPtr.Zero;
        });

        _taskbarWatch.Tick += (_, _) =>
        {
            if (_d.HideTaskbar && !TaskbarPeeking && ShellChrome.EnforceHidden()) Log.Info("Re-hid a taskbar Windows brought back");
        };
        _taskbarWatch.Start();

        // Monitor plugged in, unplugged or rearranged: rebuild the per-screen bars.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(800); // let Windows settle the new layout
            if (ScreenLayout() != _screenLayout) { CloseBars(); Apply(); }
        });
    }

    static string ScreenLayout() =>
        string.Join("|", System.Windows.Forms.Screen.AllScreens.Select(s => $"{s.DeviceName}:{s.Bounds}:{s.Primary}"));

    void CloseBars()
    {
        foreach (var b in _topBars) b.Close();
        foreach (var d in _docks) d.Close();
        _topBars.Clear();
        _docks.Clear();
    }

    public void Apply()
    {
        // Top bar first so the island can sit inside it.
        if (_d.HideTaskbar && !_d.ShowTopBar && !_d.ShowDock)
        {
            // Never leave the user with no way to see windows.
            _d.ShowDock = true;
        }

        _screenLayout = ScreenLayout();
        var screens = System.Windows.Forms.Screen.AllScreens;

        // The tray host runs only while there's a top bar to show the icons in; otherwise
        // Explorer keeps its own tray.
        if (_d.ShowTopBar && _tray is null)
        {
            try { _tray = new Tray(_app.Settings); }
            catch (Exception ex) { Log.Error("Tray host failed to start", ex); }
        }
        else if (!_d.ShowTopBar && _tray is not null) { _tray.Dispose(); _tray = null; }

        // A top bar on every screen.
        if (_d.ShowTopBar && _topBars.Count == 0)
            foreach (var screen in screens)
            {
                var bar = new TopBarWindow(_app, screen);
                bar.AttachTray(_tray);
                _topBars.Add(bar);
                bar.Show();
            }
        else if (!_d.ShowTopBar && _topBars.Count > 0)
        {
            foreach (var b in _topBars) b.Close();
            _topBars.Clear();
        }
        _app.Island.SetBarMode(_d.ShowTopBar, _app.Themes.Current.TopBarHeight);

        // A dock on every screen, each showing the windows that are open on it.
        if (_d.ShowDock && _docks.Count == 0)
        {
            if (!_d.PinsInitialized)
            {
                _d.Pins = DockModel.DefaultPins(_app.Apps);
                _d.PinsInitialized = true;
                _app.Settings.Save();
            }
            bool multi = screens.Length > 1;
            foreach (var screen in screens)
            {
                var dock = new DockWindow(_app, new DockModel(_app.Settings, _app.Apps, multi ? Native.Win32.MonitorOf(screen) : IntPtr.Zero), screen);
                _docks.Add(dock);
                dock.Show();
            }
        }
        else if (!_d.ShowDock && _docks.Count > 0)
        {
            foreach (var d in _docks) d.Close();
            _docks.Clear();
        }

        if (_d.HideTaskbar && !TaskbarPeeking) ShellChrome.HideTaskbar();
        else if (!_d.HideTaskbar && ShellChrome.TaskbarHidden) ShellChrome.ShowTaskbar();

        ShellChrome.SetDesktopIconsVisible(!_d.HideDesktopIcons);
        ApplyWindowsTheme();

        // Keep the island above the bars.
        _app.Island.Topmost = false;
        _app.Island.Topmost = true;
    }

    public void ApplyWindowsTheme()
    {
        var pack = _app.Themes.Current;
        if (_d.ThemeWindows)
        {
            _d.OriginalTheme ??= WindowsTheme.Capture();
            WindowsTheme.Apply(pack.Dark, _d.AccentTitleBars && pack.AccentTitleBars, ThemeEngine.AccentRgb(pack));
            _app.Settings.Save();
        }
        else if (_d.OriginalTheme is not null)
        {
            WindowsTheme.Restore(_d.OriginalTheme);
            _d.OriginalTheme = null;
            _app.Settings.Save();
        }
    }

    public void ToggleTaskbarPeek()
    {
        if (!_d.HideTaskbar) return;
        TaskbarPeeking = !TaskbarPeeking;
        if (TaskbarPeeking) ShellChrome.ShowTaskbar(); else ShellChrome.HideTaskbar();
    }

    public void PinApp(string path) => _docks.FirstOrDefault()?.Pin(path);

    /// <summary>One switch for the whole look.</summary>
    public void SetAll(bool on)
    {
        _d.HideTaskbar = on;
        _d.ShowDock = on;
        _d.ShowTopBar = on;
        _d.ThemeWindows = on;
        _d.WinKeyOpensLauncher = on;
        _app.Settings.Save();
        Apply();
    }

    public bool AllOn => _d.HideTaskbar && _d.ShowDock && _d.ShowTopBar && _d.ThemeWindows;

    /// <summary>Called on exit: never leave Windows without its taskbar or desktop icons.</summary>
    public void Dispose()
    {
        _taskbarWatch.Stop();
        if (_app.SnapshotMode) { _listener?.Dispose(); return; }
        if (ShellChrome.TaskbarHidden) ShellChrome.ShowTaskbar();
        if (_d.HideDesktopIcons) ShellChrome.SetDesktopIconsVisible(true);
        CloseBars();
        _tray?.Dispose();
        _listener?.Dispose();
    }
}
