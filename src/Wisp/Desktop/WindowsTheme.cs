using System.Runtime.InteropServices;
using Microsoft.Win32;
using Wisp.Core;

namespace Wisp.Desktop;

/// <summary>Saved copy of every registry value Wisp changes, so turning the look off puts Windows back exactly.</summary>
public sealed class ThemeSnapshot
{
    public Dictionary<string, object?> Values { get; set; } = [];
}

/// <summary>
/// Re-themes Windows to match Wisp: dark mode for apps and system, the warm accent on title bars
/// and Start, transparency on. Uses the same per-user registry values the Settings app writes,
/// then broadcasts the change so open windows repaint.
/// </summary>
public static class WindowsTheme
{
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string Dwm = @"Software\Microsoft\Windows\DWM";
    const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    const string Desktop = @"Control Panel\Desktop";

    static readonly (string Key, string Name)[] Touched =
    [
        (Personalize, "AppsUseLightTheme"), (Personalize, "SystemUsesLightTheme"),
        (Personalize, "EnableTransparency"), (Personalize, "ColorPrevalence"),
        (Dwm, "AccentColor"), (Dwm, "ColorizationColor"), (Dwm, "ColorizationAfterglow"), (Dwm, "ColorPrevalence"),
        (Accent, "AccentColorMenu"), (Accent, "StartColorMenu"), (Accent, "AccentPalette"),
        (Desktop, "AutoColorization"),
    ];

    public static ThemeSnapshot Capture()
    {
        var snap = new ThemeSnapshot();
        foreach (var (key, name) in Touched)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            var v = k?.GetValue(name);
            // byte[] and int both round-trip through JSON as base64 / number.
            snap.Values[$"{key}|{name}"] = v is byte[] b ? "b64:" + Convert.ToBase64String(b) : v;
        }
        return snap;
    }

    public static void Restore(ThemeSnapshot snap)
    {
        foreach (var (id, value) in snap.Values)
        {
            var (key, name) = (id[..id.IndexOf('|')], id[(id.IndexOf('|') + 1)..]);
            using var k = Registry.CurrentUser.CreateSubKey(key);
            switch (value)
            {
                case null: k.DeleteValue(name, false); break;
                case System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number:
                    k.SetValue(name, unchecked((int)je.GetInt64()), RegistryValueKind.DWord); break;
                case System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String:
                    SetString(k, name, je.GetString()!); break;
                case string s: SetString(k, name, s); break;
                case int i: k.SetValue(name, i, RegistryValueKind.DWord); break;
                case long l: k.SetValue(name, unchecked((int)l), RegistryValueKind.DWord); break;
            }
        }
        Broadcast();
        Log.Info("Windows theme restored");
    }

    static void SetString(RegistryKey k, string name, string s)
    {
        if (s.StartsWith("b64:")) k.SetValue(name, Convert.FromBase64String(s[4..]), RegistryValueKind.Binary);
        else k.SetValue(name, s);
    }

    /// <param name="accentRgb">Accent as 0xRRGGBB.</param>
    public static void Apply(bool dark, bool accentOnTitleBars, int accentRgb)
    {
        if (AlreadyApplied(dark, accentOnTitleBars, accentRgb)) return; // nothing to change, nothing to announce

        using (var p = Registry.CurrentUser.CreateSubKey(Personalize))
        {
            p.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            p.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            p.SetValue("EnableTransparency", 1, RegistryValueKind.DWord);
            p.SetValue("ColorPrevalence", accentOnTitleBars ? 1 : 0, RegistryValueKind.DWord);
        }

        int r = (accentRgb >> 16) & 0xFF, g = (accentRgb >> 8) & 0xFF, b = accentRgb & 0xFF;
        int abgr = unchecked((int)(0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | (uint)r));
        int argb = unchecked((int)(0xC4000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b));

        using (var d = Registry.CurrentUser.CreateSubKey(Dwm))
        {
            d.SetValue("AccentColor", abgr, RegistryValueKind.DWord);
            d.SetValue("ColorizationColor", argb, RegistryValueKind.DWord);
            d.SetValue("ColorizationAfterglow", argb, RegistryValueKind.DWord);
            d.SetValue("ColorPrevalence", accentOnTitleBars ? 1 : 0, RegistryValueKind.DWord);
        }

        using (var a = Registry.CurrentUser.CreateSubKey(Accent))
        {
            a.SetValue("AccentColorMenu", abgr, RegistryValueKind.DWord);
            a.SetValue("StartColorMenu", ToAbgr(Shade(r, g, b, -0.45)), RegistryValueKind.DWord);
            a.SetValue("AccentPalette", Palette(r, g, b), RegistryValueKind.Binary);
        }

        using (var desk = Registry.CurrentUser.CreateSubKey(Desktop))
            desk.SetValue("AutoColorization", "0");

        Broadcast();
        Log.Info($"Windows theme applied (dark={dark}, accent bars={accentOnTitleBars})");
    }

    static bool AlreadyApplied(bool dark, bool accentOnTitleBars, int accentRgb)
    {
        int r = (accentRgb >> 16) & 0xFF, g = (accentRgb >> 8) & 0xFF, b = accentRgb & 0xFF;
        int abgr = unchecked((int)(0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | (uint)r));
        using var p = Registry.CurrentUser.OpenSubKey(Personalize);
        using var d = Registry.CurrentUser.OpenSubKey(Dwm);
        return p?.GetValue("AppsUseLightTheme") is int apps && apps == (dark ? 0 : 1)
            && p.GetValue("SystemUsesLightTheme") is int sys && sys == (dark ? 0 : 1)
            && p.GetValue("ColorPrevalence") is int cp && cp == (accentOnTitleBars ? 1 : 0)
            && d?.GetValue("AccentColor") is int ac && ac == abgr;
    }

    /// <summary>The 8-swatch palette Windows derives from an accent: light tints to dark shades (RGBA).</summary>
    static byte[] Palette(int r, int g, int b)
    {
        double[] steps = [0.55, 0.35, 0.15, 0, -0.2, -0.38, -0.55, -0.7];
        var bytes = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            var (cr, cg, cb) = Shade(r, g, b, steps[i]);
            bytes[i * 4] = (byte)cr; bytes[i * 4 + 1] = (byte)cg; bytes[i * 4 + 2] = (byte)cb; bytes[i * 4 + 3] = 0;
        }
        return bytes;
    }

    static (int, int, int) Shade(int r, int g, int b, double amount) => amount >= 0
        ? ((int)(r + (255 - r) * amount), (int)(g + (255 - g) * amount), (int)(b + (255 - b) * amount))
        : ((int)(r * (1 + amount)), (int)(g * (1 + amount)), (int)(b * (1 + amount)));

    static int ToAbgr((int r, int g, int b) c) =>
        unchecked((int)(0xFF000000u | ((uint)c.b << 16) | ((uint)c.g << 8) | (uint)c.r));

    public static bool IsDark()
    {
        using var p = Registry.CurrentUser.OpenSubKey(Personalize);
        return p?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

    /// <summary>
    /// Tells every window the theme changed. Runs on a worker thread: a synchronous broadcast from
    /// the UI thread deadlocks against .NET's SystemEvents thread (which Invokes back onto the UI
    /// thread) once the tray host is relaying shell messages to Explorer.
    /// </summary>
    static void Broadcast() => Task.Run(() =>
    {
        const uint WM_SETTINGCHANGE = 0x1A, SMTO_ABORTIFHUNG = 2;
        var HWND_BROADCAST = new IntPtr(0xFFFF);
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 200, out _);
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "WindowsThemeElement", SMTO_ABORTIFHUNG, 200, out _);
    });
}
