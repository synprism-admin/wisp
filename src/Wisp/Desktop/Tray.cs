using System.IO;
using System.Runtime.InteropServices;
using ManagedShell;
using Microsoft.Win32;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using Wisp.Core;
using Wisp.Native;

namespace Wisp.Desktop;

/// <summary>
/// Hosts the notification area (tray icons) for the top bar, using ManagedShell's tray service.
/// It registers a "Shell_TrayWnd" above Explorer's, so apps' tray icons come to Wisp; anything
/// Wisp doesn't handle (including app-bar messages) is forwarded to Explorer untouched. Only runs
/// while the top bar is on; on dispose, apps are told to re-register their icons with Explorer.
/// </summary>
public sealed class Tray : IDisposable
{
    readonly Settings _settings;
    readonly ShellManager _shell;

    public NotificationArea Area => _shell.NotificationArea;

    public Tray(Settings settings)
    {
        _settings = settings;
        var config = ShellManager.DefaultShellConfig;
        // Wisp tracks windows itself; the tasks service is only here so apps' taskbar overlay
        // badges (Slack's or Teams' unread counts) reach Wisp instead of the hidden taskbar.
        config.EnableTasksService = true;
        config.AutoStartTasksService = true;
        config.EnableTrayService = true;
        config.AutoStartTrayService = true;
        config.PinnedNotifyIcons = settings.Desktop.PinnedTrayIcons ?? NotificationArea.DEFAULT_PINNED;
        _shell = new ShellManager(config);
        if (_shell.Tasks.GroupedWindows is null) _shell.Tasks.Initialize(false);
        Log.Info("Tray host started");
        if (settings.Desktop.PinnedTrayIcons is null) _ = ImportWindowsPinsAsync();
    }

    /// <summary>
    /// First run: show in the bar the same icons the user had promoted to the Windows taskbar
    /// (Settings > Personalization > Taskbar > Other system tray icons), instead of library defaults.
    /// </summary>
    async Task ImportWindowsPinsAsync()
    {
        var promoted = PromotedPaths();
        if (promoted.Count == 0) return;
        await Task.Delay(TimeSpan.FromSeconds(4)); // apps re-register their icons after TaskbarCreated
        int pinned = 0;
        foreach (var icon in Area.TrayIcons.ToList())
        {
            if (icon.IsPinned || string.IsNullOrEmpty(icon.Path)) continue;
            if (promoted.Contains(Path.GetFullPath(icon.Path))) { icon.Pin(); pinned++; }
        }
        _settings.Desktop.PinnedTrayIcons = Area.PinnedNotifyIcons;
        _settings.Save();
        Log.Info($"Imported {pinned} pinned tray icons from Windows");
    }

    static HashSet<string> PromotedPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (root is null) return set;
        foreach (var name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k?.GetValue("IsPromoted") is not int p || p != 1) continue;
            if (k.GetValue("ExecutablePath") is not string exe) continue;
            var resolved = ResolveKnownFolder(exe);
            if (resolved is not null) set.Add(resolved);
        }
        return set;
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Windows stores these as "{KNOWNFOLDERID}\rest\of\path.exe".</summary>
    static string? ResolveKnownFolder(string stored)
    {
        try
        {
            if (stored.StartsWith('{') && stored.IndexOf('}') is var end and > 0 && Guid.TryParse(stored[..(end + 1)], out var id))
            {
                if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
                var folder = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                return Path.GetFullPath(folder + stored[(end + 1)..]);
            }
            return Path.GetFullPath(stored);
        }
        catch { return null; }
    }

    // The bar already has its own network, volume and battery/power controls.
    static readonly HashSet<Guid> SystemIcons =
    [
        Guid.Parse(NotificationArea.NETWORK_GUID), Guid.Parse(NotificationArea.VOLUME_GUID), Guid.Parse(NotificationArea.POWER_GUID),
    ];

    /// <summary>Live views over the icons, minus the system ones the bar replaces.</summary>
    public System.ComponentModel.ICollectionView View(bool pinned)
    {
        var view = new System.Windows.Data.ListCollectionView(Area.TrayIcons)
        {
            Filter = o => o is NotifyIcon i && i.IsPinned == pinned && !i.IsHidden && !SystemIcons.Contains(i.GUID),
            IsLiveFiltering = true,
            IsLiveSorting = true,
        };
        view.LiveFilteringProperties.Add(nameof(NotifyIcon.IsPinned));
        view.LiveFilteringProperties.Add(nameof(NotifyIcon.IsHidden));
        view.LiveSortingProperties.Add(nameof(NotifyIcon.PinOrder));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(NotifyIcon.PinOrder), System.ComponentModel.ListSortDirection.Ascending));
        return view;
    }

    /// <summary>Remembers the bar's icon order after a drag.</summary>
    public void SaveOrder()
    {
        _settings.Desktop.PinnedTrayIcons = Area.PinnedNotifyIcons;
        _settings.Save();
    }

    /// <summary>Moves an icon between the bar and the overflow, and remembers the choice.</summary>
    public void TogglePin(NotifyIcon icon)
    {
        if (icon.IsPinned) icon.Unpin(); else icon.Pin();
        _settings.Desktop.PinnedTrayIcons = Area.PinnedNotifyIcons;
        _settings.Save();
    }

    static readonly System.Text.RegularExpressions.Regex Digits = new(@"\d+");

    /// <summary>
    /// The badge an app has put on a window's taskbar button: the unread count if its description
    /// says one ("3 unread messages"), plus the overlay image itself.
    /// </summary>
    public (int? Count, System.Windows.Media.ImageSource? Icon)? BadgeFor(IntPtr hwnd)
    {
        if (_shell.Tasks.GroupedWindows?.SourceCollection is not IEnumerable<ApplicationWindow> windows) return null;
        var w = windows.FirstOrDefault(x => x.Handle == hwnd);
        if (w?.OverlayIcon is not { } icon) return null;
        var m = Digits.Match(w.OverlayIconDescription ?? "");
        int? count = m.Success && int.TryParse(m.Value, out var n) ? n : null;
        return (count, icon);
    }

    /// <summary>Packs the cursor position the way tray icon messages expect (physical pixels).</summary>
    public static uint CursorParam()
    {
        Win32.GetCursorPos(out var p);
        return (uint)((p.Y << 16) | (p.X & 0xFFFF));
    }

    public void Dispose()
    {
        _shell.Dispose();
        Log.Info("Tray host stopped");
    }
}
