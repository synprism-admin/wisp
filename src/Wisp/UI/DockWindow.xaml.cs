using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop;
using Wisp.Native;

namespace Wisp.UI;

/// <summary>
/// The dock that replaces the taskbar: launcher, pinned apps and open windows with running dots.
/// It's an app bar on the bottom, left or right edge of the primary monitor (per the theme), so
/// maximized windows stop short of it.
/// </summary>
public partial class DockWindow : Window
{
    readonly WispController _app;
    readonly DockModel _model;
    readonly System.Windows.Forms.Screen _screen;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(700) };
    IntPtr _hwnd;
    AppBar? _bar;
    (string Position, double Thickness) _layout;

    // Orientation-dependent bits the XAML binds to.
    public static readonly DependencyProperty DockOrientationProperty = DependencyProperty.Register(nameof(DockOrientation), typeof(Orientation), typeof(DockWindow), new(Orientation.Horizontal));
    public static readonly DependencyProperty DotsOrientationProperty = DependencyProperty.Register(nameof(DotsOrientation), typeof(Orientation), typeof(DockWindow), new(Orientation.Horizontal));
    public static readonly DependencyProperty DotsHAlignProperty = DependencyProperty.Register(nameof(DotsHAlign), typeof(HorizontalAlignment), typeof(DockWindow), new(HorizontalAlignment.Center));
    public static readonly DependencyProperty DotsVAlignProperty = DependencyProperty.Register(nameof(DotsVAlign), typeof(VerticalAlignment), typeof(DockWindow), new(VerticalAlignment.Bottom));
    public static readonly DependencyProperty TooltipPlacementProperty = DependencyProperty.Register(nameof(TooltipPlacement), typeof(System.Windows.Controls.Primitives.PlacementMode), typeof(DockWindow), new(System.Windows.Controls.Primitives.PlacementMode.Top));

    public Orientation DockOrientation { get => (Orientation)GetValue(DockOrientationProperty); set => SetValue(DockOrientationProperty, value); }
    public Orientation DotsOrientation { get => (Orientation)GetValue(DotsOrientationProperty); set => SetValue(DotsOrientationProperty, value); }
    public HorizontalAlignment DotsHAlign { get => (HorizontalAlignment)GetValue(DotsHAlignProperty); set => SetValue(DotsHAlignProperty, value); }
    public VerticalAlignment DotsVAlign { get => (VerticalAlignment)GetValue(DotsVAlignProperty); set => SetValue(DotsVAlignProperty, value); }
    public System.Windows.Controls.Primitives.PlacementMode TooltipPlacement { get => (System.Windows.Controls.Primitives.PlacementMode)GetValue(TooltipPlacementProperty); set => SetValue(TooltipPlacementProperty, value); }

    public DockWindow(WispController app, DockModel model, System.Windows.Forms.Screen? screen = null)
    {
        _app = app;
        _model = model;
        _screen = screen ?? System.Windows.Forms.Screen.PrimaryScreen!;
        InitializeComponent();
        Items.ItemsSource = model.Items;
        ApplyLayout(force: true);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            WindowTracker.IgnoreWindow(_hwnd);
            ApplyLayout(force: true);
        };
        _app.Themes.Applied += OnThemeApplied;
        Closed += (_, _) => { _bar?.Dispose(); _poll.Stop(); _model.Detach(); _app.Themes.Applied -= OnThemeApplied; };
        _poll.Tick += (_, _) => { try { _model.Update(); } catch (Exception ex) { Log.Error("Dock update failed", ex); } };
        _poll.Start();
    }

    public void Reload() => _model.Rebuild();
    public void Pin(string path) => _model.Pin(path);

    DispatcherTimer? _relayout;

    // Theme edits arrive on every slider tick; re-docking is heavier, so wait for a pause.
    void OnThemeApplied(ThemePack _)
    {
        _relayout ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => { _relayout!.Stop(); ApplyLayout(force: false); }, Dispatcher);
        _relayout.Stop();
        _relayout.Start();
    }

    /// <summary>Re-docks when the theme changes the dock's edge or icon size.</summary>
    void ApplyLayout(bool force)
    {
        var theme = _app.Themes.Current;
        var pos = theme.DockPosition is "left" or "right" ? theme.DockPosition : "bottom";
        double thickness = Math.Clamp(theme.DockIconSize, 24, 64) + 18 + 32; // tile + padding/border/gap
        if (!force && _layout == (pos, thickness)) return;
        _layout = (pos, thickness);

        bool vertical = pos != "bottom";
        DockOrientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        DotsOrientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        DotsHAlign = pos switch { "left" => HorizontalAlignment.Left, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        DotsVAlign = vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        TooltipPlacement = pos switch
        {
            "left" => System.Windows.Controls.Primitives.PlacementMode.Right,
            "right" => System.Windows.Controls.Primitives.PlacementMode.Left,
            _ => System.Windows.Controls.Primitives.PlacementMode.Top,
        };
        Bar.HorizontalAlignment = pos switch { "left" => HorizontalAlignment.Left, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        Bar.VerticalAlignment = vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        Bar.Margin = pos switch { "left" => new Thickness(10, 0, 0, 0), "right" => new Thickness(0, 0, 10, 0), _ => new Thickness(0, 0, 0, 10) };
        Divider.Width = vertical ? double.NaN : 1;
        Divider.Height = vertical ? 1 : double.NaN;
        Divider.Margin = vertical ? new Thickness(10, 6, 10, 6) : new Thickness(6, 10, 6, 10);

        if (_hwnd == IntPtr.Zero) return; // not shown yet (or a snapshot)
        _bar?.Dispose();
        var edge = pos switch { "left" => AppBar.Edge.Left, "right" => AppBar.Edge.Right, _ => AppBar.Edge.Bottom };
        _bar = new AppBar(this, edge, thickness, _screen);
        _bar.Positioned += _app.Island.KeepOnTop;
        _app.Island.KeepOnTop();
    }

    // ================================================================ interactions

    static DockItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as DockItem;

    void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            try { _model.Click(item); } catch (Exception ex) { Log.Error("Dock click failed", ex); }
            _model.Update();
        }
    }

    void Item_MiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ItemOf(sender) is { } item)
            try { WindowTracker.Launch(item.LaunchPath); } catch { }
    }

    void Item_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var menu = new ContextMenu();
        foreach (var w in item.Windows)
        {
            var mi = new MenuItem { Header = w.Title.Length > 60 ? w.Title[..60] + "…" : w.Title };
            var h = w.Handle;
            mi.Click += (_, _) => WindowTracker.Activate(h, allowMinimize: false);
            menu.Items.Add(mi);
        }
        if (item.Windows.Count > 0) menu.Items.Add(new Separator());

        var launch = new MenuItem { Header = item.IsRunning ? "New window" : $"Open {item.Name}" };
        launch.Click += (_, _) => { try { WindowTracker.Launch(item.LaunchPath); } catch { } };
        menu.Items.Add(launch);

        var pin = new MenuItem { Header = item.IsPinned ? "Unpin from dock" : "Pin to dock" };
        pin.Click += (_, _) => _model.TogglePin(item);
        menu.Items.Add(pin);

        if (item.IsRunning)
        {
            var close = new MenuItem { Header = item.Windows.Count > 1 ? $"Close {item.Windows.Count} windows" : "Close window" };
            close.Click += (_, _) => { foreach (var w in item.Windows) WindowTracker.Close(w.Handle); };
            menu.Items.Add(close);
        }

        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
        e.Handled = true;
    }

    void Item_Enter(object sender, MouseEventArgs e) => AnimateHover((Grid)sender, true);
    void Item_Leave(object sender, MouseEventArgs e) => AnimateHover((Grid)sender, false);

    static void AnimateHover(Grid g, bool on)
    {
        var dur = TimeSpan.FromMilliseconds(140);
        if (g.Children[0] is Border hover) hover.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, dur));
        if (g.Children[2] is Grid icon)
        {
            // Transforms declared in a template are frozen; swap in a live one on first hover.
            if (icon.RenderTransform is not ScaleTransform st || st.IsFrozen)
                icon.RenderTransform = st = new ScaleTransform();
            var a = new DoubleAnimation(on ? 1.14 : 1, dur) { EasingFunction = new CubicEase() };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    void Launcher_Click(object sender, RoutedEventArgs e) => _app.ShowLauncher();
}

/// <summary>Turns a running-window count into that many dots.</summary>
public sealed class DotsConverter : IValueConverter
{
    public static readonly DotsConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => Enumerable.Range(0, value is int n ? n : 0).ToArray();
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
