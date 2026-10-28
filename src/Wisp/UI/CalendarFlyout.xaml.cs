using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisp.Core;

namespace Wisp.UI;

/// <summary>
/// Drops down from the top bar's date: the month with today highlighted, and the time in the
/// user's chosen time zones (Pacific, Mountain, Central and Eastern to start with).
/// </summary>
public partial class CalendarFlyout : UserControl
{
    readonly WispController _app;
    DateTime _month;
    DateTime _shownDay;
    string _shownMinute = "";
    bool _editing;
    List<TimeZoneInfo> _addable = [];

    // Windows only names zones in full; these are the abbreviations people actually say.
    static readonly Dictionary<string, (string Name, string Std, string Dst)> Known = new()
    {
        ["Pacific Standard Time"] = ("Pacific", "PST", "PDT"),
        ["Mountain Standard Time"] = ("Mountain", "MST", "MDT"),
        ["US Mountain Standard Time"] = ("Arizona", "MST", "MST"),
        ["Central Standard Time"] = ("Central", "CST", "CDT"),
        ["Eastern Standard Time"] = ("Eastern", "EST", "EDT"),
        ["Alaskan Standard Time"] = ("Alaska", "AKST", "AKDT"),
        ["Hawaiian Standard Time"] = ("Hawaii", "HST", "HST"),
        ["Atlantic Standard Time"] = ("Atlantic", "AST", "ADT"),
        ["UTC"] = ("UTC", "UTC", "UTC"),
        ["GMT Standard Time"] = ("London", "GMT", "BST"),
        ["W. Europe Standard Time"] = ("Central Europe", "CET", "CEST"),
        ["Romance Standard Time"] = ("Paris", "CET", "CEST"),
        ["India Standard Time"] = ("India", "IST", "IST"),
        ["China Standard Time"] = ("China", "CST", "CST"),
        ["Tokyo Standard Time"] = ("Tokyo", "JST", "JST"),
        ["AUS Eastern Standard Time"] = ("Sydney", "AEST", "AEDT"),
    };

