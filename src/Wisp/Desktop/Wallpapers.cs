using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Wisp.Core;
using Wisp.Native;

namespace Wisp.Desktop;

/// <summary>How an image is fitted to a screen whose size/shape differs from the image.</summary>
public enum WallpaperFit { Fill, Fit, Stretch, Center, Span }

/// <summary>
/// Wallpaper library and setting the desktop background.
/// Sources: wallpapers bundled with Wisp, images the user added, ones generated from the theme,
/// and an optional user folder. Setting goes through Windows' IDesktopWallpaper, which supports
/// per-monitor wallpapers and every fit mode.
/// </summary>
public static class Wallpapers
{
    public static string Generated => Directory.CreateDirectory(Path.Combine(Paths.Root, "wallpapers")).FullName;
    public static string Mine => Directory.CreateDirectory(Path.Combine(Paths.Root, "wallpapers", "mine")).FullName;
    public static string Bundled => Path.Combine(AppContext.BaseDirectory, "Wallpapers");
    public static string Credits => Path.Combine(Bundled, "CREDITS.md");

    static readonly string[] Exts = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".tif", ".tiff"];

    public static bool IsImage(string f) => Exts.Contains(Path.GetExtension(f).ToLowerInvariant());

    public static IEnumerable<string> Included() =>
        Directory.Exists(Bundled) ? Directory.EnumerateFiles(Bundled).Where(IsImage).OrderBy(f => f) : [];

    public static IEnumerable<string> UserAdded() =>
        Directory.EnumerateFiles(Mine).Where(IsImage).OrderByDescending(File.GetCreationTime);

    public static IEnumerable<string> FromTheme(ThemePack theme)
    {
        var files = new List<string>();
        if (theme.Folder is not null) files.AddRange(Directory.EnumerateFiles(theme.Folder).Where(IsImage));
        files.AddRange(Directory.EnumerateFiles(Generated, $"{theme.Id}-*.png"));
        return files;
    }

    public static IEnumerable<string> FromFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(IsImage).Take(300)
            : [];

    /// <summary>Copies an image into Wisp's library (so it survives the original moving) and returns the copy.</summary>
    public static string Import(string file)
    {
        var dest = Path.Combine(Mine, Path.GetFileName(file));
        if (File.Exists(dest) && !SameFile(file, dest))
            dest = Path.Combine(Mine, $"{Path.GetFileNameWithoutExtension(file)}-{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(file)}");
        if (!SameFile(file, dest)) File.Copy(file, dest, overwrite: true);
        return dest;
    }

    static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Sets the wallpaper on every screen, or on one screen (by device name), with the given fit.</summary>
    public static void Set(string path, WallpaperFit fit = WallpaperFit.Fill, string? screenDevice = null)
    {
        try
        {
            var dw = (IDesktopWallpaper)new DesktopWallpaperClass();
            dw.SetPosition(fit switch
            {
                WallpaperFit.Fit => DesktopWallpaperPosition.Fit,
                WallpaperFit.Stretch => DesktopWallpaperPosition.Stretch,
                WallpaperFit.Center => DesktopWallpaperPosition.Center,
                WallpaperFit.Span => DesktopWallpaperPosition.Span,
                _ => DesktopWallpaperPosition.Fill,
            });
            string? monitorId = screenDevice is null || fit == WallpaperFit.Span ? null : MonitorIdFor(dw, screenDevice);
            dw.SetWallpaper(monitorId, path);
            Marshal.ReleaseComObject(dw);
            Log.Info($"Wallpaper set: {path} ({fit}, {monitorId ?? "all screens"})");
        }
        catch (Exception ex)
        {
            // Fallback for odd setups: the classic API (all screens, current fit).
            Log.Error("IDesktopWallpaper failed; using SystemParametersInfo", ex);
            Win32.SystemParametersInfo(Win32.SPI_SETDESKWALLPAPER, 0, path, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);
        }
    }

    /// <summary>Matches a screen (e.g. \\.\DISPLAY2) to IDesktopWallpaper's monitor id by its rectangle.</summary>
    static string? MonitorIdFor(IDesktopWallpaper dw, string device)
    {
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device);
        if (screen is null) return null;
        uint count = dw.GetMonitorDevicePathCount();
        for (uint i = 0; i < count; i++)
        {
            var id = dw.GetMonitorDevicePathAt(i);
            var r = dw.GetMonitorRECT(id);
            if (r.Left == screen.Bounds.Left && r.Top == screen.Bounds.Top) return id;
        }
        return null;
    }

    // ------------------------------------------------------------------ IDesktopWallpaper (shobjidl.h)

    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    class DesktopWallpaperClass { }

    enum DesktopWallpaperPosition { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
        uint GetMonitorDevicePathCount();
        RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(DesktopWallpaperPosition position);
        DesktopWallpaperPosition GetPosition();
        void SetSlideshow(IntPtr items);
        IntPtr GetSlideshow();
        void SetSlideshowOptions(uint options, uint slideshowTick);
        void GetSlideshowOptions(out uint options, out uint slideshowTick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, uint direction);
        uint GetStatus();
        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    // ------------------------------------------------------------------ theme-generated wallpapers

    /// <summary>Renders a set of soft "aurora" wallpapers from a theme's palette, at the primary screen's size.</summary>
    public static void EnsureGenerated(ThemePack theme)
    {
        var size = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Size;
        for (int variant = 0; variant < 4; variant++)
        {
            var file = Path.Combine(Generated, $"{theme.Id}-{variant}-{size.Width}x{size.Height}.png");
            if (File.Exists(file)) continue;
            using var bmp = Render(theme, size, variant);
            bmp.Save(file, ImageFormat.Png);
        }
    }

    static Bitmap Render(ThemePack theme, Size size, int variant)
    {
        var palette = theme.WallpaperPalette.Select(h => ToDrawing(ThemeEngine.Parse(h))).ToList();
        if (palette.Count == 0) palette.Add(Color.FromArgb(240, 112, 58));
        var bg = ToDrawing(ThemeEngine.Parse(theme.Colors.Surface));
        bg = Color.FromArgb(255, bg.R, bg.G, bg.B);

        // Render small and upscale: cheap, and the bilinear upscale makes the blobs perfectly soft.
        int w = 480, h = Math.Max(1, (int)(480.0 * size.Height / size.Width));
        using var small = new Bitmap(w, h);
        var rnd = new Random(theme.Id.Aggregate(17, (acc, ch) => acc * 31 + ch) ^ (variant * 7919)); // stable across runs
        using (var g = Graphics.FromImage(small))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(bg);
            int blobs = 5 + variant;
            for (int i = 0; i < blobs; i++)
            {
                var c = palette[(i + variant) % palette.Count];
                float r = (float)(w * (0.25 + rnd.NextDouble() * 0.45));
                float x = (float)(rnd.NextDouble() * w), y = (float)(h * (0.25 + rnd.NextDouble() * 0.9));
                using var path = new GraphicsPath();
                path.AddEllipse(x - r, y - r * 0.7f, r * 2, r * 1.4f);
                using var brush = new PathGradientBrush(path)
                {
                    CenterColor = Color.FromArgb(theme.Dark ? 150 : 170, c),
                    SurroundColors = [Color.FromArgb(0, c)],
                };
                g.FillPath(brush, path);
            }
            // Darken the top so the bar and island read clearly.
            using var top = new LinearGradientBrush(new Rectangle(0, 0, w, h / 3 + 1),
                Color.FromArgb(theme.Dark ? 120 : 40, bg), Color.FromArgb(0, bg), 90f);
            g.FillRectangle(top, 0, 0, w, h / 3);
        }

        var full = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(full))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(small, 0, 0, size.Width, size.Height);
        }
        AddGrain(full, rnd);
        return full;
    }

    /// <summary>A whisper of noise so gradients don't band on 8-bit panels.</summary>
    static void AddGrain(Bitmap bmp, Random rnd)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < data.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < data.Width; x++)
                    {
                        int n = rnd.Next(-3, 4);
                        for (int ch = 0; ch < 3; ch++)
                            row[x * 4 + ch] = (byte)Math.Clamp(row[x * 4 + ch] + n, 0, 255);
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    static Color ToDrawing(System.Windows.Media.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
