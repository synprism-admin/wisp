using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Wisp.Core;

/// <summary>What the sky is doing, as far as the desktop effects care.</summary>
public enum Sky { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, HeavyRain, Snow, HeavySnow, Storm }

public sealed record DayForecast(DateTime Date, int Code, double High, double Low, int PrecipChance);

public sealed record WeatherNow(
    double Temperature, double FeelsLike, int Humidity, int Code, bool IsDay, double Wind, double WindFrom,
    IReadOnlyList<DayForecast> Days, DateTime Fetched)
{
    public Sky Sky => Weather.SkyOf(Code);
}

/// <summary>
/// Local weather for the top bar and the live desktop. A US zip code is looked up with Zippopotam
/// (api.zippopotam.us); anything else (a city name) with Open-Meteo's geocoder. The forecast comes
/// from Open-Meteo (api.open-meteo.com). Both are free and keyless; only the place you typed and its
/// coordinates are sent. Refreshes every 15 minutes.
/// </summary>
public sealed class Weather
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly Regex UsZip = new(@"^\d{5}(-\d{4})?$");

    readonly Settings _settings;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(15) };

    public WeatherNow? Current { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    WeatherSettings W => _settings.Weather;
    public bool HasLocation => W.Latitude is not null && W.Longitude is not null;
    public string? Place => W.Place;

    public Weather(Settings settings)
    {
        _settings = settings;
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Start()
    {
        _timer.Start();
        if (HasLocation) _ = RefreshAsync();
    }

    /// <summary>
    /// Looks up a zip code or city, remembers it and fetches its weather; an empty string clears the
    /// location (and with it all weather requests). Returns an error message, or null.
    /// </summary>
    public async Task<string?> SetLocationAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            (W.Location, W.Place, W.Latitude, W.Longitude) = (null, null, null, null);
            _settings.Save();
            Current = null;
            Error = null;
            Changed?.Invoke();
            return null;
        }
        try
        {
            var found = UsZip.IsMatch(text) ? await LookupZipAsync(text[..5]) : await LookupCityAsync(text);
            if (found is null) return $"Couldn't find “{text}”.";
            (W.Place, W.Latitude, W.Longitude) = found.Value;
            W.Location = text;
            _settings.Save();
            Log.Info($"Weather location: {W.Place}");
            await RefreshAsync();
            return Error;
        }
        catch (Exception ex)
        {
            Log.Error("Weather location lookup failed", ex);
            return "Couldn't reach the location service. Check your connection.";
        }
    }

    static async Task<(string, double, double)?> LookupZipAsync(string zip)
    {
        using var res = await Http.GetAsync($"https://api.zippopotam.us/us/{zip}");
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var place = doc.RootElement.GetProperty("places")[0];
        return ($"{place.GetProperty("place name").GetString()}, {place.GetProperty("state abbreviation").GetString()}",
            double.Parse(place.GetProperty("latitude").GetString()!, CultureInfo.InvariantCulture),
            double.Parse(place.GetProperty("longitude").GetString()!, CultureInfo.InvariantCulture));
    }

    static async Task<(string, double, double)?> LookupCityAsync(string city)
    {
        var json = await Http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(city)}");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        var region = r.TryGetProperty("admin1", out var a) ? a.GetString() : r.TryGetProperty("country", out var c) ? c.GetString() : null;
        var name = r.GetProperty("name").GetString();
        return (region is null ? name! : $"{name}, {region}", r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble());
    }

    public async Task RefreshAsync()
    {
        if (!HasLocation) return;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var url = "https://api.open-meteo.com/v1/forecast"
                + $"?latitude={W.Latitude!.Value.ToString(inv)}&longitude={W.Longitude!.Value.ToString(inv)}"
                + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day,wind_speed_10m,wind_direction_10m"
                + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max"
                + $"&temperature_unit={(W.Fahrenheit ? "fahrenheit" : "celsius")}&wind_speed_unit={(W.Fahrenheit ? "mph" : "kmh")}"
                + "&timezone=auto&forecast_days=6";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            var days = new List<DayForecast>();
            for (int i = 0; i < daily.GetProperty("time").GetArrayLength(); i++)
                days.Add(new DayForecast(
                    DateTime.Parse(daily.GetProperty("time")[i].GetString()!, inv),
                    daily.GetProperty("weather_code")[i].GetInt32(),
                    daily.GetProperty("temperature_2m_max")[i].GetDouble(),
                    daily.GetProperty("temperature_2m_min")[i].GetDouble(),
                    daily.GetProperty("precipitation_probability_max")[i].ValueKind == JsonValueKind.Number
                        ? daily.GetProperty("precipitation_probability_max")[i].GetInt32() : 0));
            Current = new WeatherNow(
                cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("apparent_temperature").GetDouble(),
                cur.GetProperty("relative_humidity_2m").GetInt32(), cur.GetProperty("weather_code").GetInt32(),
                cur.GetProperty("is_day").GetInt32() == 1, cur.GetProperty("wind_speed_10m").GetDouble(),
                cur.GetProperty("wind_direction_10m").GetDouble(), days, DateTime.Now);
            Error = null;
        }
        catch (Exception ex)
        {
            Log.Error("Weather refresh failed", ex);
            Error = "Couldn't get the weather right now.";
        }
        Changed?.Invoke();
    }

    /// <summary>For snapshots: plausible rainy weather without touching settings or the network.</summary>
    public void UseSample()
    {
        W.Place ??= "Maryville, TN";
        W.Latitude ??= 35.75;
        W.Longitude ??= -83.97;
        var today = DateTime.Today;
        Current = new WeatherNow(47, 42, 90, 63, true, 10, 250,
        [
            new(today, 63, 52, 44, 90), new(today.AddDays(1), 3, 49, 37, 30), new(today.AddDays(2), 2, 51, 33, 5),
            new(today.AddDays(3), 71, 38, 29, 70), new(today.AddDays(4), 0, 46, 30, 0),
        ], DateTime.Now);
        Changed?.Invoke();
    }

    /// <summary>WMO weather codes (as Open-Meteo reports them) to what the desktop shows.</summary>
    public static Sky SkyOf(int code) => code switch
    {
        0 => Sky.Clear,
        1 or 2 => Sky.PartlyCloudy,
        3 => Sky.Cloudy,
        45 or 48 => Sky.Fog,
        51 or 53 or 55 or 56 or 57 => Sky.Drizzle,
        61 or 63 or 66 or 80 or 81 => Sky.Rain,
        65 or 67 or 82 => Sky.HeavyRain,
        71 or 73 or 77 or 85 => Sky.Snow,
        75 or 86 => Sky.HeavySnow,
        95 or 96 or 99 => Sky.Storm,
        _ => Sky.Cloudy,
    };

    public static string Describe(int code) => code switch
    {
        0 => "Clear", 1 => "Mostly clear", 2 => "Partly cloudy", 3 => "Overcast",
        45 or 48 => "Fog", 51 or 53 or 55 => "Drizzle", 56 or 57 => "Freezing drizzle",
        61 => "Light rain", 63 => "Rain", 65 => "Heavy rain", 66 or 67 => "Freezing rain",
        71 => "Light snow", 73 => "Snow", 75 => "Heavy snow", 77 => "Snow grains",
        80 => "Rain showers", 81 => "Rain showers", 82 => "Heavy showers", 85 or 86 => "Snow showers",
        95 => "Thunderstorm", 96 or 99 => "Thunderstorm with hail", _ => "Cloudy",
    };

    /// <summary>A symbol for the sky (Segoe UI Symbol renders these in one color, matching the bar).</summary>
    public static string Glyph(Sky sky, bool day = true) => sky switch
    {
        Sky.Clear => day ? "☀" : "☾",
        Sky.PartlyCloudy => day ? "⛅" : "☁",
        Sky.Cloudy => "☁",
        Sky.Fog => "▒",
        Sky.Drizzle or Sky.Rain or Sky.HeavyRain => "☔",
        Sky.Snow or Sky.HeavySnow => "❄",
        Sky.Storm => "⛈",
        _ => "☁",
    };
}
