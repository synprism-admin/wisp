using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Wisp.Core;
using Wisp.Native;

namespace Wisp.Desktop;

/// <summary>Wallpaper library (theme-generated + user folder) and setting the desktop background.</summary>
public static class Wallpapers
{
    public static string Generated => Directory.CreateDirectory(Path.Combine(Paths.Root, "wallpapers")).FullName;

    static readonly string[] Exts = [".jpg", ".jpeg", ".png", ".bmp", ".webp"];

    public static IEnumerable<string> List(ThemePack theme, string? userFolder)
    {
        var files = new List<string>();
        if (theme.Folder is not null)
            files.AddRange(Directory.EnumerateFiles(theme.Folder).Where(IsImage));
        files.AddRange(Directory.EnumerateFiles(Generated, $"{theme.Id}-*.png"));
        if (!string.IsNullOrWhiteSpace(userFolder) && Directory.Exists(userFolder))
            files.AddRange(Directory.EnumerateFiles(userFolder).Where(IsImage).Take(200));
        return files;
    }

    static bool IsImage(string f) => Exts.Contains(Path.GetExtension(f).ToLowerInvariant());

    public static void Set(string path)
    {
        Win32.SystemParametersInfo(Win32.SPI_SETDESKWALLPAPER, 0, path, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);
        Log.Info($"Wallpaper set: {path}");
    }

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
