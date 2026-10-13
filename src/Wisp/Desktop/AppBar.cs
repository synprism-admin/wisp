using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wisp.Native;

namespace Wisp.Desktop;

/// <summary>
/// Registers a WPF window as a Windows "app bar" docked to an edge of the primary monitor,
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
    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

    public enum Edge : uint { Left = 0, Top = 1, Right = 2, Bottom = 3 }

    readonly Window _window;
    readonly Edge _edge;
    readonly double _thicknessDip;
    readonly IntPtr _hwnd;
    readonly uint _callback;
    readonly HwndSource _source;
    bool _registered;

    public event Action<bool>? FullScreenChanged;
    /// <summary>Raised after every reposition; bars re-assert topmost, so floating UI must re-raise itself.</summary>
    public event Action? Positioned;

    public AppBar(Window window, Edge edge, double thicknessDip)
    {
        _window = window;
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
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        double scale = VisualTreeHelper.GetDpi(_window).DpiScaleY;
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
                bool full = l != IntPtr.Zero;
                _window.Visibility = full ? Visibility.Hidden : Visibility.Visible;
                if (!full) Position();
                FullScreenChanged?.Invoke(full);
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _source.RemoveHook(WndProc);
        if (!_registered) return;
        var abd = Data();
        SHAppBarMessage(ABM_REMOVE, ref abd);
        _registered = false;
    }
}