    public CalendarFlyout(WispController app)
    {
        _app = app;
        InitializeComponent();
        var fmt = CultureInfo.CurrentCulture.DateTimeFormat;
        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)fmt.FirstDayOfWeek + i) % 7);
            var t = new TextBlock { Text = fmt.GetShortestDayName(day), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
            WeekDays.Children.Add(t);
        }
    }

    /// <summary>Called as the flyout opens: back to this month, everything fresh.</summary>
    public void Open()
    {
        SetEditing(false);
        _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RenderMonth();
        _shownMinute = "";
        Tick();
    }

    /// <summary>Keeps the times current while the flyout is open; redraws once a minute.</summary>
    public void Tick()
    {
        var now = DateTime.Now;
        if (now.Date != _shownDay) RenderMonth();
        var minute = now.ToString("yyyyMMddHHmm");
        if (minute == _shownMinute) return;
        _shownMinute = minute;
        BigTime.Text = now.ToString("t");
        LongDate.Text = now.ToString("D");
        RenderClocks();
    }

    void RenderMonth()
    {
        _shownDay = DateTime.Today;
        MonthTitle.Text = _month.ToString("Y");
        Days.Children.Clear();
        int lead = ((int)_month.DayOfWeek - (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        var day = _month.AddDays(-lead);
        for (int i = 0; i < 42; i++, day = day.AddDays(1))
        {
            bool today = day == _shownDay, inMonth = day.Month == _month.Month;
            var text = new TextBlock
            {
                Text = day.Day.ToString(), FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, today ? "AccentText" : inMonth ? "Text" : "TextFaint");
            var cell = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(1), Child = text };
            if (today) cell.SetResourceReference(Border.BackgroundProperty, "Accent");
            Days.Children.Add(cell);
        }
    }

    void RenderClocks()
    {
        Clocks.Children.Clear();
        var utc = DateTime.UtcNow;
        var local = TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utc, local);

        foreach (var id in _app.Settings.Desktop.Clocks.ToList())
        {
            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); } catch { continue; }
            var there = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            bool isLocal = tz.Id == local.Id;
            var diff = tz.GetUtcOffset(utc) - local.GetUtcOffset(utc);

            var name = new TextBlock { Text = NameOf(tz), FontSize = 13, FontWeight = isLocal ? FontWeights.SemiBold : FontWeights.Normal };
            var detail = new TextBlock { Text = $"{Abbreviation(tz, utc)} · {(isLocal ? "Local" : Relative(diff))}", FontSize = 11.5 };
            detail.SetResourceReference(TextBlock.ForegroundProperty, isLocal ? "Accent" : "TextFaint");
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(name);
            left.Children.Add(detail);

            var time = new TextBlock
            {
                Text = there.ToString("t"), FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right,
            };
            time.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(time);
            int dayShift = (there.Date - localNow.Date).Days;
            if (dayShift != 0)
            {
                var when = new TextBlock { Text = dayShift > 0 ? "Tomorrow" : "Yesterday", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
                when.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
                right.Children.Add(when);
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            if (_editing)
            {
                var remove = new Button
                {
                    Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Remove",
                    Content = new TextBlock { Text = "", FontSize = 10 },
                };
                remove.SetResourceReference(StyleProperty, "Ghost");
                ((TextBlock)remove.Content).SetResourceReference(StyleProperty, "Icon");
                remove.Click += (_, _) => { _app.Settings.Desktop.Clocks.Remove(id); _app.Settings.Save(); Refresh(); };
                Grid.SetColumn(remove, 2);
                grid.Children.Add(remove);
            }

            var row = new Border { Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 1, 0, 1), Child = grid };
            row.SetResourceReference(Border.CornerRadiusProperty, "RadiusS");
            if (isLocal) row.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            Clocks.Children.Add(row);
        }

        if (Clocks.Children.Count == 0)
        {
            var empty = new TextBlock { Text = "No time zones yet. Click Edit to add some.", FontSize = 12, Margin = new Thickness(4, 2, 4, 2) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
            Clocks.Children.Add(empty);
        }
    }

    static string NameOf(TimeZoneInfo tz)
    {
        if (Known.TryGetValue(tz.Id, out var k)) return k.Name;
        // "(UTC-08:00) Pacific Time (US & Canada)" -> "Pacific Time (US & Canada)"
        var d = tz.DisplayName;
        int close = d.IndexOf(") ");
        return close > 0 && d.StartsWith("(UTC") ? d[(close + 2)..] : d;
    }

    static string Abbreviation(TimeZoneInfo tz, DateTime utc)
    {
        if (Known.TryGetValue(tz.Id, out var k)) return tz.IsDaylightSavingTime(utc) ? k.Dst : k.Std;
        var off = tz.GetUtcOffset(utc);
        return off == TimeSpan.Zero ? "UTC" : $"UTC{(off < TimeSpan.Zero ? "−" : "+")}{off.Duration():h\\:mm}".Replace(":00", "");
    }

    static string Relative(TimeSpan diff)
    {
        if (diff == TimeSpan.Zero) return "same time";
        var sign = diff > TimeSpan.Zero ? "+" : "−";
        var d = diff.Duration();
        return d.Minutes == 0 ? $"{sign}{(int)d.TotalHours}h" : $"{sign}{(int)d.TotalHours}h {d.Minutes}m";
    }

    void Refresh()
    {
        _shownMinute = "";
        Tick();
        if (_editing) FillAddable();
    }

    void SetEditing(bool on)
    {
        _editing = on;
        EditButton.Content = on ? "Done" : "Edit";
        AddZone.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) FillAddable();
    }

    void FillAddable()
    {
        var chosen = _app.Settings.Desktop.Clocks;
        _addable = TimeZoneInfo.GetSystemTimeZones().Where(z => !chosen.Contains(z.Id)).ToList();
        AddZone.SelectionChanged -= AddZone_Changed;
        AddZone.ItemsSource = new[] { "Add a time zone…" }.Concat(_addable.Select(z => z.DisplayName)).ToList();
        AddZone.SelectedIndex = 0;
        AddZone.SelectionChanged += AddZone_Changed;
    }

    void AddZone_Changed(object s, SelectionChangedEventArgs e)
    {
        if (AddZone.SelectedIndex <= 0) return;
        _app.Settings.Desktop.Clocks.Add(_addable[AddZone.SelectedIndex - 1].Id);
        _app.Settings.Save();
        Refresh();
    }

    void Edit_Click(object s, RoutedEventArgs e) { SetEditing(!_editing); Refresh(); }
    void Prev_Click(object s, RoutedEventArgs e) { _month = _month.AddMonths(-1); RenderMonth(); }
    void Next_Click(object s, RoutedEventArgs e) { _month = _month.AddMonths(1); RenderMonth(); }
    void Today_Click(object s, RoutedEventArgs e) { _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); RenderMonth(); }
}
