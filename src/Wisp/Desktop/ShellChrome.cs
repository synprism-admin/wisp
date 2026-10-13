using System.Runtime.InteropServices;
using Wisp.Core;

namespace Wisp.Desktop;

/// <summary>
/// Hides and restores the Windows taskbar(s) and desktop icons.
///
/// The taskbar is switched to auto-hide (so it reserves no screen space) and then hidden outright
/// (so it never slides back up). The original auto-hide state is remembered and restored on exit.
/// If Explorer restarts it re-creates the taskbar, so callers re-apply on "TaskbarCreated".
/// </summary>
public static class ShellChrome
{
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage, uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;
    const int SW_HIDE = 0, SW_SHOW = 5;

    static int? _originalState;

    public static bool TaskbarHidden { get; private set; }

    /// <summary>
    /// Explorer's taskbars only. While the tray host runs there's a second "Shell_TrayWnd" (ours,
    /// sitting above Explorer's so apps send it their icons), so class name alone isn't enough.
    /// </summary>
    static IEnumerable<IntPtr> Taskbars()
    {
        var found = new List<(IntPtr h, bool main)>();
        EnumWindows((h, _) =>
        {
            var sb = new System.Text.StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            var cls = sb.ToString();
            if ((cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") && IsExplorer(h))
                found.Add((h, cls == "Shell_TrayWnd"));
            return true;
        }, IntPtr.Zero);
        return found.OrderByDescending(f => f.main).Select(f => f.h);
    }

    static IntPtr MainTaskbar() => Taskbars().FirstOrDefault();

    static bool IsExplorer(IntPtr h)
    {
        Wisp.Native.Win32.GetWindowThreadProcessId(h, out uint pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static int GetState()
    {
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = MainTaskbar() };
        return (int)SHAppBarMessage(ABM_GETSTATE, ref abd);
    }

    static void SetState(int state)
    {
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = MainTaskbar(), lParam = state };
        SHAppBarMessage(ABM_SETSTATE, ref abd);
    }

    public static void HideTaskbar()
    {
        try
        {
            _originalState ??= GetState();
            SetState(ABS_AUTOHIDE | (_originalState.Value & ABS_ALWAYSONTOP));
            foreach (var h in Taskbars()) ShowWindow(h, SW_HIDE);
            TaskbarHidden = true;
            Log.Info("Taskbar hidden");
        }
        catch (Exception ex) { Log.Error("Hiding taskbar failed", ex); }
    }

    public static void ShowTaskbar()
    {
        try
        {
            // If we never saw the original (previous instance was killed), fall back to the Windows default.
            SetState(_originalState ?? ABS_ALWAYSONTOP);
            foreach (var h in Taskbars()) ShowWindow(h, SW_SHOW);
            TaskbarHidden = false;
            Log.Info("Taskbar restored");
        }
        catch (Exception ex) { Log.Error("Restoring taskbar failed", ex); }
    }

    /// <summary>
    /// Windows 11 brings secondary-monitor taskbars back on its own (after app-bar changes, display
    /// changes, Explorer refreshes). Re-hides any that reappeared; returns true if it had to.
    /// </summary>
    public static bool EnforceHidden()
    {
        if (!TaskbarHidden) return false;
        bool changed = false;
        foreach (var h in Taskbars())
            if (IsWindowVisible(h)) { ShowWindow(h, SW_HIDE); changed = true; }
        return changed;
    }

    /// <summary>The ListView that draws desktop icons lives under Progman or a WorkerW.</summary>
    static IntPtr DesktopIconList()
    {
        IntPtr defView = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            EnumWindows((h, _) =>
            {
                var dv = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) { defView = dv; return false; }
                return true;
            }, IntPtr.Zero);
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    public static void SetDesktopIconsVisible(bool visible)
    {
        var list = DesktopIconList();
        if (list != IntPtr.Zero) ShowWindow(list, visible ? SW_SHOW : SW_HIDE);
    }

    /// <summary>Broadcast by Explorer whenever it (re)creates the taskbar.</summary>
    public static uint TaskbarCreatedMessage { get; } = RegisterWindowMessage("TaskbarCreated");
}
