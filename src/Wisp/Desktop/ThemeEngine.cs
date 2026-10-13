using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using Wisp.Core;

namespace Wisp.Desktop;

/// <summary>
/// A theme pack: everything that makes the desktop look like itself. Lives in
/// %APPDATA%\Wisp\themes\&lt;id&gt;\theme.json (plus optional wallpapers and a logo), and can be
/// exported/imported as a single .wisptheme file (a zip). "brand" renames the product in the UI,
/// so a pack can rebrand the whole desktop.
/// </summary>
public sealed class ThemePack
{
    public string Id { get; set; } = "ember";
    public string Name { get; set; } = "Ember";
    public string Brand { get; set; } = "Wisp";
    public string Author { get; set; } = "";
    public bool Dark { get; set; } = true;

    public ThemeColors Colors { get; set; } = new();
    public string Font { get; set; } = "Segoe UI Variable Text, Segoe UI";
    public string DisplayFont { get; set; } = "Segoe UI Variable Display, Segoe UI";

    /// <summary>Palette for generated wallpapers (2–4 hex colors), used when the pack ships no images.</summary>
    public List<string> WallpaperPalette { get; set; } = ["#F0703A", "#7A2E5A", "#1B1B3A"];

    /// <summary>Apply the accent to Windows title bars and Start too.</summary>
    public bool AccentTitleBars { get; set; } = true;

    [JsonIgnore] public string? Folder { get; set; }
}

public sealed class ThemeColors
{
    public string Surface { get; set; } = "#FA141416";
    public string SurfaceRaised { get; set; } = "#FF1E1E22";
    public string SurfaceHover { get; set; } = "#FF26262B";
    public string SurfaceSelected { get; set; } = "#FF2E2A27";
    public string Hairline { get; set; } = "#1AFFFFFF";
    public string Text { get; set; } = "#FFF2F0ED";
    public string TextDim { get; set; } = "#FF9C9893";
    public string TextFaint { get; set; } = "#FF65625E";
    public string Accent { get; set; } = "#FFF0703A";
    public string AccentText { get; set; } = "#FF1A1210";
    public string Good { get; set; } = "#FF6FCF97";
    public string Danger { get; set; } = "#FFEF5B5B";
}

