using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisp.Core;

namespace Wisp.UI;

/// <summary>
/// Drops down from the top bar's temperature: conditions now, the next five days, and the live
/// desktop, with buttons to preview each effect whatever the weather.
/// </summary>
public partial class WeatherFlyout : UserControl
{
    readonly WispController _app;

    static readonly (string Label, Sky? Sky)[] Effects =
        [("Auto", null), ("Rain", Sky.Rain), ("Snow", Sky.Snow), ("Storm", Sky.Storm), ("Fog", Sky.Fog)];

    public WeatherFlyout(WispController app)
    {
        _app = app;
        InitializeComponent();
    }

    /// <summary>Refreshes everything from the current weather (called as it opens and on updates).</summary>
    public void Show()
    {
        var w = _app.Weather.Current;
        bool has = _app.Weather.HasLocation;
        EmptyPanel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        DataPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has) return;

        var unit = _app.Settings.Weather.Fahrenheit ? "mph" : "km/h";
        PlaceText.Text = _app.Weather.Place;
        ErrorText.Text = _app.Weather.Error ?? "";
        ErrorText.Visibility = _app.Weather.Error is null ? Visibility.Collapsed : Visibility.Visible;
        if (w is null)
        {
            BigGlyph.Text = "";
            BigTemp.Text = "–";
            Summary.Text = "Loading…";
            Details.Text = "";
        }
        else
        {
            BigGlyph.Text = Weather.Glyph(w.Sky, w.IsDay);
            BigTemp.Text = $"{Math.Round(w.Temperature)}°";
            Summary.Text = Weather.Describe(w.Code);
            Details.Text = $"Feels like {Math.Round(w.FeelsLike)}° · Humidity {w.Humidity}% · Wind {Math.Round(w.Wind)} {unit}";
            Updated.Text = $"Updated {w.Fetched:t}";
            RenderForecast(w);
        }
        RenderLive();
    }

    void RenderForecast(WeatherNow w)
    {
        Forecast.Children.Clear();
        foreach (var d in w.Days.Take(5))
        {
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(Text(d.Date.Date == DateTime.Today ? "Today" : d.Date.ToString("ddd"), 11.5, "TextDim"));
            var glyph = Text(Weather.Glyph(Weather.SkyOf(d.Code)), 20, "Text");
            glyph.FontFamily = (FontFamily)Resources["Symbols"];
            glyph.Margin = new Thickness(0, 4, 0, 4);
            glyph.ToolTip = Weather.Describe(d.Code);
            col.Children.Add(glyph);
            var hi = Text($"{Math.Round(d.High)}°", 13, "Text");
            hi.FontWeight = FontWeights.SemiBold;
            col.Children.Add(hi);
            col.Children.Add(Text($"{Math.Round(d.Low)}°", 12, "TextFaint"));
            if (d.PrecipChance >= 20) col.Children.Add(Text($"{d.PrecipChance}%", 11, "Accent"));
            Forecast.Children.Add(col);
        }
    }

    void RenderLive()
    {
        var preview = _app.Live.Preview;
        var showing = _app.Live.Showing;
        LiveStatus.Text = preview is { } p ? $"Previewing {Name(p)}. Choose Auto to follow the weather again."
            : !_app.Settings.Weather.LiveWallpaper ? "Off. Turn it on in Settings → Weather."
            : showing is { } s ? $"Showing {Name(s)} on your desktop, like outside."
            : "Nothing to show right now: the desktop changes when it rains, snows, storms or gets foggy.";

        Previews.Children.Clear();
        foreach (var (label, sky) in Effects)
        {
            bool on = preview == sky;
            var b = new Button
            {
                Content = label, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6),
                ToolTip = sky is null ? "Show whatever the weather is doing outside" : $"Preview {label.ToLowerInvariant()} on your desktop",
            };
            b.SetResourceReference(StyleProperty, on ? "AccentButton" : "Tile");
            b.Click += (_, _) => { _app.Live.Preview = sky; RenderLive(); };
            Previews.Children.Add(b);
        }
    }

    static string Name(Sky sky) => sky switch
    {
        Sky.HeavyRain => "heavy rain", Sky.HeavySnow => "heavy snow", Sky.PartlyCloudy => "clouds",
        _ => sky.ToString().ToLowerInvariant(),
    };

    static TextBlock Text(string text, double size, string brush)
    {
        var t = new TextBlock { Text = text, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    void Location_Click(object s, RoutedEventArgs e) => _app.ShowWeatherSettings();
    async void Refresh_Click(object s, RoutedEventArgs e) { await _app.Weather.RefreshAsync(); Show(); }
}
