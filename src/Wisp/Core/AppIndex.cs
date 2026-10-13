using System.Diagnostics;
using System.IO;

namespace Wisp.Core;

public sealed record AppEntry(string Name, string Path);

/// <summary>Installed apps, discovered from Start menu shortcuts (what the user actually sees).</summary>
public sealed class AppIndex
{
    List<AppEntry> _apps = [];
    DateTime _scanned = DateTime.MinValue;

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
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        var found = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files)
            {
                var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".lnk" or ".url" or ".appref-ms")) continue;
                var name = System.IO.Path.GetFileNameWithoutExtension(f);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("readme", StringComparison.OrdinalIgnoreCase)) continue;
                found.TryAdd(name, new AppEntry(name, f));
            }
        }
        _apps = found.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _scanned = DateTime.Now;
    }

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

    public IEnumerable<AppEntry> Search(string query)
    {
        var q = Normalize(query);
        if (q.Length == 0) return All;
        return All.Where(a => Normalize(a.Name).Contains(q) || Acronym(a.Name).StartsWith(q))
                  .OrderBy(a => Normalize(a.Name).StartsWith(q) ? 0 : 1).ThenBy(a => a.Name.Length);
    }

    public static void Launch(AppEntry app) =>
        Process.Start(new ProcessStartInfo(app.Path) { UseShellExecute = true });

    static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Replace("the ", "").Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();

    static string Acronym(string s) =>
        new string(s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToLowerInvariant(w[0])).ToArray());
}