public sealed class ThemeEngine
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Root => Directory.CreateDirectory(Path.Combine(Paths.Root, "themes")).FullName;

    public ThemePack Current { get; private set; } = new();
    public event Action<ThemePack>? Applied;

    public IReadOnlyList<ThemePack> All()
    {
        SeedBuiltIns();
        var list = new List<ThemePack>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var file = Path.Combine(dir, "theme.json");
            if (!File.Exists(file)) continue;
            try
            {
                var pack = JsonSerializer.Deserialize<ThemePack>(File.ReadAllText(file), Json)!;
                pack.Folder = dir;
                pack.Id = Path.GetFileName(dir);
                list.Add(pack);
            }
            catch (Exception ex) { Log.Error($"Theme {dir} is invalid", ex); }
        }
        return list.OrderBy(p => p.Name).ToList();
    }

    public ThemePack? Find(string id) => All().FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Swaps every themed brush and font in place; open windows update live.</summary>
    public void Apply(ThemePack pack)
    {
        var res = Application.Current.Resources;
        var c = pack.Colors;
        Brush(res, "Surface", c.Surface, "SurfaceColor");
        Brush(res, "SurfaceRaised", c.SurfaceRaised);
        Brush(res, "SurfaceHover", c.SurfaceHover);
        Brush(res, "SurfaceSelected", c.SurfaceSelected);
        Brush(res, "Hairline", c.Hairline);
        Brush(res, "Text", c.Text);
        Brush(res, "TextDim", c.TextDim);
        Brush(res, "TextFaint", c.TextFaint);
        Brush(res, "Accent", c.Accent, "AccentColor");
        Brush(res, "AccentText", c.AccentText);
        Brush(res, "Good", c.Good);
        Brush(res, "Danger", c.Danger);
        var accent = Parse(c.Accent);
        Set(res, "AccentSoft", new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)));
        Set(res, "UiFont", new FontFamily(pack.Font));
        Set(res, "DisplayFont", new FontFamily(pack.DisplayFont));
        Current = pack;
        Applied?.Invoke(pack);
        Log.Info($"Theme applied: {pack.Name}");
    }

    static void Brush(ResourceDictionary res, string key, string hex, string? colorKey = null)
    {
        var color = Parse(hex);
        var b = new SolidColorBrush(color);
        b.Freeze();
        Set(res, key, b);
        if (colorKey is not null) Set(res, colorKey, color);
    }

    // Write to the top-level dictionary: it shadows Theme.xaml's defaults, and DynamicResource picks it up.
    static void Set(ResourceDictionary res, string key, object value) => res[key] = value;

    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static int AccentRgb(ThemePack p)
    {
        var a = Parse(p.Colors.Accent);
        return (a.R << 16) | (a.G << 8) | a.B;
    }

    // ------------------------------------------------------------------ packaging

    /// <summary>Zips a theme folder into a shareable .wisptheme file.</summary>
    public static void Export(ThemePack pack, string destination)
    {
        if (pack.Folder is null) throw new InvalidOperationException("Theme has no folder.");
        if (File.Exists(destination)) File.Delete(destination);
        ZipFile.CreateFromDirectory(pack.Folder, destination);
    }

    /// <summary>Installs a .wisptheme file; returns the installed pack.</summary>
    public ThemePack Import(string file)
    {
        using var zip = ZipFile.OpenRead(file);
        var manifest = zip.GetEntry("theme.json") ?? throw new InvalidDataException("Not a Wisp theme: theme.json is missing.");
        ThemePack pack;
        using (var s = manifest.Open()) pack = JsonSerializer.Deserialize<ThemePack>(s, Json)!;
        var id = PluginSafe(string.IsNullOrWhiteSpace(pack.Id) ? Path.GetFileNameWithoutExtension(file) : pack.Id);
        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var target = Path.GetFullPath(Path.Combine(dir, entry.FullName));
            if (!target.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar)) continue; // no zip-slip
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
        return Find(id)!;
    }

    /// <summary>Saves a copy of a pack under a new name and brand, ready to tweak.</summary>
    public ThemePack Duplicate(ThemePack source, string name, string brand)
    {
        var id = PluginSafe(name);
        var dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);
        if (source.Folder is not null)
            foreach (var f in Directory.EnumerateFiles(source.Folder))
                if (!f.EndsWith("theme.json")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
        var copy = JsonSerializer.Deserialize<ThemePack>(JsonSerializer.Serialize(source, Json), Json)!;
        copy.Id = id; copy.Name = name; copy.Brand = brand;
        File.WriteAllText(Path.Combine(dir, "theme.json"), JsonSerializer.Serialize(copy, Json));
        return Find(id)!;
    }

    public static bool IsBuiltIn(string id) => BuiltIns().Any(b => b.Id == id);

    public static void Save(ThemePack pack)
    {
        if (pack.Folder is null) return;
        File.WriteAllText(Path.Combine(pack.Folder, "theme.json"), JsonSerializer.Serialize(pack, Json));
    }

    static string PluginSafe(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    // ------------------------------------------------------------------ built-ins

    static bool _seeded;

    static void SeedBuiltIns()
    {
        if (_seeded) return;
        _seeded = true;
        foreach (var pack in BuiltIns())
        {
            var dir = Path.Combine(Root, pack.Id);
            var file = Path.Combine(dir, "theme.json");
            if (File.Exists(file)) continue;
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, JsonSerializer.Serialize(pack, Json));
        }
    }

    static IEnumerable<ThemePack> BuiltIns()
    {
        yield return new ThemePack(); // Ember: the default

        yield return new ThemePack
        {
            Id = "aubergine", Name = "Aubergine",
            Colors = new()
            {
                Surface = "#FA1E1220", SurfaceRaised = "#FF2A1A2C", SurfaceHover = "#FF352236", SurfaceSelected = "#FF3E2534",
                Text = "#FFF6EEF3", TextDim = "#FFAA97A6", TextFaint = "#FF6E5D6B", Accent = "#FFE95420", AccentText = "#FF1E0E08",
            },
            WallpaperPalette = ["#E95420", "#77216F", "#2C001E"],
        };

        yield return new ThemePack
        {
            Id = "nord", Name = "Nord",
            Colors = new()
            {
                Surface = "#F82E3440", SurfaceRaised = "#FF3B4252", SurfaceHover = "#FF434C5E", SurfaceSelected = "#FF4C566A",
                Text = "#FFECEFF4", TextDim = "#FFB4BCCB", TextFaint = "#FF7B8496", Accent = "#FF88C0D0", AccentText = "#FF1B2430",
                Good = "#FFA3BE8C", Danger = "#FFBF616A",
            },
            WallpaperPalette = ["#88C0D0", "#5E81AC", "#2E3440"],
        };

        yield return new ThemePack
        {
            Id = "forest", Name = "Forest",
            Colors = new()
            {
                Surface = "#FA121714", SurfaceRaised = "#FF1A211C", SurfaceHover = "#FF222B25", SurfaceSelected = "#FF26332A",
                Text = "#FFEEF2EC", TextDim = "#FF97A298", TextFaint = "#FF5F6B61", Accent = "#FF7FD18B", AccentText = "#FF0E1A10",
            },
            WallpaperPalette = ["#7FD18B", "#2F6B4F", "#0F1F18"],
        };

        yield return new ThemePack
        {
            Id = "paper", Name = "Paper", Dark = false, AccentTitleBars = false,
            Colors = new()
            {
                Surface = "#F5FAF8F5", SurfaceRaised = "#FFFFFFFF", SurfaceHover = "#FFEFEBE6", SurfaceSelected = "#FFF6E3D8",
                Hairline = "#1A000000", Text = "#FF1F1C19", TextDim = "#FF6A645E", TextFaint = "#FFA39C95",
                Accent = "#FFD9602A", AccentText = "#FFFFFFFF",
            },
            WallpaperPalette = ["#F3D9C4", "#E8B796", "#F7F2EC"],
        };
    }
}
