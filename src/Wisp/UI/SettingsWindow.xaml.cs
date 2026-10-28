using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Wisp.AI;
using Wisp.Core;
using Wisp.Speech;

namespace Wisp.UI;

public partial class SettingsWindow : Window
{
    readonly WispController _app;
    static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    static readonly (string Id, string Label)[] WhisperModels =
    [
        ("LargeV3Turbo", "Large v3 Turbo: best accuracy, fast on a GPU (~550 MB)"),
        ("Medium", "Medium: good accuracy (~1.5 GB)"),
        ("Small", "Small: lighter, fine for clear speech (~470 MB)"),
        ("Base", "Base: fastest, least accurate (~140 MB)"),
    ];

    public SettingsWindow(WispController app)
    {
        _app = app;
        InitializeComponent();
        Load();
        _app.Transcriber.StatusChanged += OnWhisperStatus;
        _app.Plugins.Reloaded += OnPluginsReloaded;
        Closed += (_, _) =>
        {
            _app.Transcriber.StatusChanged -= OnWhisperStatus;
            _app.Plugins.Reloaded -= OnPluginsReloaded;
        };
    }

    void OnWhisperStatus(string s) => Dispatcher.BeginInvoke(() => WhisperStatus.Text = s);
    void OnPluginsReloaded() => Dispatcher.BeginInvoke(ShowPlugins);

    void Load()
    {
        var s = _app.Settings;
        AiEnabled.IsChecked = s.AiEnabled;
        UseClaude.IsChecked = s.Provider == ProviderKind.Claude;
        UseGemini.IsChecked = s.Provider == ProviderKind.Gemini;
        ShowKeys();
        ShowUsage();
        MonthlyLimit.Text = s.MonthlyLimitUsd > 0 ? s.MonthlyLimitUsd.ToString("0.##") : "";
        _ = LoadModelsAsync();
        AgentEffort.ItemsSource = Efforts;
        AgentEffort.SelectedItem = Efforts.Contains(s.AgentEffort) ? s.AgentEffort : "medium";

        var mics = Recorder.Devices();
        Mic.ItemsSource = mics.Count > 0 ? mics : ["(no microphone found)"];
        Mic.SelectedIndex = Math.Clamp(s.MicrophoneDevice, 0, Math.Max(0, mics.Count - 1));
        WhisperModel.ItemsSource = WhisperModels.Select(m => m.Label).ToList();
        WhisperModel.SelectedIndex = Math.Max(0, Array.FindIndex(WhisperModels, m => m.Id == s.WhisperModel));
        WhisperStatus.Text = _app.Transcriber.Status;
        Language.Text = s.Language;
        Cleanup.IsChecked = s.DictationCleanup;

        UserName.Text = s.UserName;
        Persona.Text = s.Persona;
        Confirm.IsChecked = s.ConfirmRiskyActions;
        Startup.IsChecked = s.LaunchAtStartup;
        AutoUpdates.IsChecked = s.AutoCheckUpdates;
        ShowVersion();
        ShowPlugins();
    }

    /// <summary>Fills both model dropdowns: live from each provider when a key is set, built-in otherwise.</summary>
    async Task LoadModelsAsync()
    {
        var s = _app.Settings;
        ModelsHint.Text = "Loading models…";
        var claude = await ModelCatalog.ClaudeAsync(s.ClaudeKey);
        var gemini = await ModelCatalog.GeminiAsync(s.GeminiKey);
        Fill(ClaudeModel, claude, s.ClaudeModel);
        Fill(GeminiModel, gemini, s.GeminiModel);
        ModelsHint.Text = (s.ClaudeKey is null ? "Claude: suggested models (add a key to see your full list). " : "") +
                          (s.GeminiKey is null ? "Gemini: suggested models (add a key to see your full list)." : "");
    }

