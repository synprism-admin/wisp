using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Desktop;
using Wisp.Native;

namespace Wisp.UI;

/// <summary>
/// The dock that replaces the taskbar: launcher, pinned apps and open windows with running dots.
///
/// Two windows: an invisible "spacer" registered as an app bar reserves the strip at the screen
/// edge (so maximized windows stop short of it), and this window, which draws the dock, is taller
/// than that strip so magnified icons can rise above the bar. Its empty areas are transparent and
/// click-through.
///
/// Motion: icons magnify around the pointer (a smooth, frame-by-frame fisheye), drag lifts an icon
/// that follows the pointer while the others glide aside, dragging a pinned icon off the dock
/// removes it, and launching an app bounces its icon.
/// </summary>
public partial class DockWindow : Window
{
    readonly WispController _app;
    readonly DockModel _model;
    readonly System.Windows.Forms.Screen _screen;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(700) };
    IntPtr _hwnd;
    Window? _spacer;
    AppBar? _bar;
    (string Position, double Thickness, double Headroom) _layout;

    // Orientation-dependent bits the XAML binds to.
    public static readonly DependencyProperty DockOrientationProperty = DependencyProperty.Register(nameof(DockOrientation), typeof(Orientation), typeof(DockWindow), new(Orientation.Horizontal));
    public static readonly DependencyProperty DotsOrientationProperty = DependencyProperty.Register(nameof(DotsOrientation), typeof(Orientation), typeof(DockWindow), new(Orientation.Horizontal));
    public static readonly DependencyProperty DotsHAlignProperty = DependencyProperty.Register(nameof(DotsHAlign), typeof(HorizontalAlignment), typeof(DockWindow), new(HorizontalAlignment.Center));
    public static readonly DependencyProperty DotsVAlignProperty = DependencyProperty.Register(nameof(DotsVAlign), typeof(VerticalAlignment), typeof(DockWindow), new(VerticalAlignment.Bottom));
    public static readonly DependencyProperty ItemHAlignProperty = DependencyProperty.Register(nameof(ItemHAlign), typeof(HorizontalAlignment), typeof(DockWindow), new(HorizontalAlignment.Center));
    public static readonly DependencyProperty ItemVAlignProperty = DependencyProperty.Register(nameof(ItemVAlign), typeof(VerticalAlignment), typeof(DockWindow), new(VerticalAlignment.Bottom));
    public static readonly DependencyProperty TooltipPlacementProperty = DependencyProperty.Register(nameof(TooltipPlacement), typeof(PlacementMode), typeof(DockWindow), new(PlacementMode.Top));

    public Orientation DockOrientation { get => (Orientation)GetValue(DockOrientationProperty); set => SetValue(DockOrientationProperty, value); }
    public Orientation DotsOrientation { get => (Orientation)GetValue(DotsOrientationProperty); set => SetValue(DotsOrientationProperty, value); }
    public HorizontalAlignment DotsHAlign { get => (HorizontalAlignment)GetValue(DotsHAlignProperty); set => SetValue(DotsHAlignProperty, value); }
    public VerticalAlignment DotsVAlign { get => (VerticalAlignment)GetValue(DotsVAlignProperty); set => SetValue(DotsVAlignProperty, value); }
    public HorizontalAlignment ItemHAlign { get => (HorizontalAlignment)GetValue(ItemHAlignProperty); set => SetValue(ItemHAlignProperty, value); }
    public VerticalAlignment ItemVAlign { get => (VerticalAlignment)GetValue(ItemVAlignProperty); set => SetValue(ItemVAlignProperty, value); }
    public PlacementMode TooltipPlacement { get => (PlacementMode)GetValue(TooltipPlacementProperty); set => SetValue(TooltipPlacementProperty, value); }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    public DockWindow(WispController app, DockModel model, System.Windows.Forms.Screen? screen = null)
    {
        _app = app;
        _model = model;
        _screen = screen ?? System.Windows.Forms.Screen.PrimaryScreen!;
        InitializeComponent();
        Items.ItemsSource = model.Items;
        Row.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // hit-testable, so the pointer isn't "lost" between icons
        ApplyLayout(force: true);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            WindowTracker.IgnoreWindow(_hwnd);
            ApplyLayout(force: true);
        };
        _app.Themes.Applied += OnThemeApplied;
        Closed += (_, _) =>
        {
            StopFrames();
            _bar?.Dispose();
            _spacer?.Close();
            _poll.Stop();
            _model.Detach();
            _app.Themes.Applied -= OnThemeApplied;
        };
        _poll.Tick += (_, _) => { try { _model.Update(); } catch (Exception ex) { Log.Error("Dock update failed", ex); } };
        _poll.Start();
    }

    public void Reload() => _model.Rebuild();
    public void Pin(string path) => _model.Pin(path);

    // ================================================================ layout & docking

    DispatcherTimer? _relayout;

    // Theme edits arrive on every slider tick; re-docking is heavier, so wait for a pause.
    void OnThemeApplied(ThemePack _)
    {
        _relayout ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => { _relayout!.Stop(); ApplyLayout(force: false); }, Dispatcher);
        _relayout.Stop();
        _relayout.Start();
    }

    double Tile => Math.Clamp(_app.Themes.Current.DockIconSize, 24, 64) + 18;
    double Magnification => Math.Clamp(_app.Themes.Current.DockMagnification, 1, 2.2);

    /// <summary>Re-docks when the theme changes the dock's edge, icon size or magnification.</summary>
    void ApplyLayout(bool force)
    {
        var pos = _app.Themes.Current.DockPosition is "left" or "right" ? _app.Themes.Current.DockPosition : "bottom";
        double tile = Tile;
        double thickness = tile + 32;                     // the reserved strip: bar + gap to the screen edge
        double headroom = tile * (Magnification - 1) + 12; // extra drawing room for magnified icons
        if (!force && _layout == (pos, thickness, headroom)) return;
        _layout = (pos, thickness, headroom);

        bool vertical = pos != "bottom";
        DockOrientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        DotsOrientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        DotsHAlign = pos switch { "left" => HorizontalAlignment.Left, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        DotsVAlign = vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        // Icons grow away from the screen edge.
        ItemHAlign = pos switch { "left" => HorizontalAlignment.Left, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        ItemVAlign = vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        TooltipPlacement = pos switch { "left" => PlacementMode.Right, "right" => PlacementMode.Left, _ => PlacementMode.Top };

        BarHost.HorizontalAlignment = ItemHAlign;
        BarHost.VerticalAlignment = vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        BarHost.Margin = pos switch { "left" => new Thickness(10, 0, 0, 0), "right" => new Thickness(0, 0, 10, 0), _ => new Thickness(0, 0, 0, 10) };
        Row.HorizontalAlignment = ItemHAlign;
        Row.VerticalAlignment = vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        Items.HorizontalAlignment = ItemHAlign;
        Items.VerticalAlignment = Row.VerticalAlignment;
        LauncherButton.HorizontalAlignment = ItemHAlign;
        LauncherButton.VerticalAlignment = ItemVAlign;

        // The bar keeps its thickness however big the icons get.
        double barThickness = tile + 14;
        BarBackground.Width = vertical ? barThickness : double.NaN;
        BarBackground.Height = vertical ? double.NaN : barThickness;
        BarBackground.HorizontalAlignment = vertical ? ItemHAlign : HorizontalAlignment.Stretch;
        BarBackground.VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;

        Divider.Width = vertical ? double.NaN : 1;
        Divider.Height = vertical ? 1 : double.NaN;
        Divider.Margin = vertical ? new Thickness(10, 6, 10, 6) : new Thickness(6, 10, 6, 10);

        if (_hwnd == IntPtr.Zero) return; // not shown yet (or a snapshot)
        EnsureSpacer();
        _bar?.Dispose();
        var edge = pos switch { "left" => AppBar.Edge.Left, "right" => AppBar.Edge.Right, _ => AppBar.Edge.Bottom };
        _bar = new AppBar(_spacer!, edge, thickness, _screen);
        _bar.Positioned += OnStripPositioned;
        _bar.FullScreenChanged += full => Visibility = full ? Visibility.Hidden : Visibility.Visible;
        OnStripPositioned();
    }

    /// <summary>The invisible, click-through window that holds the app-bar reservation.</summary>
    void EnsureSpacer()
    {
        if (_spacer is not null) return;
        _spacer = new Window
        {
            Title = "Wisp Dock Space", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Width = 1, Height = 1, Left = -32000, Top = -32000,
        };
        _spacer.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(_spacer).Handle;
            Win32.SetExStyle(h, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT);
            WindowTracker.IgnoreWindow(h);
        };
        _spacer.Show();
    }

    /// <summary>Lays this (taller) window over the reserved strip, extended inward by the headroom.</summary>
    void OnStripPositioned()
    {
        if (_spacer is null || _hwnd == IntPtr.Zero) return;
        if (!GetWindowRect(new WindowInteropHelper(_spacer).Handle, out var r)) return;
        int extra = (int)Math.Ceiling(_layout.Headroom * Win32.ScaleOf(_screen));
        switch (_layout.Position)
        {
            case "left": r.Right += extra; break;
            case "right": r.Left -= extra; break;
            default: r.Top -= extra; break;
        }
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        _app.Island.KeepOnTop();
    }

    // ================================================================ per-icon effects

    sealed record Fx(ScaleTransform Scale, TranslateTransform Slide, TranslateTransform Bounce);
    readonly Dictionary<Grid, Fx> _fx = [];

    /// <summary>Live transforms for each icon (template-declared ones are frozen, so they're made here).</summary>
    void Item_Loaded(object sender, RoutedEventArgs e)
    {
        var g = (Grid)sender;
        if (_fx.ContainsKey(g)) return;
        var fx = new Fx(new ScaleTransform(1, 1), new TranslateTransform(), new TranslateTransform());
        g.LayoutTransform = fx.Scale;
        var group = new TransformGroup();
        group.Children.Add(fx.Slide);
        group.Children.Add(fx.Bounce);
        g.RenderTransform = group;
        _fx[g] = fx;
        g.Unloaded += (_, _) => _fx.Remove(g);
        if (_dragging is not null && g.DataContext == _dragging) g.Opacity = 0; // a re-created container mid-drag stays a gap
    }

    /// <summary>The icon grids in dock order.</summary>
    List<Grid> IconGrids()
    {
        var list = new List<Grid>();
        for (int i = 0; i < Items.Items.Count; i++)
            if (Items.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter cp && VisualTreeHelper.GetChildrenCount(cp) > 0 &&
                VisualTreeHelper.GetChild(cp, 0) is Grid g)
                list.Add(g);
        return list;
    }

    Grid? GridOf(DockItem item) => _fx.Keys.FirstOrDefault(g => g.DataContext == item);

    // ================================================================ magnification

    bool _framing;
    double? _simulatedPointer; // snapshots: pretend the pointer is here (fraction along the icons)

    /// <summary>For snapshots: settle the magnification as if the pointer were at this fraction of the dock.</summary>
    public void SimulateHover(double fraction)
    {
        _simulatedPointer = fraction;
        foreach (var g in IconGrids()) Item_Loaded(g, new RoutedEventArgs()); // offscreen: Loaded never fires
        for (int i = 0; i < 80; i++) { OnFrame(null, EventArgs.Empty); Items.UpdateLayout(); }
        Log.Info($"Simulated hover at {fraction:P0}: {IconGrids().Count} icons, scales {string.Join(" ", IconGrids().Select(g => _fx.TryGetValue(g, out var x) ? x.Scale.ScaleX.ToString("0.00") : "-"))}");
    }

    void Row_MouseMove(object sender, MouseEventArgs e) => StartFrames();
    void Row_MouseLeave(object sender, MouseEventArgs e) => StartFrames();

    void StartFrames()
    {
        if (_framing) return;
        _framing = true;
        CompositionTarget.Rendering += OnFrame;
    }

    void StopFrames()
    {
        if (!_framing) return;
        _framing = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>
    /// One animation frame: each icon eases toward a size given by a bell curve around the pointer.
    /// The pointer is first mapped back to "unmagnified" coordinates so the curve doesn't chase its
    /// own tail as icons grow.
    /// </summary>
    void OnFrame(object? sender, EventArgs e)
    {
        var grids = IconGrids();
        double slot = Tile + 2;
        bool vertical = DockOrientation == Orientation.Vertical;
        double peak = Magnification;
        bool hovering = Row.IsMouseOver || _dragging is not null || _simulatedPointer is not null;

        double? at = null;
        if (hovering && peak > 1.01)
        {
            var p = Mouse.GetPosition(Items);
            double pointer = vertical ? p.Y : p.X, acc = 0;
            if (_simulatedPointer is { } sim) pointer = sim * (vertical ? Items.ActualHeight : Items.ActualWidth);
            for (int i = 0; i < grids.Count; i++)
            {
                double size = slot * (_fx.TryGetValue(grids[i], out var f) ? f.Scale.ScaleX : 1);
                if (pointer < acc + size || i == grids.Count - 1)
                {
                    at = (i + Math.Clamp((pointer - acc) / size, -2, 3)) * slot;
                    break;
                }
                acc += size;
            }
        }

        bool settled = true;
        for (int i = 0; i < grids.Count; i++)
        {
            if (!_fx.TryGetValue(grids[i], out var fx)) continue;
            double target = 1;
            if (at is { } a)
            {
                double d = (a - (i + 0.5) * slot) / slot; // distance in icons
                target = 1 + (peak - 1) * Math.Exp(-(d * d) / (2 * 0.95 * 0.95));
            }
            double cur = fx.Scale.ScaleX;
            double next = cur + (target - cur) * 0.28;
            if (Math.Abs(target - next) < 0.002) next = target; else settled = false;
            if (next != cur) { fx.Scale.ScaleX = next; fx.Scale.ScaleY = next; }
        }
        if (settled && !hovering) StopFrames();
    }

    // ================================================================ drag: lift, glide, drop, remove

    static DockItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as DockItem;

    DockItem? _pressed;
    Point _pressAt;
    DockItem? _dragging;
    bool _swallowClick, _removing;
    Popup? _ghost;
    Grid? _ghostBody;
    Border? _ghostBadge;

    void Item_Press(object sender, MouseButtonEventArgs e)
    {
        _pressed = ItemOf(sender);
        _pressAt = e.GetPosition(Items);
        _swallowClick = false;
    }

    void Item_Move(object sender, MouseEventArgs e)
    {
        if (_dragging is not null || _pressed is null || e.LeftButton != MouseButtonState.Pressed) return;
        if (!DragReorder.PastThreshold(_pressAt, e.GetPosition(Items))) return;
        BeginDrag(_pressed);
    }

    void BeginDrag(DockItem item)
    {
        _dragging = item;
        _removing = false;
        _poll.Stop(); // window list refreshes would shuffle items mid-drag

        // The icon lifts off the dock into a floating copy that follows the pointer…
        double size = Math.Clamp(_app.Themes.Current.DockIconSize, 24, 64) * 1.25;
        _ghostBadge = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Background = (Brush)FindResource("Danger"), Opacity = 0,
            Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 10, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _ghostBody = new Grid
        {
            Width = size, Height = size, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Direction = 270, Opacity = 0.5 },
        };
        _ghostBody.Children.Add(new System.Windows.Controls.Image { Source = item.Icon });
        _ghostBody.Children.Add(_ghostBadge);
        _ghost = new Popup
        {
            AllowsTransparency = true, Placement = PlacementMode.Absolute, IsHitTestVisible = false, PlacementTarget = this,
            Child = new Border { Padding = new Thickness(12), Child = _ghostBody },
        };
        MoveGhost();
        _ghost.IsOpen = true;
        Pop(_ghostBody, 1, 1.12, 140);

        // …leaving a gap where it was.
        if (GridOf(item) is { } g) g.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));

        Items.MouseMove += Items_DragMove;
        Items.MouseLeftButtonUp += Items_Drop;
        Items.LostMouseCapture += Items_LostCapture;
        Items.CaptureMouse();
        StartFrames();
    }

    /// <summary>Centers the floating icon on the pointer (screen pixels → popup units).</summary>
    void MoveGhost()
    {
        if (_ghost is null || _ghostBody is null) return;
        Win32.GetCursorPos(out var c);
        var source = PresentationSource.FromVisual(this);
        var p = source?.CompositionTarget?.TransformFromDevice.Transform(new Point(c.X, c.Y)) ?? new Point(c.X, c.Y);
        double half = _ghostBody.Width / 2 + 12;
        _ghost.HorizontalOffset = p.X - half;
        _ghost.VerticalOffset = p.Y - half;
    }

    /// <summary>How far the pointer has been pulled away from the dock, in icons.</summary>
    double PullDistance()
    {
        var p = Mouse.GetPosition(BarBackground);
        double away = _layout.Position switch
        {
            "left" => p.X - BarBackground.ActualWidth,
            "right" => -p.X,
            _ => -p.Y,
        };
        return away / Tile;
    }

    void Items_DragMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null) return;
        MoveGhost();

        // Pinned icons dragged well off the dock get removed when dropped.
        bool removing = _dragging.IsPinned && PullDistance() > 1.3;
        if (removing != _removing)
        {
            _removing = removing;
            _ghostBadge?.BeginAnimation(OpacityProperty, new DoubleAnimation(removing ? 1 : 0, TimeSpan.FromMilliseconds(120)));
            _ghostBody?.BeginAnimation(OpacityProperty, new DoubleAnimation(removing ? 0.6 : 1, TimeSpan.FromMilliseconds(120)));
        }
        if (_removing) return;

        int target = DragReorder.IndexAt(Items, e.GetPosition(Items), DockOrientation);
        int current = _model.Items.IndexOf(_dragging);
        if (target >= 0 && current >= 0 && target != current) GlideMove(current, target);
    }

    /// <summary>Moves an item and animates the others from where they were to where they now are.</summary>
    void GlideMove(int from, int to)
    {
        var before = IconGrids().Where(g => g.DataContext is DockItem)
            .ToDictionary(g => (DockItem)g.DataContext, g => g.TranslatePoint(new Point(0, 0), Items));
        _model.Items.Move(from, to);
        Items.UpdateLayout();
        bool vertical = DockOrientation == Orientation.Vertical;
        foreach (var g in IconGrids())
        {
            if (g.DataContext is not DockItem item || item == _dragging || !before.TryGetValue(item, out var old)) continue;
            if (!_fx.TryGetValue(g, out var fx)) continue;
            var now = g.TranslatePoint(new Point(0, 0), Items);
            double delta = vertical ? old.Y - now.Y : old.X - now.X;
            if (Math.Abs(delta) < 0.5) continue;
            var glide = new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(210)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            fx.Slide.BeginAnimation(vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty, glide);
        }
        if (_dragging is not null && GridOf(_dragging) is { } dg) { dg.BeginAnimation(OpacityProperty, null); dg.Opacity = 0; }
    }

    void Items_Drop(object sender, MouseButtonEventArgs e) { e.Handled = true; EndDrag(); }
    void Items_LostCapture(object sender, MouseEventArgs e) => EndDrag();

    void EndDrag()
    {
        if (_dragging is null) return;
        var item = _dragging;
        bool remove = _removing;
        _dragging = null;
        _pressed = null;
        _removing = false;
        _swallowClick = true; // the button-up that ended the drag isn't a click
        Items.MouseMove -= Items_DragMove;
        Items.MouseLeftButtonUp -= Items_Drop;
        Items.LostMouseCapture -= Items_LostCapture;
        if (Items.IsMouseCaptured) Items.ReleaseMouseCapture();

        var ghost = _ghost;
        var body = _ghostBody;
        _ghost = null;
        if (remove)
        {
            // Poof: the floating icon swells and fades away, and the pin is gone.
            if (body is not null)
            {
                Pop(body, 1.12, 1.7, 220);
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
                fade.Completed += (_, _) => { if (ghost is not null) ghost.IsOpen = false; };
                body.BeginAnimation(OpacityProperty, fade);
            }
            _model.TogglePin(item);
        }
        else
        {
            // Settle: the floating icon glides into its slot, then the real one takes over.
            var grid = GridOf(item);
            if (ghost is not null && body is not null && grid is not null && PresentationSource.FromVisual(grid) is not null)
            {
                var slot = grid.PointToScreen(new Point(grid.ActualWidth / 2, grid.ActualHeight / 2));
                var source = PresentationSource.FromVisual(this);
                var p = source?.CompositionTarget?.TransformFromDevice.Transform(slot) ?? slot;
                double half = body.Width / 2 + 12;
                var dur = TimeSpan.FromMilliseconds(170);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var x = new DoubleAnimation(p.X - half, dur) { EasingFunction = ease };
                x.Completed += (_, _) =>
                {
                    ghost.IsOpen = false;
                    grid.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
                };
                ghost.BeginAnimation(Popup.HorizontalOffsetProperty, x);
                ghost.BeginAnimation(Popup.VerticalOffsetProperty, new DoubleAnimation(p.Y - half, dur) { EasingFunction = ease });
                Pop(body, 1.12, 1, 170);
            }
            else
            {
                if (ghost is not null) ghost.IsOpen = false;
                if (grid is not null) { grid.BeginAnimation(OpacityProperty, null); grid.Opacity = 1; }
            }
            _model.CommitOrder(item);
        }
        _poll.Start();
    }

    static void Pop(FrameworkElement el, double from, double to, int ms)
    {
        if (el.RenderTransform is not ScaleTransform st) return;
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ================================================================ clicks

    void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (_swallowClick) { _swallowClick = false; return; }
        if (ItemOf(sender) is not { } item) return;
        if (_model.WillLaunch(item) && sender is Grid g) Bounce(g);
        try { _model.Click(item); } catch (Exception ex) { Log.Error("Dock click failed", ex); }
        _model.Update();
    }

    /// <summary>The launch bounce: two hops away from the screen edge, the second smaller.</summary>
    void Bounce(Grid g)
    {
        if (!_fx.TryGetValue(g, out var fx)) return;
        double h = Tile * 0.42;
        double dir = _layout.Position switch { "left" => 1, "right" => -1, _ => -1 };
        var hop = new DoubleAnimationUsingKeyFrames();
        void Key(double v, int ms, EasingMode mode) =>
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(v * dir, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new QuadraticEase { EasingMode = mode }));
        Key(h, 190, EasingMode.EaseOut);
        Key(0, 380, EasingMode.EaseIn);
        Key(h * 0.5, 520, EasingMode.EaseOut);
        Key(0, 660, EasingMode.EaseIn);
        fx.Bounce.BeginAnimation(_layout.Position == "bottom" ? TranslateTransform.YProperty : TranslateTransform.XProperty, hop);
    }

    void Item_MiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ItemOf(sender) is { } item)
        {
            if (sender is Grid g) Bounce(g);
            _model.Launch(item);
        }
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
        launch.Click += (_, _) => { if (GridOf(item) is { } g) Bounce(g); _model.Launch(item); };
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
        menu.Placement = TooltipPlacement;
        menu.IsOpen = true;
        e.Handled = true;
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
