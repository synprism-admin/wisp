using System.Drawing;
using System.Windows;
using Wisp.AI;
using Wisp.Core;
using Wisp.Desktop;
using Wisp.Input;
using Wisp.Native;
using Wisp.Speech;
using Wisp.Tools;
using Wisp.UI;

namespace Wisp;

/// <summary>Owns every service and runs the three gestures: dictate, talk, circle-to-ask.</summary>
public sealed class WispController : IDisposable
{
    public Settings Settings { get; } = Settings.Load();
    public Store Store { get; } = new();
    public AppIndex Apps { get; } = new();
    public Media Media { get; } = new();
    public ToolRegistry Tools { get; } = new();
    public PluginHost Plugins { get; }
    public Transcriber Transcriber { get; }
    public Recorder Recorder { get; } = new();
    public ThemeEngine Themes { get; } = new();
    public Updater Updater { get; } = new();
    public DesktopManager Desktop { get; private set; } = null!;

    readonly PcControl _pc = new();
    readonly ClaudeProvider _claude;
    readonly GeminiProvider _gemini;
    readonly Agent _agent;
    readonly Scheduler _scheduler;
    HotkeyEngine? _hotkeys;
    ClipboardWatcher? _clipboard;
    IslandWindow? _island;
    SettingsWindow? _settingsWindow;
    LauncherWindow? _launcher;

    // Per-gesture state
    IntPtr _dictationTarget;
    CircleOverlay? _overlay;
    (Bitmap Image, System.Drawing.Point Origin)? _frozen;
    CancellationTokenSource? _work;

    public WispController()
    {
        _claude = new ClaudeProvider(Settings);
        _gemini = new GeminiProvider(Settings);
        Transcriber = new Transcriber(Settings);
        Plugins = new PluginHost(Tools);
        _agent = new Agent(() => Provider, Tools, Settings);
        _scheduler = new Scheduler(Store, RunRoutineAsync);
    }

    static readonly DisabledProvider Disabled = new();

    MeteredProvider? _meteredClaude, _meteredGemini;

    /// <summary>The active provider, metered (usage recorded, monthly limit enforced).</summary>
    public ILlmProvider Provider
    {
        get
        {
            if (!Settings.AiEnabled) return Disabled;
            if (Settings.Provider == ProviderKind.Gemini)
                return _meteredGemini ??= new MeteredProvider(_gemini, Store, Settings, () => Settings.GeminiModel);
            return _meteredClaude ??= new MeteredProvider(_claude, Store, Settings, () => Settings.ClaudeModel);
        }
    }

    public IslandWindow Island => _island!;

    /// <summary>Set for --snapshot runs: build the UI but leave the live desktop alone.</summary>
    public bool SnapshotMode { get; set; }

    public async Task StartAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Tools.AddRange(SystemTools.All(Apps, Media));
        Tools.AddRange(_pc.Tools());
        PluginHost.SeedExamples();
        Plugins.Reload();
        Log.Info($"Tools ready at {sw.ElapsedMilliseconds} ms");

        Themes.Apply(Themes.Find(Settings.ThemeId) ?? Themes.Find("ember") ?? new ThemePack());

        _island = new IslandWindow(this);
        _island.Show();

        Desktop = new DesktopManager(this);
        if (!SnapshotMode) Desktop.Apply();
        Log.Info($"Island shown at {sw.ElapsedMilliseconds} ms");

        if (SnapshotMode) return; // UI only: no hotkeys, clipboard, scheduler or speech model

        ShowPrivacyNoticeOnce();

        // A quiet daily look for a new release, a little after startup so it never slows it down.
        if (Settings.AutoCheckUpdates && Updater.CanUpdate && DateTime.Now - Settings.LastUpdateCheck > TimeSpan.FromHours(20))
            _ = Task.Delay(TimeSpan.FromSeconds(45)).ContinueWith(_ =>
                Application.Current.Dispatcher.BeginInvoke(() => _ = CheckForUpdatesAsync(quiet: true)));

        _clipboard = new ClipboardWatcher(Store);
        _hotkeys = new HotkeyEngine(Application.Current.Dispatcher);
        _hotkeys.HoldStarted += OnHoldStarted;
        _hotkeys.HoldEnded += OnHoldEnded;
        _hotkeys.HoldCancelled += OnHoldCancelled;
        _hotkeys.WinTapped += ShowLauncher;
        ApplyHotkeyOptions();

        Recorder.Level += lvl => Application.Current.Dispatcher.BeginInvoke(() => _island?.SetLevel(lvl));

        _scheduler.Start();
        _ = Task.Run(Apps.Scan);
        await Media.InitAsync();

