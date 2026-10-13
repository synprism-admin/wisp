namespace Wisp.Core;

/// <summary>Fires due routines. The action itself is supplied by the app (it runs the agent).</summary>
public sealed class Scheduler(Store store, Func<Routine, Task> run) : IDisposable
{
    Timer? _timer;
    int _ticking;

    public void Start() => _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));

    async void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var now = DateTime.Now;
            foreach (var r in store.Routines().Where(r => r.Enabled && r.NextRun <= now))
            {
                // Missed by more than an hour (PC was asleep/off): skip this run rather than surprise the user.
                bool stale = now - r.NextRun > TimeSpan.FromHours(1);
                store.UpdateRoutineRun(r.Id, NextAfter(r, now), now);
                if (stale) { Log.Info($"Skipped stale routine '{r.Title}'"); continue; }
                Log.Info($"Running routine '{r.Title}'");
                try { await run(r); } catch (Exception ex) { Log.Error($"Routine '{r.Title}' failed", ex); }
            }
        }
        catch (Exception ex) { Log.Error("Scheduler tick failed", ex); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>Next occurrence strictly after 'after', or null for one-shot routines.</summary>
    public static DateTime? NextAfter(Routine r, DateTime after)
    {
        var t = r.NextRun;
        switch (r.Kind)
        {
            case RoutineKind.Once: return null;
            case RoutineKind.Daily:
                while (t <= after) t = t.AddDays(1);
                return t;
            case RoutineKind.Weekdays:
                do t = t.AddDays(1); while (t <= after || t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
                return t;
            case RoutineKind.Weekly:
                while (t <= after) t = t.AddDays(7);
                return t;
        }
        return null;
    }

    /// <summary>First run time for a new routine given a time of day (and weekday for weekly).</summary>
    public static DateTime FirstRun(RoutineKind kind, DateTime at, DayOfWeek? weekday)
    {
        var now = DateTime.Now;
        var t = at;
        if (kind == RoutineKind.Once) return t;
        t = now.Date + at.TimeOfDay;
        if (kind == RoutineKind.Weekly && weekday is { } wd)
        {
            while (t.DayOfWeek != wd || t <= now) t = t.AddDays(1);
            return t;
        }
        while (t <= now || (kind == RoutineKind.Weekdays && t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday))
            t = t.AddDays(1);
        return t;
    }

    public void Dispose() => _timer?.Dispose();
}
