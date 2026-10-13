using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Wisp.Core;
using Wisp.Desktop;
using Wisp.Native;

namespace Wisp.UI;

public sealed record LaunchResult(string Title, string Subtitle, string Kind, string Glyph, ImageSource? Icon, Action Run);

/// <summary>
/// Keyboard-first launcher (tap Win, or click the brand): apps, open windows, settings pages,
/// system commands, inline math, web search, and "ask" as the fallback for anything else.
/// </summary>
public partial class LauncherWindow : Window
{
    readonly WispController _app;
    bool _closing;

    static readonly (string Name, string Uri, string Glyph)[] SettingsPages =
    [
        ("Display", "ms-settings:display", "\uE7F4"), ("Sound", "ms-settings:sound", "\uE767"),
        ("Bluetooth & devices", "ms-settings:bluetooth", "\uE702"), ("Wi-Fi", "ms-settings:network-wifi", "\uE701"),
        ("Network & internet", "ms-settings:network", "\uE839"), ("Apps", "ms-settings:appsfeatures", "\uE71D"),
        ("Default apps", "ms-settings:defaultapps", "\uE71D"), ("Personalization", "ms-settings:personalization", "\uE771"),
        ("Windows Update", "ms-settings:windowsupdate", "\uE895"), ("Power & battery", "ms-settings:powersleep", "\uE7E8"),
        ("Storage", "ms-settings:storagesense", "\uEDA2"), ("Mouse", "ms-settings:mousetouchpad", "\uE962"),
        ("Keyboard", "ms-settings:keyboard", "\uE765"), ("Notifications", "ms-settings:notifications", "\uEA8F"),
        ("Printers & scanners", "ms-settings:printers", "\uE749"), ("Date & time", "ms-settings:dateandtime", "\uE787"),
        ("Task Manager", "taskmgr", "\uE9D9"), ("Control Panel", "control", "\uE713"),
    ];

    public LauncherWindow(WispController app)
    {
        _app = app;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTracker.IgnoreWindow(new WindowInteropHelper(this).Handle);
        Deactivated += (_, _) => SafeClose();
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + wa.Height * 0.16;
            Activate();
            Win32.ForceForeground(new WindowInteropHelper(this).Handle);
            Query.Focus();
            Update();
        };
    }

    public void Prefill(string text) => Query.Text = text;

    void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    void Query_TextChanged(object s, System.Windows.Controls.TextChangedEventArgs e)
    {
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Update();
    }

    void Update()
    {
        var q = Query.Text.Trim();
        var results = new List<LaunchResult>();

        if (TryMath(q, out var value))
            results.Add(new("= " + value, "Press Enter to copy", "Math", "\uE8EF", null, () => Clipboard.SetText(value)));

        var apps = _app.Apps.Search(q).Take(q.Length == 0 ? 8 : 6).ToList();
        results.AddRange(apps.Select(a => new LaunchResult(a.Name, "", "App", "", ShellIcons.Get(a.Path, 48), () => AppIndex.Launch(a))));

        if (q.Length > 0)
        {
            foreach (var w in WindowTracker.List().Where(w => w.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                                              w.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(4))
                results.Add(new(w.Title, w.ProcessName, "Window", "", ShellIcons.Get(w.ExePath, 48),
                    () => WindowTracker.Activate(w.Handle, allowMinimize: false)));

            foreach (var page in SettingsPages.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(3))
                results.Add(new(page.Name, "Settings", "Settings", page.Glyph, null,
                    () => Process.Start(new ProcessStartInfo(page.Uri) { UseShellExecute = true })));

            foreach (var cmd in Commands().Where(c => c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(3))
                results.Add(cmd);

            results.Add(new($"Ask {_app.Themes.Current.Brand}: “{q}”", "Your assistant runs it", "Ask", "\uE945", null,
                () => _ = _app.AskAsync(q, null, keepPanel: false)));
            results.Add(new($"Search the web for “{q}”", "Default browser", "Web", "\uE774", null,
                () => Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={Uri.EscapeDataString(q)}") { UseShellExecute = true })));
        }

        Results.ItemsSource = results;
        if (results.Count > 0) Results.SelectedIndex = 0;
    }

    IEnumerable<LaunchResult> Commands()
    {
        yield return new("Lock", "Lock the PC", "Command", "\uE72E", null, () => Win32.LockWorkStation());
        yield return new("Sleep", "Put the PC to sleep", "Command", "\uE708", null, () => Win32.SetSuspendState(false, false, false));
        yield return new("Restart", "Restart the PC", "Command", "\uE777", null, () => _app.Island.OpenPanel("power"));
        yield return new("Shut down", "Turn off the PC", "Command", "\uE7E8", null, () => _app.Island.OpenPanel("power"));
        yield return new($"{_app.Themes.Current.Brand} settings", "Keys, voice, plugins", "Command", "\uE713", null, () => _app.ShowSettings());
        yield return new("Desktop controls", "Taskbar, dock, theme, wallpaper", "Command", "\uE771", null, () => _app.Island.OpenPanel("controls"));
        yield return new("Change wallpaper", "Pick a wallpaper", "Command", "\uE91B", null, () => _app.Island.OpenPanel("wallpaper"));
    }

    static readonly Regex MathChars = new(@"^[\d\s\.\+\-\*/\(\)%]+$");

    static bool TryMath(string q, out string value)
    {
        value = "";
        if (q.Length < 3 || !MathChars.IsMatch(q) || !q.Any(c => "+-*/%".Contains(c))) return false;
        try
        {
            var result = new DataTable().Compute(q.Replace("%", "/100*"), null);
            value = Convert.ToDouble(result).ToString("G12");
            return true;
        }
        catch { return false; }
    }

    void Query_KeyDown(object s, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: SafeClose(); e.Handled = true; break;
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Enter: RunSelected(); e.Handled = true; break;
        }
    }

    void Move(int d)
    {
        if (Results.Items.Count == 0) return;
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + d, 0, Results.Items.Count - 1);
        Results.ScrollIntoView(Results.SelectedItem);
    }

    void Results_Click(object s, MouseButtonEventArgs e) => RunSelected();

    void RunSelected()
    {
        if (Results.SelectedItem is not LaunchResult r) return;
        SafeClose();
        try { r.Run(); }
        catch (Exception ex) { Log.Error($"Launcher action '{r.Title}' failed", ex); _app.Island.ShowError(ex.Message); }
    }
}
