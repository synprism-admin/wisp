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
    TopBarWindow? _topBar;
    DockWindow? _dock;
    DockModel? _dockModel;
    HwndSource? _listener;

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
    }

    public void Apply()
    {
        // Top bar first so the island can sit inside it.
        if (_d.HideTaskbar && !_d.ShowTopBar && !_d.ShowDock)
        {
            // Never leave the user with no way to see windows.
            _d.ShowDock = true;
        }

        if (_d.ShowTopBar && _topBar is null) { _topBar = new TopBarWindow(_app); _topBar.Show(); }
        else if (!_d.ShowTopBar && _topBar is not null) { _topBar.Close(); _topBar = null; }
        _app.Island.SetBarMode(_d.ShowTopBar);

        if (_d.ShowDock && _dock is null)
        {
            if (!_d.PinsInitialized)
            {
                _d.Pins = DockModel.DefaultPins(_app.Apps);
                _d.PinsInitialized = true;
                _app.Settings.Save();
            }
            _dockModel = new DockModel(_app.Settings);
            _dock = new DockWindow(_app, _dockModel);
            _dock.Show();
        }
        else if (!_d.ShowDock && _dock is not null) { _dock.Close(); _dock = null; _dockModel = null; }

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

    public void PinApp(string path) => _dockModel?.Pin(path);

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
        if (ShellChrome.TaskbarHidden) ShellChrome.ShowTaskbar();
        if (_d.HideDesktopIcons) ShellChrome.SetDesktopIconsVisible(true);
        _topBar?.Close();
        _dock?.Close();
        _listener?.Dispose();
    }
}
