using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Wisp.Native;
using Drawing = System.Drawing;

namespace Wisp.UI;

/// <summary>
/// Full-screen, lightly tinted overlay (one window per monitor) where the user circles
/// something with the mouse while holding Ctrl+Shift. Tracks the lasso in physical pixels.
/// </summary>
public sealed class CircleOverlay
{
    readonly List<OverlayWindow> _windows = [];
    readonly List<Drawing.Point> _points = [];

    public event Action? Cancelled;

    public IReadOnlyList<Drawing.Point> Points => _points;

    public Drawing.Rectangle? Bounds
    {
        get
        {
            if (_points.Count < 3) return null;
            int l = _points.Min(p => p.X), t = _points.Min(p => p.Y), r = _points.Max(p => p.X), b = _points.Max(p => p.Y);
            if (r - l < 12 || b - t < 12) return null;
            return Drawing.Rectangle.FromLTRB(l, t, r, b);
        }
    }

    public void Show()
    {
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var w = new OverlayWindow(this, screen.Bounds);
            _windows.Add(w);
            w.Show();
        }
        // Grab focus so Escape and mouse capture work; the hotkey hook still sees key releases.
        _windows.FirstOrDefault(w => w.Bounds.Contains(System.Windows.Forms.Cursor.Position))?.Activate();
    }

    public void Close()
    {
        foreach (var w in _windows) w.Close();
        _windows.Clear();
    }

    internal void Begin() { _points.Clear(); foreach (var w in _windows) w.ClearStroke(); }
    internal void Add(Drawing.Point p) { _points.Add(p); foreach (var w in _windows) w.Redraw(_points); }
    internal void Cancel() => Cancelled?.Invoke();

    sealed class OverlayWindow : Window
    {
        readonly CircleOverlay _owner;
        public readonly Drawing.Rectangle Bounds;
        readonly Polyline _line;
        readonly Canvas _canvas = new();
        bool _drawing;

        public OverlayWindow(CircleOverlay owner, Drawing.Rectangle bounds)
        {
            _owner = owner;
            Bounds = bounds;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Cursor = Cursors.Cross;
            // Must not be fully transparent, or clicks fall through to the windows underneath.
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x08, 0x06));

            var accent = (Color)Application.Current.FindResource("AccentColor");
            _line = new Polyline
            {
                Stroke = new SolidColorBrush(accent),
                StrokeThickness = 4,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Effect = new DropShadowEffect { Color = accent, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.9 },
            };
            _canvas.Children.Add(_line);

            var hint = new Border
            {
                Background = (Brush)Application.Current.FindResource("Surface"),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(16, 9, 16, 9),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 56, 0, 0),
                Child = new TextBlock
                {
                    Text = "Circle anything, ask out loud, then let go  ·  Esc to cancel",
                    FontSize = 13,
                    Foreground = (Brush)Application.Current.FindResource("Text"),
                },
            };
            var root = new Grid();
            root.Children.Add(_canvas);
            root.Children.Add(hint);
            Content = root;

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Win32.SetExStyle(hwnd, Win32.WS_EX_TOOLWINDOW);
                Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, Win32.SWP_SHOWWINDOW);
            };

            MouseLeftButtonDown += (_, e) => { _drawing = true; CaptureMouse(); _owner.Begin(); Add(e); };
            MouseMove += (_, e) => { if (_drawing && e.LeftButton == MouseButtonState.Pressed) Add(e); };
            MouseLeftButtonUp += (_, _) => { _drawing = false; ReleaseMouseCapture(); };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) _owner.Cancel(); };
        }

        void Add(MouseEventArgs e)
        {
            var p = PointToScreen(e.GetPosition(this)); // physical pixels
            _owner.Add(new Drawing.Point((int)p.X, (int)p.Y));
        }

        public void ClearStroke() => _line.Points.Clear();

        public void Redraw(IReadOnlyList<Drawing.Point> pts)
        {
            if (PresentationSource.FromVisual(this) is null) return;
            _line.Points.Clear();
            foreach (var p in pts)
            {
                if (!Bounds.Contains(p) && pts.Count > 0 && !Bounds.IntersectsWith(new Drawing.Rectangle(p.X - 50, p.Y - 50, 100, 100))) continue;
                _line.Points.Add(PointFromScreen(new Point(p.X, p.Y)));
            }
        }
    }
}
