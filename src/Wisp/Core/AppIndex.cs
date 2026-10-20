using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Wisp.Core;

/// <param name="Name">What Start shows.</param>
/// <param name="Path">How to launch it: "shell:AppsFolder\&lt;id&gt;" for Start entries, or a shortcut file.</param>
/// <param name="AppId">AppUserModelID when the entry has one (Store apps, Chrome, Outlook…); matches window ids.</param>
/// <param name="TargetExe">The program a desktop app runs, when known; used to match its windows.</param>
public sealed record AppEntry(string Name, string Path, string? AppId, string? TargetExe);

/// <summary>
/// Installed apps, read from Windows' own app list (shell:AppsFolder, the same list Start uses), so
/// Store/packaged apps (Teams, new Outlook, Slack, Calculator…) are included alongside desktop apps.
/// Desktop shortcuts that aren't in Start are added too.
/// </summary>
public sealed class AppIndex
{
    public const string AppsFolderPrefix = @"shell:AppsFolder\";

    List<AppEntry> _apps = [];
    DateTime _scanned = DateTime.MinValue;
    readonly object _gate = new();

    public IReadOnlyList<AppEntry> All
    {
        get
        {
            if (DateTime.Now - _scanned > TimeSpan.FromMinutes(10)) Scan();
            return _apps;
        }
    }

    public void Scan()
    {
        lock (_gate)
        {
            var found = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var a in RunSta(ReadAppsFolder)) found.TryAdd(a.Name, a); }
            catch (Exception ex) { Log.Error("Reading the Windows app list failed; falling back to shortcuts", ex); }
            foreach (var a in ScanShortcuts()) found.TryAdd(a.Name, a);
            _apps = found.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _scanned = DateTime.Now;
            Log.Info($"App index: {_apps.Count} apps");
        }
    }

    /// <summary>Shell COM objects want an STA thread; callers may be on the thread pool.</summary>
    static T RunSta<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return work();
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = work(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return result;
    }

    static List<AppEntry> ReadAppsFolder()
    {
        var list = new List<AppEntry>();
        var shellType = Type.GetTypeFromProgID("Shell.Application")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic folder = shell.Namespace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string parsing = item.Path;
                if (string.IsNullOrWhiteSpace(name) || IsJunk(name)) continue;

                // Parsing names are either an AppUserModelID ("Chrome", "MSTeams_8wekyb3d8bbwe!MSTeams")
                // or a path, sometimes rooted at a known-folder GUID ("{6D809377-…}\App\app.exe").
                bool isPath = parsing.Contains(":\\") || parsing.StartsWith('{') || parsing.StartsWith(@"\\");
                string? target = null;
                try { target = item.ExtendedProperty("System.Link.TargetParsingPath") as string; } catch { }
                if (string.IsNullOrWhiteSpace(target) && isPath) target = KnownFolders.Resolve(parsing);
                if (!string.IsNullOrWhiteSpace(target) && !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) target = null;

                list.Add(new AppEntry(name, AppsFolderPrefix + parsing, isPath ? null : parsing, target));
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return list;
    }

    static IEnumerable<AppEntry> ScanShortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".lnk" or ".url" or ".appref-ms")) continue;
                var name = System.IO.Path.GetFileNameWithoutExtension(f);
                if (IsJunk(name)) continue;
                yield return new AppEntry(name, f, null, null);
            }
        }
    }

    static bool IsJunk(string name) =>
        name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("readme", StringComparison.OrdinalIgnoreCase);

    /// <summary>Best match for a spoken app name ("spotify", "vs code", "the calculator").</summary>
    public AppEntry? Find(string query)
    {
        var q = Normalize(query);
        if (q.Length == 0) return null;
        var apps = All;
        return apps.FirstOrDefault(a => Normalize(a.Name) == q)
            ?? apps.Where(a => Normalize(a.Name).StartsWith(q)).OrderBy(a => a.Name.Length).FirstOrDefault()
            ?? apps.Where(a => Normalize(a.Name).Contains(q)).OrderBy(a => a.Name.Length).FirstOrDefault()
            ?? apps.Where(a => Acronym(a.Name) == q).FirstOrDefault()
            ?? apps.Where(a => q.Split(' ').All(w => Normalize(a.Name).Contains(w))).OrderBy(a => a.Name.Length).FirstOrDefault();
    }

    /// <summary>The entry with this launch path (e.g. a dock pin), if it's still installed.</summary>
    public AppEntry? ByPath(string path) =>
        All.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<AppEntry> Search(string query)
    {
        var q = Normalize(query);
        if (q.Length == 0) return All;
        return All.Where(a => Normalize(a.Name).Contains(q) || Acronym(a.Name).StartsWith(q))
                  .OrderBy(a => Normalize(a.Name).StartsWith(q) ? 0 : 1).ThenBy(a => a.Name.Length);
    }

    public static void Launch(AppEntry app) => Launch(app.Path);

    /// <summary>Starts an app the way Start does (works for Store apps too).</summary>
    public static void Launch(string path)
    {
        if (path.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Replace("the ", "").Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();

    static string Acronym(string s) =>
        new string(s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToLowerInvariant(w[0])).ToArray());
}

/// <summary>Resolves Windows' "{KNOWNFOLDERID}\rest\of\path" notation to a real path.</summary>
public static class KnownFolders
{
    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public static string? Resolve(string stored)
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
}
