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
/// It's an app bar along the bottom of the primary monitor, so maximized windows stop above it.
/// </summary>
public partial class DockWindow : Window
{
    const double StripHeight = 82; // DIPs reserved at the bottom of the screen

    readonly WispController _app;
    readonly DockModel _model;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(700) };
    IntPtr _hwnd;
    AppBar? _bar;

    public DockWindow(WispController app, DockModel model)
    {
        _app = app;
        _model = model;
        InitializeComponent();
        Items.ItemsSource = model.Items;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            WindowTracker.IgnoreWindow(_hwnd);
            _bar = new AppBar(this, AppBar.Edge.Bottom, StripHeight);
        };
        Closed += (_, _) => { _bar?.Dispose(); _poll.Stop(); };
        _poll.Tick += (_, _) => { try { _model.Update(); } catch (Exception ex) { Log.Error("Dock update failed", ex); } };
        _poll.Start();
    }

    public void Reload() => _model.Rebuild();

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
        if (g.Children[2] is Grid icon && icon.RenderTransform is ScaleTransform st)
        {
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
