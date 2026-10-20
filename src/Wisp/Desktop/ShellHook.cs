using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Wisp.Desktop;

/// <summary>
/// Receives the shell notifications a taskbar gets: windows flashing for attention (an admin
/// prompt waiting, a chat message, a finished download) and windows being activated. With the
/// Windows taskbar hidden, this is how Wisp knows to surface them.
/// </summary>
public sealed class ShellHook : IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr h);

    const int HSHELL_WINDOWACTIVATED = 4, HSHELL_RUDEAPPACTIVATED = 0x8004, HSHELL_FLASH = 0x8006, HSHELL_WINDOWDESTROYED = 2;

    readonly HwndSource _source;
    readonly uint _msg;

    /// <summary>A window started flashing (wants attention).</summary>
    public event Action<IntPtr>? Flash;
    /// <summary>A window was activated or destroyed, so any attention it wanted is over.</summary>
    public event Action<IntPtr>? Settled;

    public ShellHook()
    {
        _source = new HwndSource(new HwndSourceParameters("WispShellHook") { Width = 0, Height = 0, WindowStyle = 0 });
        _msg = ShellChrome.RegisterWindowMessage("SHELLHOOK");
        _source.AddHook(WndProc);
        RegisterShellHookWindow(_source.Handle);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg != _msg) return IntPtr.Zero;
        switch ((int)w)
        {
            case HSHELL_FLASH: Flash?.Invoke(l); break;
            case HSHELL_WINDOWACTIVATED or HSHELL_RUDEAPPACTIVATED or HSHELL_WINDOWDESTROYED: Settled?.Invoke(l); break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        DeregisterShellHookWindow(_source.Handle);
        _source.Dispose();
    }
}
