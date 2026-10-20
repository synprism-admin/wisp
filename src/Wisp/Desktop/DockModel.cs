using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using Wisp.Core;
using Wisp.Native;

namespace Wisp.Desktop;

public sealed class DockItem : INotifyPropertyChanged
{
    /// <summary>Grouping key: "id:&lt;AppUserModelID&gt;" or "exe:&lt;path&gt;".</summary>
    public required string Key { get; init; }
    public string? AppId { get; init; }
    public string? TargetExe { get; init; }
    public required string Name { get; set; }
    public ImageSource? Icon { get; set; }
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    /// <summary>The pin as stored in settings (null for an unpinned running app).</summary>
    public string? PinnedPath { get; set; }
    /// <summary>How the app relaunches this group (e.g. Chrome with a specific profile).</summary>
    public string? RelaunchCommand { get; init; }
    public string? IconResource { get; init; }

    List<TopWindow> _windows = [];
    public List<TopWindow> Windows
    {
        get => _windows;
        set { _windows = value; Changed(); Changed(nameof(IsRunning)); Changed(nameof(DotCount)); }
    }

    bool _active;
    public bool IsActive { get => _active; set { if (_active != value) { _active = value; Changed(); } } }

    bool _attention;
    /// <summary>One of its windows is flashing for attention (the taskbar's orange button).</summary>
    public bool NeedsAttention { get => _attention; set { if (_attention != value) { _attention = value; Changed(); } } }

    public bool IsPinned => PinnedPath is not null;
    public bool IsRunning => _windows.Count > 0;
    public int DotCount => Math.Min(_windows.Count, 3);

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>
/// Pinned apps + running windows, grouped the way the Windows taskbar groups them: by
/// AppUserModelID when a window has one (so each Chrome profile is its own item), otherwise by
/// program. Pins can be Start-menu entries (shell:AppsFolder\…), shortcuts, programs, or a
/// remembered relaunch command (a specific Chrome profile).
/// </summary>
public sealed class DockModel
{
    const string RelaunchPrefix = "relaunch:";

    readonly Settings _settings;
    readonly AppIndex _apps;
    readonly IntPtr _monitor; // windows shown are those on this monitor (Zero = all)
    List<TopWindow> _all = [];
    readonly Dictionary<string, string?> _shortcutTargets = new(StringComparer.OrdinalIgnoreCase);
    int _cycle;

    /// <summary>Raised when any dock pins/unpins, so docks on other monitors follow.</summary>
    public static event Action? PinsChanged;

    /// <summary>Windows currently flashing for attention (fed by the shell hook; shared by all docks).</summary>
    public static readonly HashSet<IntPtr> Flashing = [];

    public ObservableCollection<DockItem> Items { get; } = [];

    public DockModel(Settings settings, AppIndex apps, IntPtr monitor = default)
    {
        _settings = settings;
        _apps = apps;
        _monitor = monitor;
        PinsChanged += OnPinsChanged;
        Rebuild();
    }

    void OnPinsChanged() => System.Windows.Application.Current.Dispatcher.BeginInvoke(Rebuild);

    public void Detach() => PinsChanged -= OnPinsChanged;

    // ------------------------------------------------------------------ pins

    sealed record RelaunchPin(string Id, string Cmd, string Name, string? Icon);

    DockItem? FromPin(string pin)
    {
        try
        {
            if (pin.StartsWith(RelaunchPrefix))
            {
                var r = JsonSerializer.Deserialize<RelaunchPin>(pin[RelaunchPrefix.Length..])!;
                var exe = AppIdentity.SplitCommand(r.Cmd).Exe;
                return new DockItem
                {
                    Key = "id:" + r.Id.ToLowerInvariant(), AppId = r.Id, TargetExe = exe, Name = r.Name, PinnedPath = pin,
                    RelaunchCommand = r.Cmd, IconResource = r.Icon,
                    Icon = AppIdentity.LoadIconResource(r.Icon) ?? ShellIcons.Get(exe),
                };
            }
            if (pin.StartsWith(AppIndex.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var entry = _apps.ByPath(pin);
                if (entry is null) return null; // uninstalled
                return new DockItem
                {
                    Key = entry.AppId is { } id ? "id:" + id.ToLowerInvariant() : "exe:" + (entry.TargetExe ?? pin).ToLowerInvariant(),
                    AppId = entry.AppId, TargetExe = entry.TargetExe, Name = entry.Name, PinnedPath = pin, Icon = ShellIcons.Get(pin),
                };
            }
            if (!File.Exists(pin)) return null;
            if (pin.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var appId = AppIdentity.ForShortcut(pin);
                var target = ShortcutTarget(pin);
                return new DockItem
                {
                    Key = appId is not null ? "id:" + appId.ToLowerInvariant() : "exe:" + (target ?? pin).ToLowerInvariant(),
                    AppId = appId, TargetExe = target, Name = Path.GetFileNameWithoutExtension(pin), PinnedPath = pin, Icon = ShellIcons.Get(pin),
                };
            }
            return new DockItem
            {
                Key = "exe:" + pin.ToLowerInvariant(), TargetExe = pin, PinnedPath = pin, Icon = ShellIcons.Get(pin),
                Name = Path.GetFileNameWithoutExtension(pin).Equals("explorer", StringComparison.OrdinalIgnoreCase)
                    ? "File Explorer" : Describe(pin) ?? Path.GetFileNameWithoutExtension(pin),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"Dock pin '{pin}' couldn't be read", ex);
            return null;
        }
    }

    string? ShortcutTarget(string lnk)
    {
        if (_shortcutTargets.TryGetValue(lnk, out var t)) return t;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            string target = shell.CreateShortcut(lnk).TargetPath;
            t = File.Exists(target) ? Path.GetFullPath(target) : null;
        }
        catch { t = null; }
        _shortcutTargets[lnk] = t;
        return t;
    }

