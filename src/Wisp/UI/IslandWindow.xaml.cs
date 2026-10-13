using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop;
using Wisp.Native;

namespace Wisp.UI;

/// <summary>
/// The island: a pill at the top-center of the screen that grows into a status bubble
/// (listening, thinking, answers, confirmations) or a full panel (home, apps, clipboard,
/// routines, power). Collapsed it never takes focus, so dictation lands in the app you were using.
/// </summary>
public partial class IslandWindow : Window
{
    enum Mode { Pill, Activity, Panel }

    const double PillW = 148, PanelW = 720, PanelH = 540;
    double PillH = 34;

    readonly WispController _app;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };
    readonly DispatcherTimer _autoCollapse = new();
    readonly List<Rectangle> _bars = [];
    Mode _mode = Mode.Pill;
    IntPtr _hwnd;
    TaskCompletionSource<bool>? _confirm;
    bool _suppressVolume;
    bool _nowPlaying;

    public IslandWindow(WispController app)
    {
        _app = app;
        InitializeComponent();

        for (int i = 0; i < 9; i++)
        {
            var r = new Rectangle { Width = 3, Height = 4, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(1.5, 0, 1.5, 0), Fill = (Brush)FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center };
            _bars.Add(r);
        }
        Wave.ItemsSource = _bars;

        _clock.Tick += (_, _) => UpdatePill();
        _clock.Start();
        _autoCollapse.Tick += (_, _) => { _autoCollapse.Stop(); if (_mode == Mode.Activity && _confirm is null) Collapse(); };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            Reposition();
        };
        Deactivated += (_, _) => { if (_mode == Mode.Panel) Collapse(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Collapse(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Reposition);

        _app.Store.TodosChanged += () => Dispatcher.BeginInvoke(RefreshTodos);
        _app.Store.RoutinesChanged += () => Dispatcher.BeginInvoke(RefreshRoutines);
        _app.Store.ClipboardChanged += () => Dispatcher.BeginInvoke(() => { if (PageClip.Visibility == Visibility.Visible) RefreshClips(); });
        _app.Media.Changed += () => Dispatcher.BeginInvoke(async () => await RefreshMediaAsync());

        UpdatePill();
        _ = RefreshMediaAsync();
    }

    /// <summary>Top-center of the primary monitor, in WPF units.</summary>
    void Reposition()
    {
        var wa = SystemParameters.WorkArea;
        var full = SystemParameters.PrimaryScreenWidth;
        Left = (full - Width) / 2;
        Top = 0;
        _ = wa;
    }

    // ============================================================ state machine

    void SetMode(Mode mode, double w, double h, double radius)
    {
        _mode = mode;
        PillLayer.Visibility = mode == Mode.Pill ? Visibility.Visible : Visibility.Collapsed;
        ActivityLayer.Visibility = mode == Mode.Activity ? Visibility.Visible : Visibility.Collapsed;
        PanelLayer.Visibility = mode == Mode.Panel ? Visibility.Visible : Visibility.Collapsed;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(mode == Mode.Panel ? 320 : 240);
        Shell.BeginAnimation(WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = ease });
        Shell.BeginAnimation(HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });
        Shell.CornerRadius = new CornerRadius(Math.Min(radius * _app.Themes.Current.Roundness, h / 2));

        // Only the full panel may take focus (for typing in search boxes).
        if (_hwnd != IntPtr.Zero)
        {
            if (mode == Mode.Panel) Win32.SetExStyle(_hwnd, 0, Win32.WS_EX_NOACTIVATE);
            else Win32.SetExStyle(_hwnd, Win32.WS_EX_NOACTIVATE);
        }
    }

    bool _frozen; // set for snapshots so background events can't collapse the panel

    public void Collapse()
    {
        if (_frozen) return;
        _autoCollapse.Stop();
        if (_confirm is not null) { _confirm.TrySetResult(false); _confirm = null; }
        Wave.Visibility = Visibility.Collapsed;
        ConfirmButtons.Visibility = Visibility.Collapsed;
        StopPulse();
        SetMode(Mode.Pill, PillW + (_nowPlaying ? 18 : 0), PillH, PillH / 2);
    }

    /// <summary>
    /// The pill sits inside the top bar, and both are "always on top" windows; whichever asserted it
    /// last wins. The bars re-assert whenever Windows repositions them, so the island re-raises itself
    /// after every reposition and on a slow heartbeat as a backstop.
    /// </summary>
    public void KeepOnTop()
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    readonly DispatcherTimer _raise = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>With the top bar on, the pill shrinks to sit inside it.</summary>
    public void SetBarMode(bool bar, double barHeight = 40)
    {
        PillH = bar ? Math.Max(24, barHeight - 10) : 34;
        _raise.Tick -= OnRaise;
        if (bar) { _raise.Tick += OnRaise; _raise.Start(); KeepOnTop(); } else _raise.Stop();
        if (Content is Grid root) root.Margin = new Thickness(0, bar ? (barHeight - PillH) / 2 : 6, 0, 0);
        if (_mode == Mode.Pill) Collapse();
    }

    void OnRaise(object? s, EventArgs e) => KeepOnTop();

    /// <summary>Opens the panel on a given tab: home, apps, clipboard, routines, controls, wallpaper, power.</summary>
    public void OpenPanel(string tab)
    {
        var rb = tab switch
        {
            "apps" => TabApps, "clipboard" => TabClip, "routines" => TabRoutines, "controls" => TabControls,
            "wallpaper" => TabWallpaper, "power" => TabPower, _ => TabHome,
        };
        if (rb.IsChecked == true) Tab_Checked(rb, new RoutedEventArgs()); else rb.IsChecked = true;
        OpenPanel();
    }

    /// <summary>Lays out the panel on a tab without activating the window (for snapshots).</summary>
    public void ShowPanelForSnapshot(string tab)
    {
        var rb = tab switch
        {
            "apps" => TabApps, "clipboard" => TabClip, "routines" => TabRoutines, "controls" or "customize" => TabControls,
            "wallpaper" => TabWallpaper, "power" => TabPower, _ => TabHome,
        };
        rb.IsChecked = true;
        Tab_Checked(rb, new RoutedEventArgs());
        RefreshHome();
        _frozen = true;
        SetMode(Mode.Panel, PanelW, PanelH, 26);
        Shell.BeginAnimation(WidthProperty, null);
        Shell.BeginAnimation(HeightProperty, null);
        Shell.Width = PanelW;
        Shell.Height = PanelH;
        if (tab == "customize") Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PageControls.ScrollToEnd());
    }

    public void OpenPanel()
    {
        _autoCollapse.Stop();
        RefreshHome();
        SetMode(Mode.Panel, PanelW, PanelH, 26);
        Activate();
        Win32.ForceForeground(_hwnd);
        if (TabHome.IsChecked == true) AskBox.Focus();
    }

    void ShowActivity(string icon, string title, string text, double width = 420)
    {
        _autoCollapse.Stop();
        ActivityIcon.Text = icon;
        ActivityTitle.Text = title;
        ActivityText.Text = text;
        ActivityText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

        // Measure the content to size the bubble to it.
        ActivityLayer.Visibility = Visibility.Visible;
        ActivityText.MaxWidth = width - 100;
        ActivityLayer.Measure(new Size(width - 32, double.PositiveInfinity));
        double h = Math.Max(48, ActivityLayer.DesiredSize.Height + 20);
        SetMode(Mode.Activity, width, h, h <= 52 ? 24 : 22);
    }

    public void ShowListening(string title, string hint)
    {
        Wave.Visibility = Visibility.Visible;
        ConfirmButtons.Visibility = Visibility.Collapsed;
        StartPulse();
        ShowActivity("\uE720", title, hint, 380);
    }

    public void SetLevel(float level)
    {
        if (Wave.Visibility != Visibility.Visible) return;
        var rnd = Random.Shared;
        for (int i = 0; i < _bars.Count; i++)
        {
            double center = 1 - Math.Abs(i - (_bars.Count - 1) / 2.0) / (_bars.Count / 2.0);
            double target = 4 + level * 22 * (0.45 + 0.55 * center) * (0.7 + 0.3 * rnd.NextDouble());
            _bars[i].BeginAnimation(HeightProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(70)));
        }
    }

    public void ShowThinking(string title, string text = "")
    {
        Wave.Visibility = Visibility.Collapsed;
        ConfirmButtons.Visibility = Visibility.Collapsed;
        StartPulse();
        ShowActivity("\uE945", title, text, 460);
    }

    public void ShowMessage(string title, string text, double seconds = 8, string icon = "\uE8BD")
    {
        Wave.Visibility = Visibility.Collapsed;
        ConfirmButtons.Visibility = Visibility.Collapsed;
        StopPulse();
        double width = text.Length switch { < 60 => 420, < 200 => 520, _ => 620 };
        ShowActivity(icon, title, text, width);
        // Long answers stay up longer.
        _autoCollapse.Interval = TimeSpan.FromSeconds(Math.Max(seconds, Math.Min(30, text.Length / 18.0)));
        _autoCollapse.Start();
    }

    public void ShowError(string text) => ShowMessage("Something went wrong", text, 10, "\uE783");

    public Task<bool> ConfirmAsync(string what, CancellationToken ct)
    {
        _confirm?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirm = tcs;
        Wave.Visibility = Visibility.Collapsed;
        ConfirmButtons.Visibility = Visibility.Visible;
        StopPulse();
        ShowActivity("\uE7BA", "Okay to do this?", what, 480);

        ct.Register(() => tcs.TrySetResult(false));
        // Unanswered prompts are declined after a minute.
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => tcs.TrySetResult(false));
        return tcs.Task.ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() => { if (_confirm == tcs) { _confirm = null; ConfirmButtons.Visibility = Visibility.Collapsed; } });
            return t.Result;
        });
    }

    void ConfirmYes_Click(object s, RoutedEventArgs e) { _confirm?.TrySetResult(true); ShowThinking("Working on it…"); }
    void ConfirmNo_Click(object s, RoutedEventArgs e) { _confirm?.TrySetResult(false); Collapse(); }

    void StartPulse()
    {
        var a = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        ActivityHalo.BeginAnimation(OpacityProperty, a);
    }

    void StopPulse() => ActivityHalo.BeginAnimation(OpacityProperty, null);

    void UpdatePill()
    {
        PillText.Text = DateTime.Now.ToString("h:mm");
        PillEq.Visibility = _nowPlaying ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============================================================ pill interactions

    void Shell_MouseEnter(object s, MouseEventArgs e)
    {
        if (_mode != Mode.Pill) return;
        var a = new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase() };
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void Shell_MouseLeave(object s, MouseEventArgs e)
    {
        var a = new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase() };
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void Shell_Click(object s, MouseButtonEventArgs e)
    {
        if (_mode == Mode.Pill) OpenPanel();
        else if (_mode == Mode.Activity && _confirm is null) OpenPanel();
    }

    void Shell_MouseWheel(object s, MouseWheelEventArgs e)
    {
        if (_mode != Mode.Panel && e.Delta < 0) { OpenPanel(); e.Handled = true; }
        else if (_mode == Mode.Panel && e.Delta > 0 && !IsOverScrollable(e.OriginalSource as DependencyObject)) { Collapse(); e.Handled = true; }
    }

    static bool IsOverScrollable(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ListBox or ScrollViewer or TextBox) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    void Tab_Checked(object s, RoutedEventArgs e)
    {
        if (PageHome is null) return;
        PageHome.Visibility = TabHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageApps.Visibility = TabApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageClip.Visibility = TabClip.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageRoutines.Visibility = TabRoutines.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PagePower.Visibility = TabPower.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageControls.Visibility = TabControls.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageWallpaper.Visibility = TabWallpaper.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TabControls.IsChecked == true) _ = RefreshControlsAsync();
        if (TabWallpaper.IsChecked == true) RefreshWallpapers();

        if (TabApps.IsChecked == true) { AppSearch.Text = ""; RefreshApps(); AppSearch.Focus(); }
        if (TabClip.IsChecked == true) { ClipSearch.Text = ""; RefreshClips(); ClipSearch.Focus(); }
        if (TabRoutines.IsChecked == true) RefreshRoutines();
        if (TabHome.IsChecked == true) { RefreshHome(); AskBox.Focus(); }
    }

    void Settings_Click(object s, RoutedEventArgs e) { Collapse(); _app.ShowSettings(); }

    // ============================================================ home

    void RefreshHome()
    {
        var h = DateTime.Now.Hour;
        var part = h < 5 ? "Up late" : h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening";
        var name = _app.Settings.UserName;
        Greeting.Text = string.IsNullOrWhiteSpace(name) ? part : $"{part}, {char.ToUpper(name[0]) + name[1..]}";
        DateLine.Text = DateTime.Now.ToString("dddd, MMMM d");
        RefreshTodos();
        RefreshRoutines();
        RefreshVolume();
        _ = RefreshMediaAsync();
    }

    async void AskBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(AskBox.Text)) return;
        var text = AskBox.Text.Trim();
        AskBox.Text = "";
        LastReply.Text = "…";
        await _app.AskAsync(text, null, keepPanel: true);
    }

    public void SetLastReply(string text)
    {
        LastReply.Text = text;
        LastReply.Foreground = (Brush)FindResource("Text");
    }

    sealed record TodoVm(long Id, string Text, bool Done, string DueText);

    void RefreshTodos()
    {
        var items = _app.Store.Todos()
            .Where(t => !t.Done || t.Created > DateTime.Now.AddHours(-12))
            .Select(t => new TodoVm(t.Id, t.Text, t.Done, t.Due is { } d ? (d.Date == DateTime.Today ? d.ToString("h:mm tt") : d.ToString("MMM d")) : ""))
            .ToList();
        TodoList.ItemsSource = items;
    }

    void TodoBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(TodoBox.Text)) return;
        _app.Store.AddTodo(TodoBox.Text.Trim(), null);
        TodoBox.Text = "";
    }

    void Todo_Toggle(object s, RoutedEventArgs e)
    {
        if (s is CheckBox { Tag: long id } cb) _app.Store.SetTodoDone(id, cb.IsChecked == true);
    }

    sealed record UpcomingVm(string When, string Title);

    void RefreshUpcoming(IReadOnlyList<Routine> routines)
    {
        var soon = routines.Where(r => r.Enabled).OrderBy(r => r.NextRun).Take(4)
            .Select(r => new UpcomingVm(r.NextRun.Date == DateTime.Today ? r.NextRun.ToString("h:mm tt") : r.NextRun.ToString("ddd h tt"), r.Title))
            .ToList();
        UpcomingList.ItemsSource = soon;
        UpcomingEmpty.Visibility = soon.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task RefreshMediaAsync()
    {
        var np = await _app.Media.CurrentAsync();
        _nowPlaying = np?.Playing == true;
        NpTitle.Text = np is null || string.IsNullOrEmpty(np.Title) ? "Nothing playing" : np.Title;
        NpArtist.Text = np is null ? "" : string.Join(" · ", new[] { np.Artist, np.App }.Where(x => !string.IsNullOrEmpty(x)));
        PlayIcon.Text = np?.Playing == true ? "\uE769" : "\uE768";
        UpdatePill();
        if (_mode == Mode.Pill) Shell.BeginAnimation(WidthProperty, new DoubleAnimation(PillW + (_nowPlaying ? 18 : 0), TimeSpan.FromMilliseconds(200)));
    }

    void Prev_Click(object s, RoutedEventArgs e) => _app.Media.Previous();
    void Next_Click(object s, RoutedEventArgs e) => _app.Media.Next();
    void PlayPause_Click(object s, RoutedEventArgs e) => _app.Media.PlayPause();

    void RefreshVolume()
    {
        try
        {
            _suppressVolume = true;
            VolumeSlider.Value = _app.Media.Volume;
            VolText.Text = $"{_app.Media.Volume}";
            VolIcon.Text = _app.Media.Muted ? "\uE74F" : "\uE767";
        }
        catch { }
        finally { _suppressVolume = false; }
    }

    void Volume_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressVolume || VolText is null) return;
        try { _app.Media.Volume = (int)e.NewValue; VolText.Text = $"{(int)e.NewValue}"; } catch { }
    }

    void Mute_Click(object s, RoutedEventArgs e)
    {
        try { _app.Media.Muted = !_app.Media.Muted; } catch { }
        RefreshVolume();
    }

    // ============================================================ apps

    sealed record AppVm(string Name, string Initial, AppEntry Entry);

    void RefreshApps()
    {
        AppList.ItemsSource = _app.Apps.Search(AppSearch.Text).Take(80)
            .Select(a => new AppVm(a.Name, a.Name[..1].ToUpperInvariant(), a)).ToList();
        if (AppList.Items.Count > 0) AppList.SelectedIndex = 0;
    }

    void AppSearch_TextChanged(object s, TextChangedEventArgs e) => RefreshApps();

    void AppSearch_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LaunchSelected();
        else if (e.Key == Key.Down && AppList.Items.Count > 0)
        {
            AppList.SelectedIndex = Math.Min(AppList.SelectedIndex + 1, AppList.Items.Count - 1);
            AppList.ScrollIntoView(AppList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && AppList.Items.Count > 0)
        {
            AppList.SelectedIndex = Math.Max(AppList.SelectedIndex - 1, 0);
            AppList.ScrollIntoView(AppList.SelectedItem);
            e.Handled = true;
        }
    }

    void AppList_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) LaunchSelected(); }
    void AppList_Click(object s, MouseButtonEventArgs e) => LaunchSelected();

    void LaunchSelected()
    {
        if (AppList.SelectedItem is not AppVm vm) return;
        Collapse();
        try { AppIndex.Launch(vm.Entry); } catch (Exception ex) { ShowError(ex.Message); }
    }

    // ============================================================ clipboard

    sealed record ClipVm(long Id, string Text, string Preview, string Age, Visibility PinVisibility);

    void RefreshClips()
    {
        var q = ClipSearch.Text;
        ClipList.ItemsSource = _app.Store.Clips()
            .Where(c => q.Length == 0 || c.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(c => new ClipVm(c.Id, c.Text, OneLine(c.Text), Ago(c.Copied), c.Pinned ? Visibility.Visible : Visibility.Collapsed))
            .ToList();
    }

    static string OneLine(string s)
    {
        s = s.Replace("\r", "").Replace('\n', ' ').Replace('\t', ' ').Trim();
        return s.Length > 140 ? s[..140] + "…" : s;
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m" : d.TotalDays < 1 ? $"{(int)d.TotalHours}h" : $"{(int)d.TotalDays}d";
    }

    void ClipSearch_TextChanged(object s, TextChangedEventArgs e) => RefreshClips();

    async void ClipList_Click(object s, MouseButtonEventArgs e)
    {
        if (ClipList.SelectedItem is not ClipVm vm) return;
        Collapse();
        await Task.Delay(120); // let focus return to the previous app
        await Input.TextInjector.PasteAsync(vm.Text);
    }

    void ClipPin_Click(object s, RoutedEventArgs e)
    {
        if (s is MenuItem { Tag: long id })
        {
            var c = _app.Store.Clips().FirstOrDefault(x => x.Id == id);
            if (c is not null) _app.Store.SetClipPinned(id, !c.Pinned);
        }
    }

    void ClipRemove_Click(object s, RoutedEventArgs e)
    {
        if (s is MenuItem { Tag: long id }) _app.Store.DeleteClip(id);
    }

    // ============================================================ routines

    sealed record RoutineVm(long Id, string Time, string Repeat, string Title, string Instruction, bool Enabled);

    void RefreshRoutines()
    {
        var rs = _app.Store.Routines();
        RoutineList.ItemsSource = rs.Select(r => new RoutineVm(r.Id,
            r.NextRun.ToString("h:mm tt"),
            r.Kind switch
            {
                RoutineKind.Once => r.NextRun.ToString("ddd, MMM d"),
                RoutineKind.Daily => "Every day",
                RoutineKind.Weekdays => "Weekdays",
                RoutineKind.Weekly => $"Every {r.NextRun:dddd}",
                _ => "",
            },
            r.Title, r.Instruction, r.Enabled)).ToList();
        RefreshUpcoming(rs);
    }

    void Routine_Toggle(object s, RoutedEventArgs e)
    {
        if (s is CheckBox { Tag: long id } cb) _app.Store.SetRoutineEnabled(id, cb.IsChecked == true);
    }

    void Routine_Delete(object s, RoutedEventArgs e)
    {
        if (s is Button { Tag: long id }) _app.Store.DeleteRoutine(id);
    }

    // ============================================================ power

    void Lock_Click(object s, RoutedEventArgs e) { Collapse(); Win32.LockWorkStation(); }
    void Sleep_Click(object s, RoutedEventArgs e) { Collapse(); Win32.SetSuspendState(false, false, false); }

    void Restart_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show("Restart the PC now?", "Wisp", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
            System.Diagnostics.Process.Start("shutdown", "/r /t 0");
    }

    void Shutdown_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show("Shut down the PC now?", "Wisp", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
            System.Diagnostics.Process.Start("shutdown", "/s /t 0");
    }

    void Quit_Click(object s, RoutedEventArgs e) => Application.Current.Shutdown();

    // ============================================================ controls

    bool _syncing;

    async Task RefreshControlsAsync()
    {
        _syncing = true;
        var d = _app.Settings.Desktop;
        CtlAll.IsChecked = _app.Desktop.AllOn;
        CtlAllTitle.Text = $"{_app.Themes.Current.Brand} desktop";
        CtlTaskbar.IsChecked = d.HideTaskbar;
        CtlTopBar.IsChecked = d.ShowTopBar;
        CtlDock.IsChecked = d.ShowDock;
        CtlIcons.IsChecked = d.HideDesktopIcons;
        CtlWinKey.IsChecked = d.WinKeyOpensLauncher;
        CtlTheme.IsChecked = d.ThemeWindows;
        CtlTitleBars.IsChecked = d.AccentTitleBars;
        BrandBox.Text = _app.Themes.Current.Brand;
        RefreshThemes();
        RefreshCustomize();
        _syncing = false;

        var wifi = await Radios.IsOnAsync(true);
        var bt = await Radios.IsOnAsync(false);
        _syncing = true;
        CtlWifi.IsChecked = wifi == true; CtlWifi.IsEnabled = wifi is not null;
        CtlBluetooth.IsChecked = bt == true; CtlBluetooth.IsEnabled = bt is not null;
        _syncing = false;
    }

    void CtlAll_Click(object s, RoutedEventArgs e)
    {
        if (_syncing) return;
        _app.Desktop.SetAll(CtlAll.IsChecked == true);
        _ = RefreshControlsAsync();
    }

    void Ctl_Click(object s, RoutedEventArgs e)
    {
        if (_syncing) return;
        var d = _app.Settings.Desktop;
        d.HideTaskbar = CtlTaskbar.IsChecked == true;
        d.ShowTopBar = CtlTopBar.IsChecked == true;
        d.ShowDock = CtlDock.IsChecked == true;
        d.HideDesktopIcons = CtlIcons.IsChecked == true;
        d.WinKeyOpensLauncher = CtlWinKey.IsChecked == true;
        d.ThemeWindows = CtlTheme.IsChecked == true;
        d.AccentTitleBars = CtlTitleBars.IsChecked == true;
        _app.Settings.Save();
        _app.Desktop.Apply();
        _app.ApplyHotkeyOptions();
        _ = RefreshControlsAsync();
    }

    async void Radio_Click(object s, RoutedEventArgs e)
    {
        if (_syncing || s is not CheckBox cb) return;
        bool ok = await Radios.SetAsync(cb == CtlWifi, cb.IsChecked == true);
        if (!ok) ShowError("Windows didn't allow changing that radio.");
        await RefreshControlsAsync();
    }

    sealed record ThemeVm(string Id, string Name, Brush Swatch, Brush Ring);

    void RefreshThemes()
    {
        var current = _app.Themes.Current.Id;
        ThemeList.ItemsSource = _app.Themes.All().Select(t => new ThemeVm(t.Id, t.Name,
            new SolidColorBrush(ThemeEngine.Parse(t.Colors.Accent)),
            t.Id == current ? (Brush)FindResource("Text") : new SolidColorBrush(ThemeEngine.Parse(t.Colors.Surface)))).ToList();
    }

    void Theme_Click(object s, RoutedEventArgs e)
    {
        if (s is Button { Tag: string id }) _app.SetTheme(id);
        _ = RefreshControlsAsync();
    }

    void Rebrand_Click(object s, RoutedEventArgs e)
    {
        var brand = BrandBox.Text.Trim();
        if (brand.Length == 0) return;
        _app.Rebrand(brand);
        _ = RefreshControlsAsync();
    }

    void ImportTheme_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Wisp theme (*.wisptheme)|*.wisptheme|Zip (*.zip)|*.zip" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var pack = _app.Themes.Import(dlg.FileName);
            _app.SetTheme(pack.Id);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        _ = RefreshControlsAsync();
    }

    void ExportTheme_Click(object s, RoutedEventArgs e)
    {
        var pack = _app.Themes.Current;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"{pack.Id}.wisptheme", Filter = "Wisp theme (*.wisptheme)|*.wisptheme" };
        if (dlg.ShowDialog() != true) return;
        try { ThemeEngine.Export(pack, dlg.FileName); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    void ThemeFolder_Click(object s, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_app.Themes.Current.Folder ?? ThemeEngine.Root) { UseShellExecute = true });

    // ============================================================ customize

    sealed record SwatchVm(string Hex, Brush Brush, Visibility Ring);

    static readonly string[] AccentPresets =
    [
        "#F0703A", "#E95420", "#F2B134", "#E5484D", "#E54D8C", "#A35CF0",
        "#6E7BF2", "#3B9EF5", "#2BB5C4", "#3CC48A", "#88C0D0", "#B4B4B4",
    ];

    static readonly string[] BackgroundPresets =
    [
        "#141416", "#1C1C1F", "#0F1115", "#1E1220", "#2E3440", "#121714",
        "#1B1611", "#24262B", "#F7F4F0", "#FFFFFF",
    ];

    bool _fontsLoaded;

    void RefreshCustomize()
    {
        var t = _app.Themes.Current;
        _syncing = true;
        try
        {
            CustNote.Text = ThemeEngine.IsBuiltIn(t.Id)
                ? $"Changes go into your own copy of {t.Name}; the original stays untouched."
                : $"Editing {t.Name}. Changes save automatically.";

            AccentSwatches.ItemsSource = Swatches(AccentPresets, t.Colors.Accent);
            BackgroundSwatches.ItemsSource = Swatches(BackgroundPresets, t.Colors.Surface);
            AccentHex.Text = "#" + t.Colors.Accent[^6..];
            BackgroundHex.Text = "#" + t.Colors.Surface[^6..];

            if (!_fontsLoaded)
            {
                var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n).ToList();
                UiFontBox.ItemsSource = fonts;
                HeadingFontBox.ItemsSource = fonts;
                _fontsLoaded = true;
            }
            UiFontBox.SelectedItem = FirstFamily(t.Font);
            HeadingFontBox.SelectedItem = FirstFamily(t.DisplayFont);

            TransparencySlider.Value = t.Transparency * 100;
            RoundnessSlider.Value = t.Roundness * 100;
            DockSizeSlider.Value = t.DockIconSize;
            BarHeightSlider.Value = t.TopBarHeight;
            UpdateShapeLabels();
            (t.DockPosition switch { "left" => DockLeft, "right" => DockRight, _ => DockBottom }).IsChecked = true;
        }
        finally { _syncing = false; }
    }

    static string FirstFamily(string font) => font.Split(',')[0].Trim();

    static List<SwatchVm> Swatches(IEnumerable<string> hexes, string current)
    {
        var cur = current[^6..].ToUpperInvariant();
        return hexes.Select(h => new SwatchVm(h, new SolidColorBrush(ThemeEngine.Parse(h)),
            h[^6..].ToUpperInvariant() == cur ? Visibility.Visible : Visibility.Collapsed)).ToList();
    }

    void UpdateShapeLabels()
    {
        TransparencyText.Text = $"{(int)TransparencySlider.Value}%";
        RoundnessText.Text = $"{(int)RoundnessSlider.Value}%";
        DockSizeText.Text = $"{(int)DockSizeSlider.Value}";
        BarHeightText.Text = $"{(int)BarHeightSlider.Value}";
    }

    void Edit(Action<ThemePack> change)
    {
        if (_syncing) return;
        _app.EditTheme(change);
        // Swatch rings and the note may have changed (e.g. we just forked a built-in).
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { RefreshCustomize(); RefreshThemes(); });
    }

    void AccentSwatch_Click(object s, RoutedEventArgs e)
    {
        if (s is Button { Tag: string hex }) Edit(p => ThemeEngine.SetAccent(p, ThemeEngine.Parse(hex)));
    }

    void BackgroundSwatch_Click(object s, RoutedEventArgs e)
    {
        if (s is Button { Tag: string hex }) Edit(p => ThemeEngine.SetBackground(p, ThemeEngine.Parse(hex)));
    }

    static bool TryHex(string text, out Color c)
    {
        c = default;
        var t = text.Trim();
        if (!t.StartsWith('#')) t = "#" + t;
        if (t.Length != 7) return false;
        try { c = ThemeEngine.Parse(t); return true; } catch { return false; }
    }

    void AccentHex_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyAccentHex(); }
    void AccentHex_LostFocus(object s, RoutedEventArgs e) => ApplyAccentHex();
    void ApplyAccentHex()
    {
        if (TryHex(AccentHex.Text, out var c) && !_app.Themes.Current.Colors.Accent.EndsWith(AccentHex.Text.Trim().TrimStart('#'), StringComparison.OrdinalIgnoreCase))
            Edit(p => ThemeEngine.SetAccent(p, c));
    }

    void BackgroundHex_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyBackgroundHex(); }
    void BackgroundHex_LostFocus(object s, RoutedEventArgs e) => ApplyBackgroundHex();
    void ApplyBackgroundHex()
    {
        if (TryHex(BackgroundHex.Text, out var c) && !_app.Themes.Current.Colors.Surface.EndsWith(BackgroundHex.Text.Trim().TrimStart('#'), StringComparison.OrdinalIgnoreCase))
            Edit(p => ThemeEngine.SetBackground(p, c));
    }

    void Font_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_syncing || UiFontBox.SelectedItem is not string ui || HeadingFontBox.SelectedItem is not string heading) return;
        Edit(p => { p.Font = $"{ui}, Segoe UI"; p.DisplayFont = $"{heading}, Segoe UI"; });
    }

    void Shape_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        // Sliders coerce their values while the XAML is still loading; ignore until it's done.
        if (!IsInitialized) return;
        UpdateShapeLabels();
        if (_syncing) return;
        // Sliders fire continuously while dragging; edit in place without re-syncing the whole section.
        _app.EditTheme(p =>
        {
            p.Transparency = TransparencySlider.Value / 100;
            p.Roundness = RoundnessSlider.Value / 100;
            p.DockIconSize = Math.Round(DockSizeSlider.Value);
            p.TopBarHeight = Math.Round(BarHeightSlider.Value);
        });
        CustNote.Text = $"Editing {_app.Themes.Current.Name}. Changes save automatically.";
    }

    void DockPos_Checked(object s, RoutedEventArgs e)
    {
        var pos = DockLeft.IsChecked == true ? "left" : DockRight.IsChecked == true ? "right" : "bottom";
        if (_app.Themes.Current.DockPosition != pos) Edit(p => p.DockPosition = pos);
    }

    void CustReset_Click(object s, RoutedEventArgs e)
    {
        var t = _app.Themes.Current;
        _app.SetTheme(t.BasedOn ?? t.Id);
        _ = RefreshControlsAsync();
    }

    void CustSaveAs_Click(object s, RoutedEventArgs e)
    {
        var name = SaveAsName.Text.Trim();
        if (name.Length == 0) return;
        var copy = _app.Themes.Duplicate(_app.Themes.Current, name, _app.Themes.Current.Brand);
        _app.SetTheme(copy.Id);
        SaveAsName.Text = "";
        _ = RefreshControlsAsync();
    }

    // ============================================================ wallpaper

    sealed record WallVm(string Path, ImageSource Thumb);

    void RefreshWallpapers()
    {
        var folder = _app.Settings.Desktop.WallpaperFolder;
        WallFolderText.Text = string.IsNullOrEmpty(folder) ? "Showing theme wallpapers" : folder;
        var theme = _app.Themes.Current;
        WallList.ItemsSource = null;
        Task.Run(() =>
        {
            Wallpapers.EnsureGenerated(theme);
            var items = Wallpapers.List(theme, folder).Select(f => new WallVm(f, Thumb(f))).Where(v => v.Thumb is not null).ToList();
            Dispatcher.BeginInvoke(() => WallList.ItemsSource = items);
        });
    }

    static ImageSource Thumb(string path)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 400;
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null!; }
    }

    void Wall_Click(object s, MouseButtonEventArgs e)
    {
        if (WallList.SelectedItem is not WallVm vm) return;
        Wallpapers.Set(vm.Path);
        _app.Settings.Desktop.Wallpaper = vm.Path;
        _app.Settings.Save();
    }

    void WallFolder_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() != true) return;
        _app.Settings.Desktop.WallpaperFolder = dlg.FolderName;
        _app.Settings.Save();
        RefreshWallpapers();
    }
}
