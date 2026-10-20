using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Wisp.Native;

namespace Wisp.Desktop;

/// <param name="AppId">AppUserModelID Windows groups the window by (per window, or the Store app's package id).</param>
/// <param name="DisplayName">The app's own label for this window's group, e.g. "Sam (work) - Chrome".</param>
public sealed record TopWindow(IntPtr Handle, string Title, string ExePath, string ProcessName, bool Minimized,
    string? AppId = null, string? DisplayName = null, string? IconResource = null, string? RelaunchCommand = null)
{
    /// <summary>Taskbar grouping key: the AppUserModelID when there is one, otherwise the program.</summary>
    public string Key => AppId is { } id ? "id:" + id.ToLowerInvariant() : "exe:" + ExePath.ToLowerInvariant();
}

/// <summary>The windows a taskbar would show: visible, un-owned, not tool windows, not cloaked.</summary>
public static class WindowTracker
{
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);

    const uint GW_OWNER = 4, GA_ROOTOWNER = 3;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint WM_CLOSE = 0x10;
    const int SW_MINIMIZE = 6;
    const long WS_EX_APPWINDOW = 0x40000;

    static readonly HashSet<IntPtr> Ignore = [];

    public static void IgnoreWindow(IntPtr h) => Ignore.Add(h);

    public static List<TopWindow> List()
    {
        var list = new List<TopWindow>();
        Win32.EnumWindows((h, _) =>
        {
            if (Ignore.Contains(h) || !Win32.IsWindowVisible(h)) return true;
            var ex = (long)Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE);
            bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && !appWindow) return true;
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero && !appWindow) return true;
            if (Win32.DwmGetWindowAttribute(h, Win32.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            var title = Win32.WindowTitle(h);
            if (string.IsNullOrWhiteSpace(title)) return true;

            Win32.GetWindowThreadProcessId(h, out uint pid);
            var exe = ExePath(pid);
            // Store apps are hosted by ApplicationFrameHost; the real process owns the CoreWindow child.
            if (exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                var core = FindWindowEx(h, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (core != IntPtr.Zero) { Win32.GetWindowThreadProcessId(core, out pid); exe = ExePath(pid); }
            }
            if (exe.Length == 0) return true;

            // Group the way the Windows taskbar does: by the window's AppUserModelID if it set one
            // (Chrome sets one per profile), else the Store package's id, else the program.
            var identity = AppIdentity.ForWindow(h);
            var appId = identity.AppId ?? PackagedId(pid);
            list.Add(new TopWindow(h, title, exe, Path.GetFileNameWithoutExtension(exe), Win32.IsIconic(h),
                appId, identity.DisplayName, identity.IconResource, identity.RelaunchCommand));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static readonly Dictionary<uint, string?> PackagedCache = [];

    static string? PackagedId(uint pid)
    {
        if (PackagedCache.TryGetValue(pid, out var id)) return id;
        id = AppIdentity.ForPackagedProcess(pid);
        if (PackagedCache.Count > 2000) PackagedCache.Clear();
        PackagedCache[pid] = id;
        return id;
    }

    static readonly Dictionary<uint, string> PidCache = [];

    static string ExePath(uint pid)
    {
        if (PidCache.TryGetValue(pid, out var cached)) return cached;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            var path = QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
            if (PidCache.Count > 2000) PidCache.Clear();
            PidCache[pid] = path;
            return path;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Taskbar-style click: focus it, or minimize it if it's already in front.</summary>
    public static void Activate(IntPtr h, bool allowMinimize = true)
    {
        if (allowMinimize && Win32.GetForegroundWindow() == h && !Win32.IsIconic(h))
        {
            ShowWindow(h, SW_MINIMIZE);
            return;
        }
        Win32.ForceForeground(h);
    }

    public static void Close(IntPtr h) => PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static void Launch(string path) => Wisp.Core.AppIndex.Launch(path);
}
