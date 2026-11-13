using System.Windows;
using System.Windows.Media;
using Wisp.Core;

namespace Wisp.UI;

/// <summary>
/// Draws the weather over the wallpaper: rain streaks that slant with the wind, drifting snow,
/// lightning flashes, rolling fog, and a darker sky for overcast or stormy weather. One
/// DrawingVisual redrawn per frame; nothing animates for clear or merely cloudy skies.
/// </summary>
public sealed class WeatherScene : FrameworkElement
{
    readonly DrawingVisual _visual = new();
    readonly Random _rng = new();
    Sky _sky = Sky.Clear;
    double _slant;            // horizontal drift per unit of fall, from the wind
    bool _running;
    TimeSpan _lastFrame;

    /// <summary>Frame rate cap; the live desktop runs at 30 to keep its cost down.</summary>
    public int MaxFps { get; set; } = 60;

    struct Drop { public double X, Y, Speed, Length; public int Layer; }
    struct Flake { public double X, Y, Speed, Radius, Phase, Sway; public int Layer; }
    struct Blob { public double X, Y, Radius, Speed; }
    Drop[] _drops = [];
    Flake[] _flakes = [];
    Blob[] _fog = [];
    double _flash, _nextFlash = 4;

    static readonly Pen[] RainPens = MakeRainPens();
    static readonly Brush[] SnowBrushes = [Frozen(0x55), Frozen(0x99), Frozen(0xDD)];

    static readonly Brush FogBrush = MakeFogBrush();

