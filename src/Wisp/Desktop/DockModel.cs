using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Wisp.Core;
using Wisp.Native;

namespace Wisp.Desktop;

public sealed class DockItem : INotifyPropertyChanged
{
    public required string Key { get; init; }         // normalized exe path
    public required string Name { get; set; }
    public ImageSource? Icon { get; set; }
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public string? PinnedPath { get; set; }           // shortcut or exe to launch
    public string LaunchPath => PinnedPath ?? Key;

    List<TopWindow> _windows = [];
    public List<TopWindow> Windows
    {
        get => _windows;
        set { _windows = value; Changed(); Changed(nameof(IsRunning)); Changed(nameof(DotCount)); }
    }

    bool _active;
    public bool IsActive { get => _active; set { if (_active != value) { _active = value; Changed(); } } }

    public bool IsPinned => PinnedPath is not null;
    public bool IsRunning => _windows.Count > 0;
    public int DotCount => Math.Min(_windows.Count, 3);

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public void Refresh() { Changed(nameof(IsPinned)); Changed(nameof(Name)); Changed(nameof(Icon)); }
}

/// <summary>Pinned apps + running windows, merged into dock items keyed by executable.</summary>
public sealed class DockModel
{
    readonly Settings _settings;
    readonly Dictionary<string, string> _shortcutTargets = new(StringComparer.OrdinalIgnoreCase);
    int _cycle;

    public ObservableCollection<DockItem> Items { get; } = [];

    public DockModel(Settings settings)
    {
        _settings = settings;
        Rebuild();
    }

    public static string Normalize(string path) => Path.GetFullPath(path).ToLowerInvariant();

    /// <summary>The exe a pinned entry starts, so it can be matched to running windows.</summary>
    string TargetOf(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return Normalize(path);
        if (_shortcutTargets.TryGetValue(path, out var t)) return t;
        string target = "";
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);
            target = (string)link.TargetPath;
        }
        catch { }
        // Store-app shortcuts have no file target; key them by the shortcut itself.
        t = string.IsNullOrEmpty(target) || !File.Exists(target) ? Normalize(path) : Normalize(target);
        _shortcutTargets[path] = t;
        return t;
    }

    public void Rebuild()
    {
        Items.Clear();
        foreach (var pin in _settings.Desktop.Pins.Where(p => File.Exists(p)))
        {
            Items.Add(new DockItem
            {
                Key = TargetOf(pin),
                Name = FriendlyName(pin),
                PinnedPath = pin,
                Icon = ShellIcons.Get(pin),
            });
        }
        Update();
    }

    static string FriendlyName(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path);
        return n.Equals("explorer", StringComparison.OrdinalIgnoreCase) ? "File Explorer" : n;
    }

    /// <summary>Re-reads open windows; adds/removes unpinned running items.</summary>
    public void Update()
    {
        var windows = WindowTracker.List();
        var fg = Win32.GetForegroundWindow();
        var byExe = windows.GroupBy(w => Normalize(w.ExePath)).ToDictionary(g => g.Key, g => g.ToList());

        // Launcher-style shortcuts (Discord's Update.exe, Squirrel apps, ...) start a different exe
        // that lives under the same app folder; fold those windows into the pinned item.
        foreach (var item in Items.Where(i => i.IsPinned))
        {
            var dir = AppFolder(item.Key);
            if (dir is null || byExe.ContainsKey(item.Key)) continue;
            var match = byExe.Keys.FirstOrDefault(k => k.StartsWith(dir) && !Items.Any(i => i.Key == k));
            if (match is not null) { byExe[item.Key] = byExe[match]; byExe.Remove(match); }
        }

        foreach (var item in Items.ToList())
        {
            var ws = byExe.GetValueOrDefault(item.Key) ?? [];
            if (!ws.Select(w => w.Handle).SequenceEqual(item.Windows.Select(w => w.Handle)) ||
                ws.Any(w => item.Windows.FirstOrDefault(x => x.Handle == w.Handle)?.Minimized != w.Minimized))
                item.Windows = ws;
            item.IsActive = ws.Any(w => w.Handle == fg);
            if (!item.IsPinned && ws.Count == 0) Items.Remove(item);
        }

        foreach (var (exe, ws) in byExe)
        {
            if (Items.Any(i => i.Key == exe)) continue;
            var desc = Describe(ws[0].ExePath) ?? ws[0].ProcessName;
            Items.Add(new DockItem
            {
                Key = exe,
                Name = desc,
                Icon = ShellIcons.Get(ws[0].ExePath),
                Windows = ws,
                IsActive = ws.Any(w => w.Handle == fg),
            });
        }
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
        return dir.ToLowerInvariant() + Path.DirectorySeparatorChar;
    }

    static string? Describe(string exe)
    {
        try
        {
            var d = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription;
            return string.IsNullOrWhiteSpace(d) ? null : d.Trim();
        }
        catch { return null; }
    }

    /// <summary>Click: launch if not running; focus/minimize if one window; cycle through several.</summary>
    public void Click(DockItem item)
    {
        if (item.Windows.Count == 0) { WindowTracker.Launch(item.LaunchPath); return; }
        if (item.Windows.Count == 1) { WindowTracker.Activate(item.Windows[0].Handle); return; }
        var fg = Win32.GetForegroundWindow();
        int idx = item.Windows.FindIndex(w => w.Handle == fg);
        var next = item.Windows[(idx + 1 + (idx < 0 ? _cycle++ % item.Windows.Count : 0)) % item.Windows.Count];
        WindowTracker.Activate(next.Handle, allowMinimize: false);
    }

    public void TogglePin(DockItem item)
    {
        var pins = _settings.Desktop.Pins;
        if (item.IsPinned)
        {
            pins.RemoveAll(p => string.Equals(p, item.PinnedPath, StringComparison.OrdinalIgnoreCase));
            item.PinnedPath = null;
            if (!item.IsRunning) Items.Remove(item);
        }
        else
        {
            var path = item.Windows.FirstOrDefault()?.ExePath ?? item.Key;
            pins.Add(path);
            item.PinnedPath = path;
            // Pinned items sit before unpinned ones.
            int firstUnpinned = Items.TakeWhile(i => i.IsPinned || i == item).Count();
            Items.Move(Items.IndexOf(item), Math.Min(firstUnpinned - 1, Items.Count - 1));
        }
        item.Refresh();
        _settings.Save();
    }

    public void Pin(string path)
    {
        if (_settings.Desktop.Pins.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _settings.Desktop.Pins.Add(path);
        _settings.Save();
        Rebuild();
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
