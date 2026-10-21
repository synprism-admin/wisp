using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wisp.AI;

/// <summary>Provider-neutral conversation model. Each provider translates to its own wire format.</summary>
public enum ChatRole { User, Assistant }

public abstract record Part;
public sealed record TextPart(string Text) : Part;
public sealed record ImagePart(byte[] Png) : Part;
public sealed record ToolCallPart(string Id, string Name, JsonObject Args) : Part;
public sealed record ToolResultPart(string Id, string Name, string Text, byte[]? Png = null, bool IsError = false) : Part;

public sealed class ChatMessage
{
    public required ChatRole Role { get; init; }
    public required List<Part> Parts { get; init; }

    /// <summary>
    /// The provider's own copy of an assistant turn (thinking blocks, signatures, etc.) that must be
    /// echoed back verbatim on the next request. Only used when the same provider reads it back.
    /// </summary>
    public object? Native { get; init; }
    public string? NativeProvider { get; init; }

    public static ChatMessage User(string text, byte[]? png = null)
    {
        var parts = new List<Part>();
        if (png is not null) parts.Add(new ImagePart(png));
        parts.Add(new TextPart(text));
        return new ChatMessage { Role = ChatRole.User, Parts = parts };
    }
}

public sealed record ToolSpec(string Name, string Description, JsonObject Schema);

public sealed record LlmRequest
{
    public required string System { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<ToolSpec> Tools { get; init; } = [];
    public string Effort { get; init; } = "medium";
    public int MaxTokens { get; init; } = 16000;
}

public sealed record LlmResponse(ChatMessage Message, string StopReason, TokenUsage? Usage = null)
{
    public string Text => string.Concat(Message.Parts.OfType<TextPart>().Select(p => p.Text)).Trim();
    public IEnumerable<ToolCallPart> ToolCalls => Message.Parts.OfType<ToolCallPart>();
}

public interface ILlmProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}

public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);

public static class Schema
{
    /// <summary>Tiny helper for building JSON-schema objects for tools.</summary>
    public static JsonObject Object(params (string name, string type, string description, bool required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, req) in props)
        {
            properties[name] = new JsonObject { ["type"] = type, ["description"] = description };
            if (req) required.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    public static JsonObject Empty() => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public static string? Str(this JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v?.ToString();

    public static int? Int(this JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) && v is not null && int.TryParse(v.ToString(), out var i) ? i : null;

    public static bool? Bool(this JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) && v is not null && bool.TryParse(v.ToString(), out var b) ? b : null;

    public static JsonObject ParseObject(string json) =>
        JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();

    public static JsonObject FromElements(IReadOnlyDictionary<string, JsonElement> d)
    {
        var o = new JsonObject();
        foreach (var (k, v) in d) o[k] = JsonNode.Parse(v.GetRawText());
        return o;
    }
}