    public WeatherScene()
    {
        AddVisualChild(_visual);
        IsHitTestVisible = false;
        SizeChanged += (_, _) => Reseed();
        Unloaded += (_, _) => Stop();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    /// <summary>How dark the sky should make the wallpaper (0..1).</summary>
    public static double Gloom(Sky sky) => sky switch
    {
        Sky.PartlyCloudy => 0.06, Sky.Cloudy => 0.18, Sky.Fog => 0.10, Sky.Drizzle => 0.18,
        Sky.Rain => 0.26, Sky.HeavyRain => 0.34, Sky.Storm => 0.40, Sky.Snow or Sky.HeavySnow => 0.08,
        _ => 0,
    };

    public static bool Animates(Sky sky) => sky is not (Sky.Clear or Sky.PartlyCloudy or Sky.Cloudy);

    /// <param name="windSpeed">mph or km/h; only its rough size matters</param>
    /// <param name="windFrom">compass degrees the wind blows from</param>
    public void SetSky(Sky sky, double windSpeed = 0, double windFrom = 270)
    {
        _sky = sky;
        // Wind from the west (270) blows rain toward the east, i.e. to the right.
        double eastward = -Math.Sin(windFrom * Math.PI / 180) * windSpeed;
        _slant = Math.Clamp(eastward / 45, -0.35, 0.35);
        Reseed();
        Step(0);
        Render(); // draw once now, so a paused screen still shows the weather
        if (Animates(sky)) Start(); else Stop();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    void OnRendering(object? s, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return; // fires more than once per frame sometimes
        if (_lastFrame != TimeSpan.Zero && (now - _lastFrame).TotalSeconds < 0.9 / MaxFps) return;
        double dt = _lastFrame == TimeSpan.Zero ? 0.016 : Math.Min((now - _lastFrame).TotalSeconds, 0.05);
        _lastFrame = now;
        Step(dt);
        Render();
    }

    /// <summary>Advances the simulation (also used by snapshots to settle a frame offscreen).</summary>
    public void Step(double dt)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        for (int i = 0; i < _drops.Length; i++)
        {
            ref var d = ref _drops[i];
            d.Y += d.Speed * dt;
            d.X += d.Speed * _slant * dt;
            if (d.Y - d.Length > h || d.X < -60 || d.X > w + 60)
            {
                d.Y = -_rng.NextDouble() * 80;
                d.X = _rng.NextDouble() * (w + 120) - 60 - _slant * h * 0.5;
            }
        }

        double drift = _slant * 60;
        for (int i = 0; i < _flakes.Length; i++)
        {
            ref var f = ref _flakes[i];
            f.Phase += dt * 1.3;
            f.Y += f.Speed * dt;
            f.X += (drift + Math.Sin(f.Phase) * f.Sway) * dt;
            if (f.Y - f.Radius > h) { f.Y = -f.Radius - _rng.NextDouble() * 40; f.X = _rng.NextDouble() * w; }
            if (f.X < -10) f.X += w + 20; else if (f.X > w + 10) f.X -= w + 20;
        }

        for (int i = 0; i < _fog.Length; i++)
        {
            ref var b = ref _fog[i];
            b.X += b.Speed * dt;
            if (b.X - b.Radius > w) b.X = -b.Radius;
        }

        if (_sky == Sky.Storm)
        {
            _nextFlash -= dt;
            if (_nextFlash <= 0)
            {
                _flash = 0.55;
                _nextFlash = 5 + _rng.NextDouble() * 12;
            }
            // A quick double flicker, then fade.
            _flash = Math.Max(0, _flash - dt * (_flash > 0.3 ? 2.5 : 1.4));
            if (_flash is > 0.2 and < 0.24 && _rng.NextDouble() < 0.5) _flash = 0.45;
        }
        else _flash = 0;
    }

    public void Render()
    {
        double w = ActualWidth, h = ActualHeight;
        using var dc = _visual.RenderOpen();
        if (w <= 0 || h <= 0) return;

        foreach (var b in _fog)
        {
            dc.PushTransform(new TranslateTransform(b.X, b.Y));
            dc.DrawEllipse(FogBrush, null, new Point(0, 0), b.Radius * 1.6, b.Radius);
            dc.Pop();
        }

        foreach (var d in _drops)
            dc.DrawLine(RainPens[d.Layer], new Point(d.X, d.Y), new Point(d.X - d.Length * _slant, d.Y - d.Length));

        foreach (var f in _flakes)
            dc.DrawEllipse(SnowBrushes[f.Layer], null, new Point(f.X, f.Y), f.Radius, f.Radius);

        if (_flash > 0)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(_flash * 255), 0xE8, 0xEE, 0xFF)), null, new Rect(0, 0, w, h));
    }

    void Reseed()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double area = w * h / (2560.0 * 1440.0); // particle counts are tuned for a 1440p screen

        int drops = (int)(area * _sky switch { Sky.Drizzle => 160, Sky.Rain => 380, Sky.HeavyRain or Sky.Storm => 750, _ => 0 });
        double speed = _sky == Sky.Drizzle ? 260 : 420; // px/s: unhurried, easy on the eyes
        _drops = new Drop[drops];
        for (int i = 0; i < drops; i++)
        {
            int layer = _rng.Next(3);
            _drops[i] = new Drop
            {
                X = _rng.NextDouble() * w, Y = _rng.NextDouble() * h, Layer = layer,
                Speed = speed * (0.7 + layer * 0.2 + _rng.NextDouble() * 0.2),
                Length = (_sky == Sky.Drizzle ? 8 : 12) + layer * 6 + _rng.NextDouble() * 8,
            };
        }

        int flakes = (int)(area * _sky switch { Sky.Snow => 420, Sky.HeavySnow => 850, _ => 0 });
        _flakes = new Flake[flakes];
        for (int i = 0; i < flakes; i++)
        {
            int layer = _rng.Next(3);
            double r = 1.4 + layer * 1.1 + _rng.NextDouble() * 1.1;
            _flakes[i] = new Flake
            {
                X = _rng.NextDouble() * w, Y = _rng.NextDouble() * h, Layer = layer, Radius = r,
                Speed = 30 + r * 22 + _rng.NextDouble() * 20, Phase = _rng.NextDouble() * 6.28, Sway = 10 + _rng.NextDouble() * 25,
            };
        }

        int blobs = _sky == Sky.Fog ? 7 : 0;
        _fog = new Blob[blobs];
        for (int i = 0; i < blobs; i++)
            _fog[i] = new Blob
            {
                X = _rng.NextDouble() * w, Y = h * (0.25 + _rng.NextDouble() * 0.7),
                Radius = 280 + _rng.NextDouble() * 320, Speed = 8 + _rng.NextDouble() * 14,
            };
    }

    static Pen[] MakeRainPens()
    {
        var pens = new Pen[3];
        byte[] alpha = [0x38, 0x55, 0x78];
        for (int i = 0; i < 3; i++)
        {
            pens[i] = new Pen(new SolidColorBrush(Color.FromArgb(alpha[i], 0xD6, 0xE6, 0xFF)), 0.9 + i * 0.35)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pens[i].Freeze();
        }
        return pens;
    }

    static Brush Frozen(byte alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        b.Freeze();
        return b;
    }

    static Brush MakeFogBrush()
    {
        var b = new RadialGradientBrush(Color.FromArgb(0x22, 0xE6, 0xEA, 0xF0), Color.FromArgb(0, 0xE6, 0xEA, 0xF0));
        b.Freeze();
        return b;
    }
}