        if (!Provider.IsConfigured)
            _island.ShowMessage("Welcome to Wisp", "Add an API key in settings to get started. Click here to open the panel.", 12, "\uE946");

        // Warm the speech model in the background so the first dictation is fast.
        _ = Task.Run(async () =>
        {
            try { await Transcriber.EnsureLoadedAsync(); }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.BeginInvoke(() => _island.ShowError($"Speech model failed to load: {ex.Message}"));
            }
        });
        Transcriber.StatusChanged += s =>
        {
            if (s.StartsWith("Downloading"))
                Application.Current.Dispatcher.BeginInvoke(() => _island.ShowThinking("First-time setup", s + " This happens once."));
            else if (s.StartsWith("Ready"))
                Application.Current.Dispatcher.BeginInvoke(() => { if (!Recorder.IsRecording) _island.Collapse(); });
        };
    }

    ToolContext Context(CancellationToken ct) => new()
    {
        Store = Store,
        Settings = Settings,
        Confirm = (what, c) => Application.Current.Dispatcher.Invoke(() => _island!.ConfirmAsync(what, c)),
        Progress = text => Application.Current.Dispatcher.BeginInvoke(() => _island!.ShowThinking(text)),
    };

    // =================================================================== gestures

    void OnHoldStarted(HoldAction action)
    {
        _work?.Cancel();
        switch (action)
        {
            case HoldAction.Dictate:
                _dictationTarget = Win32.GetForegroundWindow();
                Recorder.Start(Settings.MicrophoneDevice);
                _island!.ShowListening("Dictating", "Talk naturally. Let go to type it.");
                break;

            case HoldAction.Talk:
                Recorder.Start(Settings.MicrophoneDevice);
                _island!.ShowListening("Listening", "What can I do?");
                break;

            case HoldAction.Circle:
                _frozen?.Image.Dispose();
                _frozen = ScreenCapture.CaptureVirtualScreen();
                Recorder.Start(Settings.MicrophoneDevice);
                _overlay = new CircleOverlay();
                _overlay.Cancelled += () => _hotkeys?.CancelActive();
                _overlay.Show();
                _island!.ShowListening("Circle & ask", "Circle something and ask about it.");
                break;
        }
    }

    void OnHoldCancelled(HoldAction action)
    {
        Recorder.Stop();
        _overlay?.Close();
        _overlay = null;
        _frozen?.Image.Dispose();
        _frozen = null;
        _island!.Collapse();
    }

    async void OnHoldEnded(HoldAction action)
    {
        var samples = Recorder.Stop();
        var cts = _work = new CancellationTokenSource();
        try
        {
            switch (action)
            {
                case HoldAction.Dictate: await FinishDictationAsync(samples, cts.Token); break;
                case HoldAction.Talk: await FinishTalkAsync(samples, cts.Token); break;
                case HoldAction.Circle: await FinishCircleAsync(samples, cts.Token); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (LlmException ex) { _island!.ShowError(ex.Message); }
        catch (Exception ex)
        {
            Log.Error($"{action} failed", ex);
            _island!.ShowError(ex.Message);
        }
    }

    async Task FinishDictationAsync(float[] samples, CancellationToken ct)
    {
        _island!.ShowThinking("Transcribing…");
        var raw = await Transcriber.TranscribeAsync(samples, ct: ct);
        if (raw.Length == 0) { _island.Collapse(); return; }

        var text = raw;
        if (Settings.DictationCleanup && Provider.IsConfigured && raw.Split(' ').Length >= 4)
        {
            _island.ShowThinking("Cleaning up…", raw);
            try { text = await CleanUpAsync(raw, _dictationTarget, ct); }
            catch (Exception ex) { Log.Error("Cleanup failed; pasting raw transcript", ex); }
        }

        _island.Collapse();
        await TextInjector.PasteAsync(text, _dictationTarget);
    }

    async Task<string> CleanUpAsync(string raw, IntPtr target, CancellationToken ct)
    {
        Win32.GetWindowThreadProcessId(target, out uint pid);
        string app;
        try { app = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { app = "unknown"; }

        var resp = await Provider.CompleteAsync(new LlmRequest
        {
            System = """
                You turn raw speech-to-text into the text the speaker meant to type.
                - Remove filler (um, uh, like, you know) and false starts.
                - Apply self-corrections: "at 6, no, 9" becomes "at 9".
                - Fix punctuation, capitalization and obvious mis-hearings.
                - Keep the speaker's own words, tone and language. Don't add, summarize or answer anything.
                - If they dictate formatting ("new line", "bullet point"), apply it.
                - Match the destination app: casual for chat, proper prose for email/docs, exact identifiers for code.
                Output only the final text, with nothing before or after it.
                """,
            Messages = [ChatMessage.User($"Destination app: {app} — \"{Win32.WindowTitle(target)}\"\n\nTranscript:\n{raw}")],
            Effort = Settings.CleanupEffort,
            MaxTokens = 4000,
        }, ct);
        var text = resp.Text.Trim().Trim('"');
        return text.Length == 0 ? raw : text;
    }

    async Task FinishTalkAsync(float[] samples, CancellationToken ct)
    {
        _island!.ShowThinking("Transcribing…");
        var said = await Transcriber.TranscribeAsync(samples, ct: ct);
        if (said.Length == 0) { _island.Collapse(); return; }
        await AskAsync(said, null, keepPanel: false, ct);
    }

    async Task FinishCircleAsync(float[] samples, CancellationToken ct)
    {
        var overlay = _overlay;
        var frozen = _frozen;
        _overlay = null;
        _frozen = null;
        var bounds = overlay?.Bounds;
        var path = overlay?.Points.ToList() ?? [];
        overlay?.Close();
        if (frozen is null) return;

        byte[] png;
        using (frozen.Value.Image)
            png = ScreenCapture.CropLasso(frozen.Value.Image, frozen.Value.Origin, bounds, path);

        _island!.ShowThinking("Transcribing…");
        var question = await Transcriber.TranscribeAsync(samples, ct: ct);
        if (question.Length == 0)
            question = bounds is null ? "What's on my screen?" : "What is this?";

        var prompt = bounds is null
            ? $"(Screenshot of my screen attached.) {question}"
            : $"(I circled part of my screen; the image shows it with the rest dimmed.) {question}";
        await AskAsync(prompt, png, keepPanel: false, ct, displayText: question);
    }

    /// <summary>Runs the agent and shows the reply. Used by voice, the panel's ask box and circle-to-ask.</summary>
    public async Task AskAsync(string text, byte[]? image, bool keepPanel, CancellationToken ct = default, string? displayText = null)
    {
        if (!Provider.IsConfigured)
        {
            _island!.ShowError(Settings.AiEnabled
                ? $"Add a {Provider.Name} API key in settings first."
                : "AI features are turned off. Turn them on in Settings to use this.");
            return;
        }
        if (!keepPanel) _island!.ShowThinking("Thinking…", $"“{displayText ?? text}”");
        try
        {
            var reply = await _agent.AskAsync(text, image, Context(ct), ct);
            _island!.SetLastReply(reply);
            if (!keepPanel) _island.ShowMessage("Wisp", reply);
        }
        catch (LlmException ex)
        {
            _island!.SetLastReply(ex.Message);
            if (!keepPanel) _island.ShowError(ex.Message);
        }
    }

    async Task RunRoutineAsync(Routine r)
    {
        if (!Provider.IsConfigured) return;
        await Application.Current.Dispatcher.InvokeAsync(() => _island!.ShowThinking(r.Title, r.Instruction));
        var reply = await _agent.RunDetachedAsync(r.Instruction, Context(CancellationToken.None), CancellationToken.None);
        await Application.Current.Dispatcher.InvokeAsync(() => _island!.ShowMessage(r.Title, reply, 12, "\uE823"));
    }

    // =================================================================== privacy

    /// <summary>
    /// First run: say plainly what stays local and what goes to the AI provider, and let the user
    /// choose before anything is sent.
    /// </summary>
    void ShowPrivacyNoticeOnce()
    {
        if (Settings.PrivacyNoticeShown) return;
        var policy = System.IO.Path.Combine(AppContext.BaseDirectory, "PRIVACY.md");
        bool on = NoticeWindow.Ask(
            "Before you start",
            "Wisp runs on your PC. Your voice is transcribed locally and never uploaded. Wisp has no servers and collects no analytics.\n\n" +
            "AI features send data to the AI provider you choose (Claude or Gemini), using your own API key:\n" +
            "• Voice commands and the Ask box: what you said, plus tool results the assistant needs, which can include window contents and screenshots\n" +
            "• Dictation clean-up: the transcript and the name of the app you're typing into\n" +
            "• Circle to ask: a screenshot of what you circled\n\n" +
            "You can change this any time in Settings → AI features. With AI off, dictation still works without clean-up.",
            "Turn on AI features", "Keep AI off",
            "Read the full privacy policy", policy);
        Settings.AiEnabled = on;
        Settings.PrivacyNoticeShown = true;
        Settings.Save();
    }

    // =================================================================== desktop

    public void ApplyHotkeyOptions()
    {
        if (_hotkeys is not null) _hotkeys.InterceptWinTap = Settings.Desktop.WinKeyOpensLauncher;
    }

    public void ShowLauncher()
    {
        if (_launcher is { IsLoaded: true }) { _launcher.Close(); return; }
        _launcher = new LauncherWindow(this);
        _launcher.Closed += (_, _) => _launcher = null;
        _launcher.Show();
    }

    public void SetTheme(string id)
    {
        var pack = Themes.Find(id);
        if (pack is null) return;
        Themes.Apply(pack);
        Log.Info($"Theme selected: {pack.Name}");
        Settings.ThemeId = pack.Id;
        Settings.Save();
        Desktop.ApplyWindowsTheme();
    }

    System.Windows.Threading.DispatcherTimer? _themeSave;

    /// <summary>
    /// Live theme editing. The first edit of a built-in theme forks it into "My &lt;name&gt;" so the
    /// originals stay pristine; changes apply instantly and save to disk shortly after the last one.
    /// </summary>
    public void EditTheme(Action<ThemePack> change)
    {
        var pack = Themes.Current;
        if (ThemeEngine.IsBuiltIn(pack.Id))
        {
            var names = Themes.All().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = $"My {pack.Name}";
            for (int n = 2; names.Contains(name); n++) name = $"My {pack.Name} {n}";
            pack = Themes.Duplicate(pack, name, pack.Brand);
            Settings.ThemeId = pack.Id;
            Settings.Save();
        }
        change(pack);
        Themes.Apply(pack);

        _themeSave ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _themeSave.Stop();
        _themeSave.Tick -= SaveTheme;
        _themeSave.Tick += SaveTheme;
        _themeSave.Start();
    }

    void SaveTheme(object? s, EventArgs e)
    {
        _themeSave!.Stop();
        ThemeEngine.Save(Themes.Current);
        Desktop.ApplyWindowsTheme();
    }

    /// <summary>Renames the product everywhere. Built-in themes get copied first so they stay pristine.</summary>
    public void Rebrand(string brand)
    {
        var pack = Themes.Current;
        if (pack.Brand == brand) return;
        if (ThemeEngine.IsBuiltIn(pack.Id))
        {
            SetTheme(Themes.Duplicate(pack, $"{pack.Name} ({brand})", brand).Id);
            return;
        }
        pack.Brand = brand;
        ThemeEngine.Save(pack);
        Themes.Apply(pack);
    }

    // =================================================================== updates

    bool _updating;

    /// <summary>
    /// Looks for a newer release on GitHub. Quiet checks only speak up when there's an update;
    /// a manual check always reports back. Installing closes Wisp cleanly (restoring the taskbar
    /// and tray), swaps in the new version and relaunches it.
    /// </summary>
    public async Task<string> CheckForUpdatesAsync(bool quiet)
    {
        if (_updating) return "An update is already in progress.";
        if (!Updater.CanUpdate)
        {
            var msg = "Updates work in the installed version of Wisp (this copy isn't installed).";
            if (!quiet) _island!.ShowMessage("Updates", msg);
            return msg;
        }

        Velopack.UpdateInfo? update;
        try { update = await Updater.CheckAsync(); }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            var msg = "Couldn't reach GitHub to check for updates.";
            if (!quiet) _island!.ShowError(msg);
            return msg;
        }
        Settings.LastUpdateCheck = DateTime.Now;
        Settings.Save();

        if (update is null)
        {
            var msg = $"You're up to date (Wisp {Updater.CurrentVersion}).";
            if (!quiet) _island!.ShowMessage("Updates", msg, 6, "\uE930");
            return msg;
        }

        var version = update.TargetFullRelease.Version.ToString();
        bool install = await _island!.ConfirmAsync(
            $"Wisp {version} is available (you have {Updater.CurrentVersion}). Wisp will close, update and reopen.",
            CancellationToken.None, "Update available", yes: "Update now", no: "Later", thenWorking: false);
        if (!install) return $"Wisp {version} is available.";

        _updating = true;
        try
        {
            _island.ShowThinking("Downloading update…", $"Wisp {version}");
            await Updater.DownloadAsync(update, pct =>
                Application.Current.Dispatcher.BeginInvoke(() => _island.ShowThinking("Downloading update…", $"Wisp {version} · {pct}%")));
            Log.Info($"Updating to {version}");
            Updater.ApplyWhenWispExits(update);
            Application.Current.Shutdown(); // normal exit: taskbar, theme and tray are handed back first
            return $"Updating to {version}…";
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            _updating = false;
            _island.ShowError($"The update didn't download: {ex.Message}");
            return "The update failed.";
        }
    }

    // =================================================================== settings

    public void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void Dispose()
    {
        Desktop?.Dispose();
        _hotkeys?.Dispose();
        _clipboard?.Dispose();
        _scheduler.Dispose();
        Plugins.Dispose();
        Recorder.Dispose();
        Transcriber.Dispose();
        _pc.Dispose();
    }
}
