using Microsoft.Data.Sqlite;

namespace Wisp.Core;

public sealed record TodoItem(long Id, string Text, DateTime? Due, bool Done, DateTime Created);

public enum RoutineKind { Once, Daily, Weekdays, Weekly }

/// <summary>
/// A scheduled instruction. When it fires, <see cref="Instruction"/> is handed to the agent
/// verbatim ("open Unity and Spotify"), so routines can do anything the agent can.
/// </summary>
public sealed record Routine(
    long Id, string Title, string Instruction, RoutineKind Kind,
    DateTime NextRun, DayOfWeek? Weekday, bool Enabled, DateTime? LastRun);

public sealed record ClipEntry(long Id, string Text, DateTime Copied, bool Pinned);

public sealed record UsageRow(string Provider, string Model, long Calls, long Input, long Output, double? Cost);

public sealed class Store
{
    readonly string _cs = new SqliteConnectionStringBuilder { DataSource = Paths.Database }.ToString();

    public event Action? TodosChanged;
    public event Action? RoutinesChanged;
    public event Action? ClipboardChanged;

    public Store()
    {
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS todos(
              id INTEGER PRIMARY KEY, text TEXT NOT NULL, due TEXT, done INTEGER NOT NULL DEFAULT 0,
              created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS routines(
              id INTEGER PRIMARY KEY, title TEXT NOT NULL, instruction TEXT NOT NULL, kind TEXT NOT NULL,
              next_run TEXT NOT NULL, weekday INTEGER, enabled INTEGER NOT NULL DEFAULT 1, last_run TEXT);
            CREATE TABLE IF NOT EXISTS clips(
              id INTEGER PRIMARY KEY, text TEXT NOT NULL, copied TEXT NOT NULL, pinned INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS clips_copied ON clips(copied);
            CREATE TABLE IF NOT EXISTS usage(
              month TEXT NOT NULL, provider TEXT NOT NULL, model TEXT NOT NULL,
              calls INTEGER NOT NULL DEFAULT 0, input INTEGER NOT NULL DEFAULT 0, output INTEGER NOT NULL DEFAULT 0,
              cost REAL, PRIMARY KEY(month, provider, model));
            """);
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    static string Iso(DateTime d) => d.ToString("o");
    static DateTime? ParseDate(object v) => v is string s ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind) : null;

    // ---- todos ----

    public IReadOnlyList<TodoItem> Todos(bool includeDone = true)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,text,due,done,created FROM todos " +
                          (includeDone ? "" : "WHERE done=0 ") +
                          "ORDER BY done, COALESCE(due,'9999'), id";
        using var r = cmd.ExecuteReader();
        var list = new List<TodoItem>();
        while (r.Read())
            list.Add(new TodoItem(r.GetInt64(0), r.GetString(1), ParseDate(r.GetValue(2)), r.GetInt64(3) == 1,
                ParseDate(r.GetValue(4)) ?? DateTime.Now));
        return list;
    }

    public long AddTodo(string text, DateTime? due)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO todos(text,due,created) VALUES($t,$d,$c); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", text);
        cmd.Parameters.AddWithValue("$d", due is null ? DBNull.Value : Iso(due.Value));
        cmd.Parameters.AddWithValue("$c", Iso(DateTime.Now));
        var id = (long)cmd.ExecuteScalar()!;
        TodosChanged?.Invoke();
        return id;
    }

    public void SetTodoDone(long id, bool done)
    {
        using var c = Open();
        Exec(c, "UPDATE todos SET done=$d WHERE id=$i", ("$d", done ? 1 : 0), ("$i", id));
        TodosChanged?.Invoke();
    }

    public void DeleteTodo(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM todos WHERE id=$i", ("$i", id));
        TodosChanged?.Invoke();
    }

    // ---- routines ----

    public IReadOnlyList<Routine> Routines()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,title,instruction,kind,next_run,weekday,enabled,last_run FROM routines ORDER BY next_run";
        using var r = cmd.ExecuteReader();
        var list = new List<Routine>();
        while (r.Read())
            list.Add(new Routine(r.GetInt64(0), r.GetString(1), r.GetString(2),
                Enum.Parse<RoutineKind>(r.GetString(3)), ParseDate(r.GetValue(4))!.Value,
                r.IsDBNull(5) ? null : (DayOfWeek)r.GetInt64(5), r.GetInt64(6) == 1, ParseDate(r.GetValue(7))));
        return list;
    }

    public long AddRoutine(string title, string instruction, RoutineKind kind, DateTime nextRun, DayOfWeek? weekday)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO routines(title,instruction,kind,next_run,weekday) VALUES($t,$i,$k,$n,$w); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$i", instruction);
        cmd.Parameters.AddWithValue("$k", kind.ToString());
        cmd.Parameters.AddWithValue("$n", Iso(nextRun));
        cmd.Parameters.AddWithValue("$w", weekday is null ? DBNull.Value : (int)weekday.Value);
        var id = (long)cmd.ExecuteScalar()!;
        RoutinesChanged?.Invoke();
        return id;
    }

    public void UpdateRoutineRun(long id, DateTime? nextRun, DateTime lastRun)
    {
        using var c = Open();
        if (nextRun is null)
            Exec(c, "UPDATE routines SET enabled=0,last_run=$l WHERE id=$i", ("$l", Iso(lastRun)), ("$i", id));
        else
            Exec(c, "UPDATE routines SET next_run=$n,last_run=$l WHERE id=$i",
                ("$n", Iso(nextRun.Value)), ("$l", Iso(lastRun)), ("$i", id));
        RoutinesChanged?.Invoke();
    }

    public void SetRoutineEnabled(long id, bool enabled)
    {
        using var c = Open();
        Exec(c, "UPDATE routines SET enabled=$e WHERE id=$i", ("$e", enabled ? 1 : 0), ("$i", id));
        RoutinesChanged?.Invoke();
    }

    public void DeleteRoutine(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM routines WHERE id=$i", ("$i", id));
        RoutinesChanged?.Invoke();
    }

    // ---- AI usage (per month, provider and model) ----

    static string Month(DateTime d) => d.ToString("yyyy-MM");

    public void RecordUsage(string provider, string model, Wisp.AI.TokenUsage u, double? cost)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO usage(month, provider, model, calls, input, output, cost) VALUES($m,$p,$o,1,$i,$x,$c)
            ON CONFLICT(month, provider, model) DO UPDATE SET
              calls = calls + 1, input = input + $i, output = output + $x,
              cost = CASE WHEN $c IS NULL THEN cost ELSE COALESCE(cost, 0) + $c END
            """,
            ("$m", Month(DateTime.Now)), ("$p", provider), ("$o", model),
            ("$i", u.Input + u.CacheRead + u.CacheWrite), ("$x", u.Output), ("$c", cost));
    }

    public IReadOnlyList<UsageRow> UsageThisMonth()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT provider, model, calls, input, output, cost FROM usage WHERE month=$m ORDER BY provider, model";
        cmd.Parameters.AddWithValue("$m", Month(DateTime.Now));
        using var r = cmd.ExecuteReader();
        var list = new List<UsageRow>();
        while (r.Read())
            list.Add(new UsageRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.IsDBNull(5) ? null : r.GetDouble(5)));
        return list;
    }

    // ---- clipboard history ----

    public IReadOnlyList<ClipEntry> Clips(int limit = 200)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,text,copied,pinned FROM clips ORDER BY pinned DESC, copied DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<ClipEntry>();
        while (r.Read())
            list.Add(new ClipEntry(r.GetInt64(0), r.GetString(1), ParseDate(r.GetValue(2))!.Value, r.GetInt64(3) == 1));
        return list;
    }

    public void AddClip(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return;
        using var c = Open();
        // Re-copying something moves it to the top instead of duplicating it.
        Exec(c, "DELETE FROM clips WHERE text=$t AND pinned=0", ("$t", text));
        Exec(c, "INSERT INTO clips(text,copied) VALUES($t,$c)", ("$t", text), ("$c", Iso(DateTime.Now)));
        Exec(c, "DELETE FROM clips WHERE pinned=0 AND id NOT IN (SELECT id FROM clips WHERE pinned=0 ORDER BY copied DESC LIMIT 500)");
        ClipboardChanged?.Invoke();
    }

    public void SetClipPinned(long id, bool pinned)
    {
        using var c = Open();
        Exec(c, "UPDATE clips SET pinned=$p WHERE id=$i", ("$p", pinned ? 1 : 0), ("$i", id));
        ClipboardChanged?.Invoke();
    }

    public void DeleteClip(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM clips WHERE id=$i", ("$i", id));
        ClipboardChanged?.Invoke();
    }
}
