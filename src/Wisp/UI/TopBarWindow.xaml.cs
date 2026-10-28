using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop;
using Wisp.Native;

namespace Wisp.UI;

/// <summary>
/// The top bar: brand/launcher and focused app on the left, the island in the middle (it floats
/// over the bar), status on the right. An app bar, so maximized windows start below it.
/// </summary>
public partial class TopBarWindow : Window
{
    readonly WispController _app;
    readonly System.Windows.Forms.Screen _screen;
    readonly bool _primary;
    IntPtr _monitor;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1.5) };
    AppBar? _bar;
    IntPtr _hwnd;
    double _height;
    DispatcherTimer? _redock;
    string _lastFocused = "";

    public TopBarWindow(WispController app, System.Windows.Forms.Screen? screen = null)
    {
        _app = app;
        _screen = screen ?? System.Windows.Forms.Screen.PrimaryScreen!;
        _primary = _screen.Primary;
        _monitor = Win32.MonitorOf(_screen);
        InitializeComponent();
        // The island lives on the main screen; other screens get a clock in the middle instead.
        CenterClock.Visibility = _primary ? Visibility.Collapsed : Visibility.Visible;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            WindowTracker.IgnoreWindow(_hwnd);
            Dock();
        };
        Closed += (_, _) => { _bar?.Dispose(); _tick.Stop(); };

        _tick.Tick += (_, _) => Refresh();
        _tick.Start();
        _app.Media.Changed += () => Dispatcher.BeginInvoke(async () => await RefreshMediaAsync());
        _app.Themes.Applied += p => Dispatcher.BeginInvoke(() =>
        {
            BrandText.Text = p.Brand;
            if (Math.Abs(p.TopBarHeight - _height) > 0.5)
            {
                _redock?.Stop();
                _redock = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
                    (_, _) => { _redock!.Stop(); Dock(); }, Dispatcher);
                _redock.Start();
            }
        });
        BrandText.Text = _app.Themes.Current.Brand;
        Refresh();
        _ = RefreshMediaAsync();
    }

    /// <summary>(Re)registers the bar at the theme's height.</summary>
    void Dock()
    {
        if (_hwnd == IntPtr.Zero) return;
        _height = Math.Clamp(_app.Themes.Current.TopBarHeight, 30, 60);
        _bar?.Dispose();
        _bar = new AppBar(this, AppBar.Edge.Top, _height, _screen);
        if (!_primary) return; // the island and its z-order belong to the main screen's bar
        _bar.Positioned += _app.Island.KeepOnTop;
        _app.Island.KeepOnTop();
        _bar.FullScreenChanged += full => _app.Island.Visibility = full ? Visibility.Hidden : Visibility.Visible;
        _app.Island.SetBarMode(true, _height);
    }

    void Refresh()
    {
        DateText.Text = DateTime.Now.ToString("ddd MMM d");
        if (CalendarPopup.IsOpen) _calendar?.Tick();
        CenterClock.Text = DateTime.Now.ToString("h:mm");

        var fg = Win32.GetForegroundWindow();
        if (fg != _hwnd)
        {
            // Each bar names the focused app only when it's on that bar's screen.
            var name = Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST) == _monitor ? AppName(fg) : "";
            if (name != _lastFocused) { FocusedApp.Text = name; _lastFocused = name; }
        }

        try
        {
            int vol = _app.Media.Volume;
            bool muted = _app.Media.Muted;
            VolText.Text = muted ? "" : $"{vol}";
            VolIcon.Text = muted || vol == 0 ? "\uE74F" : vol < 34 ? "\uE993" : vol < 67 ? "\uE994" : "\uE995";
        }
        catch { }

        bool online = NetworkInterface.GetIsNetworkAvailable();
        bool wifi = online && NetworkInterface.GetAllNetworkInterfaces().Any(n =>
            n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        NetIcon.Text = !online ? "\uF384" : wifi ? "\uE701" : "\uE839";

        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        if (power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery))
            BatteryPanel.Visibility = Visibility.Collapsed;
        else
        {
            BatteryPanel.Visibility = Visibility.Visible;
            int pct = (int)Math.Round(power.BatteryLifePercent * 100);
            BatteryText.Text = $"{pct}%";
            bool charging = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            BatteryIcon.Text = charging ? "\uEA93" : ((char)(0xE850 + Math.Clamp(pct / 10, 0, 10))).ToString();
        }

    }

    static string AppName(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        Win32.GetWindowThreadProcessId(h, out uint pid);
        try
        {
            var p = Process.GetProcessById((int)pid);
            if (p.ProcessName is "explorer" && Win32.WindowTitle(h) is "" or "Program Manager") return "Desktop";
            var desc = p.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(desc) ? p.ProcessName : desc;
        }
        catch { return Win32.WindowTitle(h); }
    }

    async Task RefreshMediaAsync()
    {
        var np = await _app.Media.CurrentAsync();
        if (np is null || string.IsNullOrEmpty(np.Title))
        {
            MediaButton.Visibility = Visibility.Collapsed;
            return;
        }
        MediaButton.Visibility = Visibility.Visible;
        MediaText.Text = string.IsNullOrEmpty(np.Artist) ? np.Title : $"{np.Title} · {np.Artist}";
        MediaIcon.Text = np.Playing ? "\uE769" : "\uE768";
    }

    void Brand_Click(object s, RoutedEventArgs e) => _app.ShowLauncher();
    void Media_Click(object s, RoutedEventArgs e) => _app.Media.PlayPause();
    void Power_Click(object s, RoutedEventArgs e) => _app.Island.OpenPanel("power");

    CalendarFlyout? _calendar;

    void Date_Click(object s, RoutedEventArgs e)
    {
        if (CalendarPopup.IsOpen) { CalendarPopup.IsOpen = false; return; }
        CalendarPopup.Child = _calendar ??= new CalendarFlyout(_app);
        _calendar.Open();
        // Right-align the flyout under the date so it doesn't run off the screen edge.
        _calendar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        CalendarPopup.HorizontalOffset = DateButton.ActualWidth - _calendar.DesiredSize.Width;
        CalendarPopup.IsOpen = true;
    }
    void Network_Click(object s, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });
    void Tray_Click(object s, RoutedEventArgs e) { OverflowPopup.IsOpen = false; _app.Desktop.ToggleTaskbarPeek(); }

    // ================================================================ notification area

    bool _editTray;

    /// <summary>Binds this bar to the shared tray host (or hides the tray if there isn't one).</summary>
    public void AttachTray(Tray? tray)
    {
        _tray = tray;
        SetEditTray(false);
        _hiddenView = tray?.View(pinned: false);
        PinnedTray.ItemsSource = tray?.View(pinned: true);
        HiddenTray.ItemsSource = _hiddenView;
        OverflowButton.Visibility = tray is null ? Visibility.Collapsed : Visibility.Visible;
    }

    Tray? _tray;
    System.ComponentModel.ICollectionView? _hiddenView;

    void Overflow_Click(object s, RoutedEventArgs e)
    {
        if (_tray is null) return;
        HiddenEmpty.Visibility = _hiddenView?.IsEmpty != false ? Visibility.Visible : Visibility.Collapsed;
        OverflowPopup.IsOpen = !OverflowPopup.IsOpen;
        if (!OverflowPopup.IsOpen) SetEditTray(false);
    }

    void EditTray_Click(object s, RoutedEventArgs e) => SetEditTray(!_editTray);

    void SetEditTray(bool on)
    {
        _editTray = on;
        // The template's badges bind to Window.Tag, so one property flips them all.
        Tag = on ? Visibility.Visible : Visibility.Collapsed;
        EditTrayButton.Content = on ? "Done" : "Edit";
        EditHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    static ManagedShell.WindowsTray.NotifyIcon? IconOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ManagedShell.WindowsTray.NotifyIcon;

    void TrayIcon_MouseEnter(object s, MouseEventArgs e)
    {
        if (s is System.Windows.Controls.Border b) b.Background = (System.Windows.Media.Brush)FindResource("SurfaceHover");
        if (!_editTray) IconOf(s)?.IconMouseEnter(Tray.CursorParam());
    }

    void TrayIcon_MouseLeave(object s, MouseEventArgs e)
    {
        if (s is System.Windows.Controls.Border b) b.Background = System.Windows.Media.Brushes.Transparent;
        if (!_editTray) IconOf(s)?.IconMouseLeave(Tray.CursorParam());
    }

    void TrayIcon_MouseMove(object s, MouseEventArgs e)
    {
        TrayIcon_DragStart(e);
        if (_trayDragging is not null) return;
        if (!_editTray) IconOf(s)?.IconMouseMove(Tray.CursorParam());
    }

    // A left press on an icon in the bar might become a drag, so its click is held until the
    // button comes up; everything else goes straight to the app.
    ManagedShell.WindowsTray.NotifyIcon? _trayPressed, _trayDragging;
    Point _trayPressAt;

    void TrayIcon_MouseDown(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_editTray) return;
        var icon = IconOf(s);
        if (icon is null) return;
        if (e.ChangedButton == MouseButton.Left && icon.IsPinned)
        {
            _trayPressed = icon;
            _trayPressAt = e.GetPosition(PinnedTray);
            return;
        }
        icon.IconMouseDown(e.ChangedButton, Tray.CursorParam(), System.Windows.Forms.SystemInformation.DoubleClickTime);
    }

    void TrayIcon_DragStart(MouseEventArgs e)
    {
        if (_trayPressed is null || _trayDragging is not null || e.LeftButton != MouseButtonState.Pressed) return;
        if (!DragReorder.PastThreshold(_trayPressAt, e.GetPosition(PinnedTray))) return;
        _trayDragging = _trayPressed;
        if (DragReorder.ContainerOf(PinnedTray, _trayDragging) is { } c) c.Opacity = 0.55;
        PinnedTray.MouseMove += PinnedTray_DragMove;
        PinnedTray.MouseLeftButtonUp += PinnedTray_Drop;
        PinnedTray.LostMouseCapture += PinnedTray_LostCapture;
        PinnedTray.CaptureMouse();
    }

    void PinnedTray_DragMove(object s, MouseEventArgs e)
    {
        if (_trayDragging is null) return;
        int target = DragReorder.IndexAt(PinnedTray, e.GetPosition(PinnedTray), System.Windows.Controls.Orientation.Horizontal);
        if (target < 0 || PinnedTray.Items[target] is not ManagedShell.WindowsTray.NotifyIcon over || over == _trayDragging) return;
        // Take the slot of the icon we're over; the bar's view re-sorts live by pin order.
        _trayDragging.Pin(over.PinOrder);
    }

    void PinnedTray_Drop(object s, MouseButtonEventArgs e) { e.Handled = true; EndTrayDrag(); }
    void PinnedTray_LostCapture(object s, MouseEventArgs e) => EndTrayDrag();

    void EndTrayDrag()
    {
        if (_trayDragging is null) return;
        var icon = _trayDragging;
        _trayDragging = null;
        _trayPressed = null;
        PinnedTray.MouseMove -= PinnedTray_DragMove;
        PinnedTray.MouseLeftButtonUp -= PinnedTray_Drop;
        PinnedTray.LostMouseCapture -= PinnedTray_LostCapture;
        if (PinnedTray.IsMouseCaptured) PinnedTray.ReleaseMouseCapture();
        if (DragReorder.ContainerOf(PinnedTray, icon) is { } c) c.Opacity = 1;
        _tray?.SaveOrder();
    }

    void TrayIcon_MouseUp(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var icon = IconOf(s);
        if (icon is null) return;
        if (_editTray)
        {
            _tray?.TogglePin(icon);
            HiddenEmpty.Visibility = _hiddenView?.IsEmpty != false ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        // Clicking a hidden icon opens its app's UI; close the flyout like Windows does.
        if (OverflowPopup.IsOpen && !icon.IsPinned) OverflowPopup.IsOpen = false;
        if (e.ChangedButton == MouseButton.Left && _trayPressed == icon)
        {
            // A held click that never became a drag: deliver it now.
            _trayPressed = null;
            icon.IconMouseDown(MouseButton.Left, Tray.CursorParam(), System.Windows.Forms.SystemInformation.DoubleClickTime);
        }
        icon.IconMouseUp(e.ChangedButton, Tray.CursorParam(), System.Windows.Forms.SystemInformation.DoubleClickTime);
    }

    /// <summary>Tells the app where its icon is, so its popups and balloons anchor to it.</summary>
    void TrayIcon_Loaded(object s, RoutedEventArgs e)
    {
        if (!_primary || s is not FrameworkElement el || IconOf(s) is not { } icon || PresentationSource.FromVisual(el) is null) return;
        var tl = el.PointToScreen(new Point(0, 0));
        var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
        icon.Placement = new ManagedShell.Interop.NativeMethods.Rect { Left = (int)tl.X, Top = (int)tl.Y, Right = (int)br.X, Bottom = (int)br.Y };
    }

    void Volume_Click(object s, RoutedEventArgs e)
    {
        try { _app.Media.Muted = !_app.Media.Muted; } catch { }
        Refresh();
    }

    void Volume_Wheel(object s, MouseWheelEventArgs e)
    {
        try { _app.Media.Volume += e.Delta > 0 ? 4 : -4; } catch { }
        Refresh();
        e.Handled = true;
    }
}
