using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisp.Core;

public static class Paths
{
    public static string Root { get; } = Directory.CreateDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp")).FullName;

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string Database => Path.Combine(Root, "wisp.db");
    public static string Plugins => Directory.CreateDirectory(Path.Combine(Root, "plugins")).FullName;
    public static string Models => Directory.CreateDirectory(Path.Combine(Root, "models")).FullName;
    public static string Logs => Directory.CreateDirectory(Path.Combine(Root, "logs")).FullName;
}

public enum ProviderKind { Claude, Gemini }

public sealed class Settings
{
    // Which provider runs the assistant/agent, vision questions and dictation cleanup.
    public ProviderKind Provider { get; set; } = ProviderKind.Claude;

    public string ClaudeModel { get; set; } = "claude-opus-5-5";
    public string GeminiModel { get; set; } = "gemini-flash-latest";

    // Effort for agent work vs. the quick dictation-cleanup pass (Claude only).
    public string AgentEffort { get; set; } = "medium";
    public string CleanupEffort { get; set; } = "low";

    // Encrypted with DPAPI (CurrentUser); never stored in plain text.
    public string? ClaudeKeyProtected { get; set; }
    public string? GeminiKeyProtected { get; set; }

    // Local speech-to-text.
    public string WhisperModel { get; set; } = "LargeV3Turbo";
    public string Language { get; set; } = "en";
    public int MicrophoneDevice { get; set; } = 0;

    // Dictation: run the transcript through the LLM to strip filler and self-corrections.
    public bool DictationCleanup { get; set; } = true;

    // Agent safety: tools flagged as risky ask before running.
    public bool ConfirmRiskyActions { get; set; } = true;
    public int AgentMaxSteps { get; set; } = 24;

    public string UserName { get; set; } = Environment.UserName;
    public string Persona { get; set; } = "";

    public bool LaunchAtStartup { get; set; } = false;

    public DesktopSettings Desktop { get; set; } = new();
    public string ThemeId { get; set; } = "ember";

    [JsonIgnore]
    public string? ClaudeKey
    {
        get => Unprotect(ClaudeKeyProtected) ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        set => ClaudeKeyProtected = Protect(value);
    }

    [JsonIgnore]
    public string? GeminiKey
    {
        get => Unprotect(GeminiKeyProtected) ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        set => GeminiKeyProtected = Protect(value);
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Settings load failed; using defaults", ex);
        }
        return new();
    }

    public void Save()
    {
        var tmp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, Paths.SettingsFile, overwrite: true);
        Changed?.Invoke();
    }

    public event Action? Changed;

    static string? Protect(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain.Trim()), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }
}

/// <summary>Wisp's desktop look. Everything here is reversible; originals are kept in OriginalTheme.</summary>
public sealed class DesktopSettings
{
    public bool HideTaskbar { get; set; }
    public bool ShowDock { get; set; }
    public bool ShowTopBar { get; set; }
    public bool ThemeWindows { get; set; }
    public bool AccentTitleBars { get; set; } = true;
    public bool HideDesktopIcons { get; set; }
    public bool WinKeyOpensLauncher { get; set; }
    public List<string> Pins { get; set; } = [];
    public bool PinsInitialized { get; set; }
    public string? WallpaperFolder { get; set; }
    public string? Wallpaper { get; set; }
    public Wisp.Desktop.ThemeSnapshot? OriginalTheme { get; set; }
}

public static class Log
{
    static readonly object Gate = new();
    static string File => Path.Combine(Paths.Logs, $"wisp-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}";
        lock (Gate)
        {
            try { System.IO.File.AppendAllText(File, line); } catch { }
        }
        System.Diagnostics.Debug.Write(line);
    }
}
