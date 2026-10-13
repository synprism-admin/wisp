using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Wisp.AI;
using Wisp.Core;
using Wisp.Native;

namespace Wisp.Tools;

public static class SystemTools
{
    public static IEnumerable<ITool> All(AppIndex apps, Media media)
    {
        // ---------------- apps & web

        yield return FnTool.Sync("open_app", "Launch an installed app by name (from the Start menu).",
            Schema.Object(("name", "string", "App name as the user said it, e.g. 'Spotify', 'VS Code', 'calculator'.", true)),
            (a, _) =>
            {
                var name = a.Str("name") ?? "";
                var app = apps.Find(name);
                if (app is null)
                {
                    var near = string.Join(", ", apps.Search(name.Split(' ')[0]).Take(8).Select(x => x.Name));
                    return ToolOutput.Fail($"No app called '{name}'." + (near.Length > 0 ? $" Close matches: {near}" : ""));
                }
                AppIndex.Launch(app);
                return ToolOutput.Ok($"Opened {app.Name}.");
            });

        yield return FnTool.Sync("list_apps", "List installed apps matching a filter.",
            Schema.Object(("filter", "string", "Optional text to filter by.", false)),
            (a, _) => ToolOutput.Ok(string.Join("\n", apps.Search(a.Str("filter") ?? "").Take(60).Select(x => x.Name))));

        yield return FnTool.Sync("open_url", "Open a URL in the default browser.",
            Schema.Object(("url", "string", "Full URL.", true)),
            (a, _) =>
            {
                var url = a.Str("url") ?? "";
                if (!url.Contains("://")) url = "https://" + url;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                    return ToolOutput.Fail("Only http(s) links can be opened.");
                Shell(u.ToString());
                return ToolOutput.Ok($"Opened {u.Host}.");
            });

        yield return FnTool.Sync("web_search",
            "Search the web or a specific site in the browser. Sites: google, youtube, amazon, maps, wikipedia, reddit, github, images, news.",
            Schema.Object(("query", "string", "What to search for.", true),
                ("site", "string", "Optional site to search on (default google).", false)),
            (a, _) =>
            {
                var q = Uri.EscapeDataString(a.Str("query") ?? "");
                var site = (a.Str("site") ?? "google").ToLowerInvariant();
                var url = site switch
                {
                    "youtube" => $"https://www.youtube.com/results?search_query={q}",
                    "amazon" => $"https://www.amazon.com/s?k={q}",
                    "maps" => $"https://www.google.com/maps/search/{q}",
                    "wikipedia" => $"https://en.wikipedia.org/w/index.php?search={q}",
                    "reddit" => $"https://www.reddit.com/search/?q={q}",
                    "github" => $"https://github.com/search?q={q}",
                    "images" => $"https://www.google.com/search?tbm=isch&q={q}",
                    "news" => $"https://news.google.com/search?q={q}",
                    _ => $"https://www.google.com/search?q={q}",
                };
                Shell(url);
                return ToolOutput.Ok($"Searching {site} for \"{a.Str("query")}\".");
            });

        yield return FnTool.Sync("open_path", "Open a file or folder (e.g. 'Downloads', 'C:\\Projects') in its default app / Explorer.",
            Schema.Object(("path", "string", "Path, or a known folder name: Desktop, Documents, Downloads, Pictures, Music, Videos.", true)),
            (a, _) =>
            {
                var p = a.Str("path") ?? "";
                var known = p.ToLowerInvariant() switch
                {
                    "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "downloads" => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                    "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                    "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    _ => Environment.ExpandEnvironmentVariables(p),
                };
                if (!System.IO.File.Exists(known) && !System.IO.Directory.Exists(known)) return ToolOutput.Fail($"Not found: {known}");
                Shell(known);
                return ToolOutput.Ok($"Opened {known}.");
            });

        // ---------------- to-dos

        yield return FnTool.Sync("add_todo", "Add an item to the user's to-do list.",
            Schema.Object(("text", "string", "The to-do.", true),
                ("due", "string", "Optional local due date/time, ISO 8601 (e.g. 2026-10-14T18:00).", false)),
            (a, c) =>
            {
                DateTime? due = DateTime.TryParse(a.Str("due"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
                c.Store.AddTodo(a.Str("text") ?? "", due);
                return ToolOutput.Ok("Added.");
            });

        yield return FnTool.Sync("list_todos", "List the user's to-dos.",
            Schema.Object(("include_done", "boolean", "Include completed items.", false)),
            (a, c) =>
            {
                var items = c.Store.Todos(a.Bool("include_done") ?? false);
                return ToolOutput.Ok(items.Count == 0 ? "No to-dos." : string.Join("\n",
                    items.Select(t => $"#{t.Id} {(t.Done ? "[x]" : "[ ]")} {t.Text}{(t.Due is { } d ? $" (due {d:ddd MMM d h:mm tt})" : "")}")));
            });

        yield return FnTool.Sync("complete_todo", "Mark a to-do done (or delete it).",
            Schema.Object(("id", "integer", "To-do id from list_todos.", true), ("delete", "boolean", "Delete instead of completing.", false)),
            (a, c) =>
            {
                var id = a.Int("id") ?? 0;
                if (a.Bool("delete") == true) c.Store.DeleteTodo(id); else c.Store.SetTodoDone(id, true);
                return ToolOutput.Ok("Done.");
            });

        // ---------------- scheduling

        yield return FnTool.Sync("schedule_task",
            "Schedule something for later or on a repeat. 'instruction' is what you'll be told to do at that time, " +
            "phrased as a command (e.g. 'Open Unity and Spotify', 'Search Amazon for gift wrap', 'Remind me to call Mom').",
            Schema.Object(
                ("title", "string", "Short label, e.g. 'Morning apps'.", true),
                ("instruction", "string", "What to do when it fires.", true),
                ("repeat", "string", "once | daily | weekdays | weekly", true),
                ("at", "string", "Local ISO 8601 date-time of the (first) run, e.g. 2026-11-10T11:00. For repeating tasks only the time of day (and weekday for weekly) matters.", true)),
            (a, c) =>
            {
                if (!DateTime.TryParse(a.Str("at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at))
                    return ToolOutput.Fail("Couldn't read 'at'. Use ISO 8601 like 2026-11-10T11:00.");
                var kind = (a.Str("repeat") ?? "once").ToLowerInvariant() switch
                {
                    "daily" => RoutineKind.Daily, "weekdays" => RoutineKind.Weekdays, "weekly" => RoutineKind.Weekly, _ => RoutineKind.Once,
                };
                if (kind == RoutineKind.Once && at <= DateTime.Now) return ToolOutput.Fail("That time has already passed.");
                var first = Scheduler.FirstRun(kind, at, kind == RoutineKind.Weekly ? at.DayOfWeek : null);
                c.Store.AddRoutine(a.Str("title") ?? "Task", a.Str("instruction") ?? "", kind, first, kind == RoutineKind.Weekly ? at.DayOfWeek : null);
                return ToolOutput.Ok($"Scheduled {kind.ToString().ToLowerInvariant()}, next run {first:dddd MMM d 'at' h:mm tt}.");
            });

        yield return FnTool.Sync("list_scheduled", "List scheduled tasks and routines.", Schema.Empty(),
            (_, c) =>
            {
                var rs = c.Store.Routines();
                return ToolOutput.Ok(rs.Count == 0 ? "Nothing scheduled." : string.Join("\n", rs.Select(r =>
                    $"#{r.Id} {r.Title} — {r.Kind}, next {r.NextRun:ddd MMM d h:mm tt}{(r.Enabled ? "" : " (off)")}: {r.Instruction}")));
            });

        yield return FnTool.Sync("cancel_scheduled", "Delete a scheduled task by id.",
            Schema.Object(("id", "integer", "Id from list_scheduled.", true)),
            (a, c) => { c.Store.DeleteRoutine(a.Int("id") ?? 0); return ToolOutput.Ok("Cancelled."); });

        // ---------------- media & volume

        yield return new FnTool("media", "Control music/video playback and see what's playing.",
            Schema.Object(("action", "string", "playpause | next | previous | status", true)),
            async (a, _, _) =>
            {
                switch (a.Str("action"))
                {
                    case "next": media.Next(); break;
                    case "previous": media.Previous(); break;
                    case "playpause": media.PlayPause(); break;
                }
                await Task.Delay(400);
                var np = await media.CurrentAsync();
                return ToolOutput.Ok(np is null ? "Nothing is playing." :
                    $"{(np.Playing ? "Playing" : "Paused")}: {np.Title} — {np.Artist} ({np.App})");
            });

        yield return FnTool.Sync("volume", "Get or set the system volume.",
            Schema.Object(("level", "integer", "0-100 to set; omit to read.", false), ("mute", "boolean", "Mute/unmute.", false)),
            (a, _) =>
            {
                if (a.Int("level") is { } lvl) media.Volume = lvl;
                if (a.Bool("mute") is { } m) media.Muted = m;
                return ToolOutput.Ok($"Volume {media.Volume}%{(media.Muted ? " (muted)" : "")}.");
            });

        // ---------------- clipboard

        yield return FnTool.Sync("read_clipboard", "Read the text currently on the clipboard.", Schema.Empty(),
            (_, _) =>
            {
                string? text = null;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try { text = System.Windows.Clipboard.GetText(); } catch { }
                });
                return ToolOutput.Ok(string.IsNullOrEmpty(text) ? "(clipboard is empty)" : text);
            });

        yield return FnTool.Sync("write_clipboard", "Put text on the clipboard.",
            Schema.Object(("text", "string", "Text.", true)),
            (a, _) =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Clipboard.SetText(a.Str("text") ?? ""));
                return ToolOutput.Ok("Copied.");
            });

        // ---------------- power & shell (ask first)

        yield return FnTool.Sync("power", "Lock, sleep, restart or shut down the PC.",
            Schema.Object(("action", "string", "lock | sleep | restart | shutdown", true)),
            (a, _) =>
            {
                switch (a.Str("action"))
                {
                    case "lock": Win32.LockWorkStation(); break;
                    case "sleep": Win32.SetSuspendState(false, false, false); break;
                    case "restart": Process.Start("shutdown", "/r /t 5"); break;
                    case "shutdown": Process.Start("shutdown", "/s /t 5"); break;
                    default: return ToolOutput.Fail("Unknown power action.");
                }
                return ToolOutput.Ok("OK.");
            },
            risk: Risk.Confirm,
            describe: a => a.Str("action") switch
            {
                "lock" => "Lock the PC", "sleep" => "Put the PC to sleep",
                "restart" => "Restart the PC", "shutdown" => "Shut down the PC", var x => $"Power: {x}",
            });

        yield return new FnTool("run_powershell",
            "Run a PowerShell command and return its output. Use for system info and tasks no other tool covers. Avoid anything destructive.",
            Schema.Object(("command", "string", "PowerShell to run.", true)),
            async (a, _, ct) => await RunPowerShellAsync(a.Str("command") ?? "", ct),
            risk: Risk.Confirm,
            describe: a => $"Run PowerShell:\n{a.Str("command")}");
    }

    static void Shell(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    public static async Task<ToolOutput> RunPowerShellAsync(string command, CancellationToken ct, int timeoutSec = 60)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command);

        using var p = Process.Start(psi)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return ToolOutput.Fail($"Timed out after {timeoutSec}s.");
        }
        var output = (await stdout).Trim();
        var err = (await stderr).Trim();
        if (p.ExitCode != 0 || err.Length > 0)
            return new ToolOutput($"exit {p.ExitCode}\n{output}\n{err}".Trim(), IsError: p.ExitCode != 0);
        return ToolOutput.Ok(output.Length == 0 ? "(no output)" : output);
    }
}
