using System.Text.Json.Nodes;
using Wisp.AI;
using Wisp.Core;

namespace Wisp.Tools;

public enum Risk
{
    /// <summary>Read-only or trivially undoable (open an app, add a to-do).</summary>
    Safe,
    /// <summary>Changes things in a way that matters (shutdown, run a script, send data). Asks first.</summary>
    Confirm,
}

public sealed record ToolOutput(string Text, byte[]? Png = null, bool IsError = false)
{
    public static ToolOutput Ok(string text) => new(text);
    public static ToolOutput Fail(string text) => new(text, IsError: true);
}

public sealed class ToolContext
{
    public required Store Store { get; init; }
    public required Settings Settings { get; init; }
    /// <summary>Asks the user (in the island) to approve an action. Returns false if declined or timed out.</summary>
    public required Func<string, CancellationToken, Task<bool>> Confirm { get; init; }
    /// <summary>Shows a short status line in the island while the agent works.</summary>
    public required Action<string> Progress { get; init; }
}

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonObject Schema { get; }
    Risk Risk { get; }
    /// <summary>Human-readable description of a specific call, shown in the confirmation prompt.</summary>
    string Describe(JsonObject args) => $"{Name} {args.ToJsonString()}";
    Task<ToolOutput> RunAsync(JsonObject args, ToolContext ctx, CancellationToken ct);
}

/// <summary>Lambda-backed tool, for the many built-ins that are a few lines each.</summary>
public sealed class FnTool(
    string name, string description, JsonObject schema,
    Func<JsonObject, ToolContext, CancellationToken, Task<ToolOutput>> run,
    Risk risk = Risk.Safe, Func<JsonObject, string>? describe = null) : ITool
{
    public string Name => name;
    public string Description => description;
    public JsonObject Schema => schema;
    public Risk Risk => risk;
    public string Describe(JsonObject args) => describe?.Invoke(args) ?? $"{name} {args.ToJsonString()}";
    public Task<ToolOutput> RunAsync(JsonObject args, ToolContext ctx, CancellationToken ct) => run(args, ctx, ct);

    public static FnTool Sync(string name, string description, JsonObject schema,
        Func<JsonObject, ToolContext, ToolOutput> run, Risk risk = Risk.Safe, Func<JsonObject, string>? describe = null) =>
        new(name, description, schema, (a, c, _) => Task.FromResult(run(a, c)), risk, describe);
}

public sealed class ToolRegistry
{
    readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public void Add(ITool tool) => _tools[tool.Name] = tool;
    public void AddRange(IEnumerable<ITool> tools) { foreach (var t in tools) Add(t); }
    public void RemoveWhere(Func<ITool, bool> pred)
    {
        foreach (var k in _tools.Where(kv => pred(kv.Value)).Select(kv => kv.Key).ToList()) _tools.Remove(k);
    }

    public ITool? Get(string name) => _tools.GetValueOrDefault(name);
    public IReadOnlyCollection<ITool> All => _tools.Values;

    public IReadOnlyList<ToolSpec> Specs() =>
        _tools.Values.OrderBy(t => t.Name).Select(t => new ToolSpec(t.Name, t.Description, t.Schema)).ToList();
}