    /// <summary>Selects the saved model; if it isn't offered any more, keeps it visible as-is so nothing changes silently.</summary>
    static void Fill(System.Windows.Controls.ComboBox box, IReadOnlyList<ModelChoice> models, string current)
    {
        var list = models.ToList();
        if (!string.IsNullOrWhiteSpace(current) && list.All(m => m.Id != current))
            list.Insert(0, new ModelChoice(current, $"{current} (current)"));
        box.ItemsSource = list;
        box.SelectedItem = list.FirstOrDefault(m => m.Id == current) ?? list.FirstOrDefault();
    }

    async void RefreshModels_Click(object s, RoutedEventArgs e) => await LoadModelsAsync();

    void ShowKeys()
    {
        var s = _app.Settings;
        ClaudeKeyHint.Text = s.ClaudeKey is { Length: > 8 } k ? $"Saved: …{k[^4..]}. Leave blank to keep it." : "No key saved. Get one at platform.claude.com.";
        GeminiKeyHint.Text = s.GeminiKey is { Length: > 8 } g ? $"Saved: …{g[^4..]}. Leave blank to keep it." : "No key saved. Free from Google AI Studio (aistudio.google.com).";
        RemoveClaudeKey.Visibility = s.ClaudeKeyProtected is not null ? Visibility.Visible : Visibility.Collapsed;
        RemoveGeminiKey.Visibility = s.GeminiKeyProtected is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowUsage()
    {
        var rows = _app.Store.UsageThisMonth();
        long calls = rows.Sum(r => r.Calls), tokens = rows.Sum(r => r.Input + r.Output);
        var cost = rows.Where(r => r.Cost is not null).Sum(r => r.Cost!.Value);
        bool unpriced = rows.Any(r => r.Cost is null);
        UsageSummary.Text = calls == 0
            ? $"No AI requests yet in {DateTime.Now:MMMM}."
            : $"≈ ${cost:0.00} estimated  ·  {calls:N0} requests  ·  {Short(tokens)} tokens" + (unpriced ? " (Gemini not priced)" : "");
        UsageDetail.Text = string.Join("\n", rows.Select(r =>
            $"{r.Provider} {r.Model}: {r.Calls:N0} requests, {Short(r.Input)} in / {Short(r.Output)} out" + (r.Cost is { } c ? $", ≈ ${c:0.00}" : "")));
        if (_app.Settings.MonthlyLimitUsd > 0) UsageDetail.Text += $"\nLimit: ${_app.Settings.MonthlyLimitUsd:0.##} per month";
    }

    static string Short(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M" : n >= 1_000 ? $"{n / 1_000.0:0.#}k" : n.ToString();

    void RemoveClaudeKey_Click(object s, RoutedEventArgs e) => RemoveKey(claude: true);
    void RemoveGeminiKey_Click(object s, RoutedEventArgs e) => RemoveKey(claude: false);

    void RemoveKey(bool claude)
    {
        var name = claude ? "Claude" : "Gemini";
        if (!NoticeWindow.Ask($"Remove your {name} key?",
                $"The saved {name} API key is deleted from this PC right away. AI features that use {name} stop until you add a key again.\n\n" +
                "The key itself stays valid on your account. To revoke it, delete it in your provider's console.",
                "Remove key", "Cancel")) return;
        if (claude) _app.Settings.ClaudeKeyProtected = null; else _app.Settings.GeminiKeyProtected = null;
        _app.Settings.Save();
        (claude ? ClaudeKey : GeminiKey).Password = "";
        ShowKeys();
        SaveStatus.Text = $"{name} key removed";
    }

    void ShowVersion()
    {
        VersionText.Text = $"Wisp {_app.Updater.CurrentVersion}";
        if (!_app.Updater.CanUpdate) UpdateStatus.Text = "This copy isn't installed, so it can't update itself.";
        else if (_app.Settings.LastUpdateCheck > DateTime.MinValue) UpdateStatus.Text = $"Last checked {_app.Settings.LastUpdateCheck:MMM d, h:mm tt}";
    }

    void ReportBug_Click(object s, RoutedEventArgs e) => _app.Updater.ReportBug();

    async void CheckUpdates_Click(object s, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateStatus.Text = "Checking GitHub…";
        try { UpdateStatus.Text = await _app.CheckForUpdatesAsync(quiet: false); }
        finally { UpdateButton.IsEnabled = true; }
    }

    void ShowPlugins()
    {
        var tools = _app.Tools.All.Where(t => t.Name.StartsWith("plugin_")).Select(t => t.Name["plugin_".Length..]).ToList();
        PluginList.Text = _app.Plugins.Loaded.Count == 0
            ? "No plugins enabled yet. An example (disabled) is in the plugins folder."
            : $"Enabled: {string.Join(", ", _app.Plugins.Loaded)}  ·  {tools.Count} tools";
        PluginErrors.Text = string.Join("\n", _app.Plugins.Errors);
    }

    void Save()
    {
        var s = _app.Settings;
        s.AiEnabled = AiEnabled.IsChecked == true;
        var limitText = MonthlyLimit.Text.Trim().TrimStart('$');
        s.MonthlyLimitUsd = double.TryParse(limitText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lim) && lim > 0 ? lim : 0;
        s.Provider = UseGemini.IsChecked == true ? ProviderKind.Gemini : ProviderKind.Claude;
        if (ClaudeKey.Password.Length > 0) s.ClaudeKey = ClaudeKey.Password;
        if (GeminiKey.Password.Length > 0) s.GeminiKey = GeminiKey.Password;
        if (ClaudeModel.SelectedItem is ModelChoice claude) s.ClaudeModel = claude.Id;
        if (GeminiModel.SelectedItem is ModelChoice gemini) s.GeminiModel = gemini.Id;
        s.AgentEffort = AgentEffort.SelectedItem as string ?? "medium";
        s.MicrophoneDevice = Math.Max(0, Mic.SelectedIndex);
        var newModel = WhisperModels[Math.Max(0, WhisperModel.SelectedIndex)].Id;
        bool modelChanged = newModel != s.WhisperModel;
        s.WhisperModel = newModel;
        s.Language = Language.Text.Trim();
        s.DictationCleanup = Cleanup.IsChecked == true;
        s.UserName = UserName.Text.Trim();
        s.Persona = Persona.Text.Trim();
        s.ConfirmRiskyActions = Confirm.IsChecked == true;
        s.LaunchAtStartup = Startup.IsChecked == true;
        s.AutoCheckUpdates = AutoUpdates.IsChecked == true;
        s.Save();
        ApplyStartup(s.LaunchAtStartup);

        if (modelChanged) _ = Task.Run(() => _app.Transcriber.EnsureLoadedAsync());
        ClaudeKey.Password = GeminiKey.Password = "";
        Load();
    }

    static void ApplyStartup(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null) return;
        if (on) key.SetValue("Wisp", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("Wisp", throwOnMissingValue: false);
    }

    void Save_Click(object s, RoutedEventArgs e)
    {
        try { Save(); SaveStatus.Text = "Saved"; }
        catch (Exception ex) { SaveStatus.Text = ex.Message; }
    }

    async void Test_Click(object s, RoutedEventArgs e)
    {
        Save();
        TestResult.Text = $"Asking {_app.Provider.Name}…";
        try
        {
            var resp = await _app.Provider.CompleteAsync(new LlmRequest
            {
                System = "Reply with exactly: ready",
                Messages = [ChatMessage.User("Are you there?")],
                Effort = "low",
                MaxTokens = 2000,
            }, CancellationToken.None);
            TestResult.Text = $"{_app.Provider.Name} replied: {resp.Text}";
        }
        catch (Exception ex) { TestResult.Text = ex.Message; }
    }

    void OpenPlugins_Click(object s, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(Paths.Plugins) { UseShellExecute = true });
    void OpenLogs_Click(object s, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(Paths.Logs) { UseShellExecute = true });
    void Close_Click(object s, RoutedEventArgs e) => Close();
    void Header_Drag(object s, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
