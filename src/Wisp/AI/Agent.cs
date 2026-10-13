using System.Text.Json.Nodes;
using Wisp.Core;
using Wisp.Tools;

namespace Wisp.AI;

/// <summary>
/// The assistant loop: send the conversation plus tools, run whatever tools the model calls,
/// feed the results back, repeat until it answers. A conversation stays alive for a few minutes
/// so follow-ups ("now do the same for Chrome") have context.
/// </summary>
public sealed class Agent(Func<ILlmProvider> provider, ToolRegistry tools, Settings settings)
{
    static readonly TimeSpan ConversationIdle = TimeSpan.FromMinutes(5);

    readonly List<ChatMessage> _history = [];
    DateTime _lastTurn = DateTime.MinValue;
    readonly SemaphoreSlim _busy = new(1, 1);

    public void Reset() => _history.Clear();

    /// <summary>Runs one user turn in the ongoing conversation.</summary>
    public async Task<string> AskAsync(string text, byte[]? screenshot, ToolContext ctx, CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            if (DateTime.Now - _lastTurn > ConversationIdle) _history.Clear();
            _lastTurn = DateTime.Now;
            var result = await RunAsync(_history, ChatMessage.User(text, screenshot), ctx, ct);
            _lastTurn = DateTime.Now;
            return result;
        }
        finally { _busy.Release(); }
    }

    /// <summary>Runs an instruction in a fresh, throwaway conversation (routines, scheduled tasks).</summary>
    public Task<string> RunDetachedAsync(string instruction, ToolContext ctx, CancellationToken ct) =>
        RunAsync([], ChatMessage.User(instruction), ctx, ct, detached: true);

    async Task<string> RunAsync(List<ChatMessage> history, ChatMessage user, ToolContext ctx, CancellationToken ct, bool detached = false)
    {
        var llm = provider();
        history.Add(user);
        int mark = history.Count - 1;

        try
        {
            for (int step = 0; step < settings.AgentMaxSteps; step++)
            {
                var resp = await llm.CompleteAsync(new LlmRequest
                {
                    System = SystemPrompt(detached),
                    Messages = history,
                    Tools = tools.Specs(),
                    Effort = settings.AgentEffort,
                }, ct);

                history.Add(resp.Message);
                var calls = resp.ToolCalls.ToList();
                if (calls.Count == 0)
                    return string.IsNullOrWhiteSpace(resp.Text) ? "Done." : resp.Text;

                if (!string.IsNullOrWhiteSpace(resp.Text)) ctx.Progress(resp.Text);

                // All results go back in a single user message.
                var results = new List<Part>();
                foreach (var call in calls)
                    results.Add(await ExecuteAsync(call, ctx, ct));
                history.Add(new ChatMessage { Role = ChatRole.User, Parts = results });
            }
            return "I stopped after too many steps. Tell me how you'd like to continue.";
        }
        catch
        {
            // Drop the half-finished turn so the next request isn't built on a broken transcript.
            history.RemoveRange(mark, history.Count - mark);
            throw;
        }
    }

    async Task<ToolResultPart> ExecuteAsync(ToolCallPart call, ToolContext ctx, CancellationToken ct)
    {
        var tool = tools.Get(call.Name);
        if (tool is null) return new ToolResultPart(call.Id, call.Name, $"Unknown tool '{call.Name}'.", IsError: true);

        try
        {
            if (tool.Risk == Risk.Confirm && settings.ConfirmRiskyActions &&
                !await ctx.Confirm(tool.Describe(call.Args), ct))
                return new ToolResultPart(call.Id, call.Name, "The user declined this action. Don't retry it.", IsError: true);

            ctx.Progress(Friendly(call));
            Log.Info($"Tool {call.Name} {call.Args.ToJsonString()}");
            var output = await tool.RunAsync(call.Args, ctx, ct);
            return new ToolResultPart(call.Id, call.Name, Truncate(output.Text), output.Png, output.IsError);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"Tool {call.Name} failed", ex);
            return new ToolResultPart(call.Id, call.Name, $"Error: {ex.Message}", IsError: true);
        }
    }

    static string Friendly(ToolCallPart c) => c.Name switch
    {
        "open_app" => $"Opening {c.Args.Str("name")}…",
        "open_url" => "Opening link…",
        "web_search" => $"Searching {c.Args.Str("site") ?? "the web"}…",
        "inspect_window" or "list_windows" => "Looking at the screen…",
        "screenshot" => "Taking a look…",
        "click" or "click_element" => "Clicking…",
        "type_text" or "set_text" => "Typing…",
        "schedule_task" => "Scheduling…",
        _ => c.Name.Replace('_', ' ') + "…",
    };

    static string Truncate(string s) => s.Length <= 24_000 ? s : s[..24_000] + "\n…[truncated]";

    string SystemPrompt(bool detached)
    {
        var now = DateTime.Now;
        var persona = string.IsNullOrWhiteSpace(settings.Persona) ? "" : $"\nPersonality: {settings.Persona}\n";
        var mode = detached
            ? "This is a scheduled task running unattended. Do the task, then reply with one short sentence saying what you did."
            : "The user spoke to you by voice; your reply appears in a small panel at the top of their screen.";
        return $"""
            You are Wisp, an assistant that lives on {settings.UserName}'s Windows PC and gets things done on it.
            {mode}
            {persona}
            Now: {now:dddd, MMMM d, yyyy h:mm tt} (local time, {TimeZoneInfo.Local.StandardName}).

            How to respond:
            - Keep replies short: one or two plain sentences, no markdown. Say what you did, not how.
            - The transcript comes from speech recognition, so read through obvious mis-hearings.
            - Prefer doing over explaining. If a request is ambiguous and acting wrongly would be costly, ask one short question.

            Using the PC:
            - open_app for installed programs, open_url / web_search for anything on the web.
            - To work inside an app: list_windows or focus_window, then inspect_window to see its controls
              (each has an id), then click_element / set_text / press_keys. Re-inspect after the UI changes.
            - Use screenshot when the control tree doesn't show what you need (canvases, games, web content).
            - For anything at a future time or on a repeat, use schedule_task; for things to remember, add_todo.
            - Plugin tools (named plugin_*) reach the user's own systems; use them when the request is about those.
            """;
    }
}
