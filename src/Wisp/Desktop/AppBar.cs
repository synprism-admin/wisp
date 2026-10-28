using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wisp.Native;

namespace Wisp.Desktop;

/// <summary>
/// Registers a WPF window as a Windows "app bar" docked to an edge of a given monitor,
/// so other windows (including maximized ones) don't cover it. Hides itself while a full-screen
/// app (game, video) is in front.
/// </summary>
public sealed class AppBar : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge;
        public ShellChrome.RECT rc; public IntPtr lParam;
    }

    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out ShellChrome.RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int n);
    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

    public enum Edge : uint { Left = 0, Top = 1, Right = 2, Bottom = 3 }

    readonly Window _window;
    readonly Edge _edge;
    readonly double _thicknessDip;
    readonly IntPtr _hwnd;
    readonly uint _callback;
    readonly HwndSource _source;
    readonly string _device;
    bool _registered;
    bool _shellSaysFull, _hiddenForFullScreen;
    System.Windows.Threading.DispatcherTimer? _fullWatch;

    public event Action<bool>? FullScreenChanged;
    /// <summary>Raised after every reposition; bars re-assert topmost, so floating UI must re-raise itself.</summary>
    public event Action? Positioned;

    public AppBar(Window window, Edge edge, double thicknessDip, System.Windows.Forms.Screen? screen = null)
    {
        _window = window;
        _device = (screen ?? System.Windows.Forms.Screen.PrimaryScreen!).DeviceName;
        _edge = edge;
        _thicknessDip = thicknessDip;
        _hwnd = new WindowInteropHelper(window).Handle;
        _callback = ShellChrome.RegisterWindowMessage($"WispAppBar{edge}");
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);

        var abd = Data();
        abd.uCallbackMessage = _callback;
        SHAppBarMessage(ABM_NEW, ref abd);
        _registered = true;
        Position();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uEdge = (uint)_edge };

    void OnDisplayChanged(object? s, EventArgs e) => _window.Dispatcher.BeginInvoke(Position);

    public void Position()
    {
        if (!_registered) return;
        // Re-resolve by device name: Screen objects go stale when the display layout changes.
        var target = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _device)
                     ?? System.Windows.Forms.Screen.PrimaryScreen!;
        var screen = target.Bounds;
        double scale = Win32.ScaleOf(target);
        int t = (int)Math.Ceiling(_thicknessDip * scale);

        var abd = Data();
        abd.rc = _edge switch
        {
            Edge.Top => new ShellChrome.RECT { Left = screen.Left, Right = screen.Right, Top = screen.Top, Bottom = screen.Top + t },
            Edge.Left => new ShellChrome.RECT { Left = screen.Left, Right = screen.Left + t, Top = screen.Top, Bottom = screen.Bottom },
            Edge.Right => new ShellChrome.RECT { Left = screen.Right - t, Right = screen.Right, Top = screen.Top, Bottom = screen.Bottom },
            _ => new ShellChrome.RECT { Left = screen.Left, Right = screen.Right, Top = screen.Bottom - t, Bottom = screen.Bottom },
        };
        SHAppBarMessage(ABM_QUERYPOS, ref abd);
        switch (_edge)
        {
            case Edge.Top: abd.rc.Bottom = abd.rc.Top + t; break;
            case Edge.Left: abd.rc.Right = abd.rc.Left + t; break;
            case Edge.Right: abd.rc.Left = abd.rc.Right - t; break;
            default: abd.rc.Top = abd.rc.Bottom - t; break;
        }
        SHAppBarMessage(ABM_SETPOS, ref abd);
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, abd.rc.Left, abd.rc.Top, abd.rc.Right - abd.rc.Left, abd.rc.Bottom - abd.rc.Top,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        Positioned?.Invoke();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg != _callback) return IntPtr.Zero;
        switch ((int)w)
        {
            case ABN_POSCHANGED: Position(); break;
            case ABN_FULLSCREENAPP:
                _shellSaysFull = l != IntPtr.Zero;
                UpdateFullScreen();
                // The shell doesn't say again when the front window changes, so keep checking while
                // it thinks something is full screen.
                if (_shellSaysFull && _fullWatch is null)
                {
                    _fullWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _fullWatch.Tick += (_, _) => UpdateFullScreen();
                    _fullWatch.Start();
                }
                else if (!_shellSaysFull) { _fullWatch?.Stop(); _fullWatch = null; }
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The shell's full-screen signal also fires for click-through overlays that cover a screen
    /// (Zoom's screen-share border, recording frames) and for full-screen apps on other monitors.
    /// Only step aside when the window in front really fills this bar's screen.
    /// </summary>
    void UpdateFullScreen()
    {
        bool hide = _shellSaysFull && ForegroundFillsScreen();
        if (hide == _hiddenForFullScreen) return;
        _hiddenForFullScreen = hide;
        _window.Visibility = hide ? Visibility.Hidden : Visibility.Visible;
        if (!hide) Position();
        FullScreenChanged?.Invoke(hide);
    }

    bool ForegroundFillsScreen()
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (((long)Win32.GetWindowLongPtr(fg, Win32.GWL_EXSTYLE) & Win32.WS_EX_TRANSPARENT) != 0) return false;
        if (!GetWindowRect(fg, out var r)) return false;
        var screen = (System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _device)
                      ?? System.Windows.Forms.Screen.PrimaryScreen!).Bounds;
        return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _source.RemoveHook(WndProc);
        _fullWatch?.Stop();
        if (!_registered) return;
        var abd = Data();
        SHAppBarMessage(ABM_REMOVE, ref abd);
        _registered = false;
    }
}