    public void Rebuild()
    {
        Items.Clear();
        foreach (var pin in _settings.Desktop.Pins)
            if (FromPin(pin) is { } item && Items.All(i => i.Key != item.Key))
                Items.Add(item);
        Update();
    }

    // ------------------------------------------------------------------ matching windows to items

    /// <summary>Does this window belong to this (pinned) item?</summary>
    static bool Matches(DockItem item, TopWindow w)
    {
        if (w.AppId is not null)
        {
            // The window has its own identity: only an item with the same id (or an id-less pin of
            // the same program, e.g. explorer.exe) claims it. This keeps Chrome profiles apart.
            if (item.AppId is not null) return string.Equals(item.AppId, w.AppId, StringComparison.OrdinalIgnoreCase);
            return SamePath(item.TargetExe, w.ExePath);
        }
        if (SamePath(item.TargetExe, w.ExePath)) return true;
        // Launcher-style apps (Discord's Update.exe, Squirrel apps) run a different exe that lives
        // under the same app folder.
        return item.TargetExe is { } t && AppFolder(t) is { } dir && w.ExePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-reads open windows; adds/removes unpinned running items.</summary>
    public void Update()
    {
        _all = WindowTracker.List();
        var fg = Win32.GetForegroundWindow();
        // Like the Windows taskbar on multiple monitors: each dock shows the windows on its own screen.
        var windows = _monitor == IntPtr.Zero ? _all
            : _all.Where(w => Win32.MonitorFromWindow(w.Handle, Win32.MONITOR_DEFAULTTONEAREST) == _monitor).ToList();

        var claimed = new Dictionary<DockItem, List<TopWindow>>();
        var rest = new List<TopWindow>();
        var pinned = Items.Where(i => i.IsPinned).ToList();
        foreach (var w in windows)
        {
            var owner = pinned.FirstOrDefault(i => Matches(i, w));
            if (owner is null) { rest.Add(w); continue; }
            (claimed.TryGetValue(owner, out var l) ? l : claimed[owner] = []).Add(w);
        }

        // Unpinned running apps, grouped by the taskbar key.
        foreach (var group in rest.GroupBy(w => w.Key))
        {
            var item = Items.FirstOrDefault(i => !i.IsPinned && i.Key == group.Key) ?? AddRunning(group.First());
            claimed[item] = group.ToList();
        }

        foreach (var item in Items.ToList())
        {
            var ws = claimed.GetValueOrDefault(item) ?? [];
            if (!ws.Select(w => w.Handle).SequenceEqual(item.Windows.Select(w => w.Handle)) ||
                ws.Any(w => item.Windows.FirstOrDefault(x => x.Handle == w.Handle)?.Minimized != w.Minimized))
                item.Windows = ws;
            item.IsActive = ws.Any(w => w.Handle == fg);
            if (item.IsActive) foreach (var w in ws) Flashing.Remove(w.Handle);
            item.NeedsAttention = !item.IsActive && ws.Any(w => Flashing.Contains(w.Handle));
            if (!item.IsPinned && ws.Count == 0) Items.Remove(item);
        }
    }

    DockItem AddRunning(TopWindow w)
    {
        // Name/icon: the app's own per-group label (Chrome profile), else the Start entry, else the exe.
        var entry = w.AppId is { } id ? _apps.All.FirstOrDefault(a => string.Equals(a.AppId, id, StringComparison.OrdinalIgnoreCase)) : null;
        var item = new DockItem
        {
            Key = w.Key,
            AppId = w.AppId,
            TargetExe = w.ExePath,
            Name = w.DisplayName ?? entry?.Name ?? Describe(w.ExePath) ?? w.ProcessName,
            Icon = AppIdentity.LoadIconResource(w.IconResource) ?? (entry is not null ? ShellIcons.Get(entry.Path) : null) ?? ShellIcons.Get(w.ExePath),
            RelaunchCommand = w.RelaunchCommand,
            IconResource = w.IconResource,
        };
        Items.Add(item);
        return item;
    }

    /// <summary>The app's own install folder, or null for shared roots where prefix-matching would be wrong.</summary>
    static string? AppFolder(string exe)
    {
        var dir = Path.GetDirectoryName(exe);
        if (dir is null) return null;
        string[] shared =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ];
        if (shared.Any(s => dir.Equals(s, StringComparison.OrdinalIgnoreCase)) ||
            dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            return null;
        return dir + Path.DirectorySeparatorChar;
    }

    static string? Describe(string exe)
    {
        try
        {
            var d = FileVersionInfo.GetVersionInfo(exe).FileDescription;
            return string.IsNullOrWhiteSpace(d) ? null : d.Trim();
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Click: launch if not running; focus/minimize if one window; cycle through several.</summary>
    public void Click(DockItem item)
    {
        if (item.Windows.Count == 0)
        {
            // Open on another monitor? Bring that up rather than starting a second copy.
            var elsewhere = _all.FirstOrDefault(w => item.IsPinned ? Matches(item, w) : w.Key == item.Key);
            if (elsewhere is not null) WindowTracker.Activate(elsewhere.Handle, allowMinimize: false);
            else Launch(item);
            return;
        }
        if (item.Windows.Count == 1) { WindowTracker.Activate(item.Windows[0].Handle); return; }
        var fg = Win32.GetForegroundWindow();
        int idx = item.Windows.FindIndex(w => w.Handle == fg);
        var next = item.Windows[(idx + 1 + (idx < 0 ? _cycle++ % item.Windows.Count : 0)) % item.Windows.Count];
        WindowTracker.Activate(next.Handle, allowMinimize: false);
    }

    /// <summary>Opens a new window of this exact item (the same Chrome profile, etc.).</summary>
    public void Launch(DockItem item)
    {
        try
        {
            if (item.RelaunchCommand is { } cmd)
            {
                var (exe, args) = AppIdentity.SplitCommand(cmd);
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            }
            else if (item.PinnedPath is { } pin) AppIndex.Launch(pin);
            else if (item.AppId is { } id && _apps.All.FirstOrDefault(a => string.Equals(a.AppId, id, StringComparison.OrdinalIgnoreCase)) is { } entry)
                AppIndex.Launch(entry);
            else if (item.TargetExe is { } exePath) AppIndex.Launch(exePath);
        }
        catch (Exception ex) { Log.Error($"Launching {item.Name} failed", ex); }
    }

    /// <summary>What to store so a running item can be pinned and relaunched exactly.</summary>
    string PinFor(DockItem item)
    {
        if (item.AppId is { } id && item.RelaunchCommand is { } cmd)
            return RelaunchPrefix + JsonSerializer.Serialize(new RelaunchPin(id, cmd, item.Name, item.IconResource));
        if (item.AppId is { } appId && _apps.All.FirstOrDefault(a => string.Equals(a.AppId, appId, StringComparison.OrdinalIgnoreCase)) is { } entry)
            return entry.Path;
        return item.TargetExe ?? item.Windows.FirstOrDefault()?.ExePath ?? item.Key;
    }

    public void TogglePin(DockItem item)
    {
        var pins = _settings.Desktop.Pins;
        if (item.IsPinned) pins.RemoveAll(p => string.Equals(p, item.PinnedPath, StringComparison.OrdinalIgnoreCase));
        else pins.Add(PinFor(item));
        _settings.Save();
        PinsChanged?.Invoke(); // every dock (including this one) rebuilds
    }

    public void Pin(string path)
    {
        if (_settings.Desktop.Pins.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _settings.Desktop.Pins.Add(path);
        _settings.Save();
        PinsChanged?.Invoke();
    }

    /// <summary>First-run pins: whichever of the usual suspects are installed.</summary>
    public static List<string> DefaultPins(AppIndex apps)
    {
        var pins = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") };
        string[] wanted = ["Google Chrome", "Microsoft Edge", "Outlook", "Teams", "Spotify", "Visual Studio Code", "Terminal", "Discord"];
        foreach (var w in wanted)
        {
            var app = apps.Find(w);
            if (app is not null && !pins.Contains(app.Path)) pins.Add(app.Path);
        }
        return pins;
    }
}
