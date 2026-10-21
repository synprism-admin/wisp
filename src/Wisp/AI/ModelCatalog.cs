using System.Net.Http;
using System.Text.Json.Nodes;
using Anthropic;
using Wisp.Core;

namespace Wisp.AI;

public sealed record ModelChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The models each provider offers, for the Settings dropdowns. Live from the provider's models API
/// when there's a key (so new models show up on their own), with a built-in list as the fallback.
/// </summary>
public static class ModelCatalog
{
    public const string DefaultClaude = "claude-opus-5-5";
    public const string DefaultGemini = "gemini-flash-latest";

    public static readonly IReadOnlyList<ModelChoice> ClaudeFallback =
    [
        new("claude-opus-5-5", "Claude Opus 5.5 (Recommended)"),
        new("claude-fable-5-1", "Claude Fable 5.1 (most capable, slower)"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5"),
        new("claude-haiku-5-5", "Claude Haiku 5.5 (fastest)"),
        new("claude-opus-5", "Claude Opus 5"),
        new("claude-sonnet-5", "Claude Sonnet 5"),
    ];

    public static readonly IReadOnlyList<ModelChoice> GeminiFallback =
    [
        new("gemini-flash-latest", "Gemini Flash, latest (Recommended)"),
        new("gemini-pro-latest", "Gemini Pro, latest"),
        new("gemini-flash-lite-latest", "Gemini Flash-Lite, latest"),
    ];

    public static async Task<IReadOnlyList<ModelChoice>> ClaudeAsync(string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return ClaudeFallback;
        try
        {
            var client = new AnthropicClient { ApiKey = apiKey };
            var page = await client.Models.List(cancellationToken: ct);
            var live = page.Items.Select(m => new ModelChoice(m.ID, m.ID == DefaultClaude ? $"{m.DisplayName} (Recommended)" : m.DisplayName)).ToList();
            return live.Count > 0 ? live : ClaudeFallback;
        }
        catch (Exception ex)
        {
            Log.Error("Listing Claude models failed; using the built-in list", ex);
            return ClaudeFallback;
        }
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<IReadOnlyList<ModelChoice>> GeminiAsync(string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return GeminiFallback;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=200");
            req.Headers.Add("x-goog-api-key", apiKey);
            using var resp = await Http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var live = (json?["models"]?.AsArray() ?? [])
                .Where(m => m?["supportedGenerationMethods"]?.AsArray().Any(x => x?.GetValue<string>() == "generateContent") == true)
                .Select(m => (Id: m!["name"]!.GetValue<string>().Replace("models/", ""), Name: m["displayName"]?.GetValue<string>() ?? ""))
                .Where(m => m.Id.StartsWith("gemini") && !m.Id.Contains("tts") && !m.Id.Contains("image") && !m.Id.Contains("embedding"))
                .OrderBy(m => m.Id.EndsWith("-latest") ? 0 : 1).ThenByDescending(m => m.Id)
                .Select(m => new ModelChoice(m.Id, m.Id == DefaultGemini ? $"{m.Name} (Recommended)" : $"{m.Name}  ·  {m.Id}"))
                .ToList();
            return live.Count > 0 ? live : GeminiFallback;
        }
        catch (Exception ex)
        {
            Log.Error("Listing Gemini models failed; using the built-in list", ex);
            return GeminiFallback;
        }
    }

    /// <summary>Server-side refusal fallback is only accepted by these Claude models.</summary>
    public static bool SupportsRefusalFallback(string model) =>
        model is "claude-fable-5-1" or "claude-opus-5-5" or "claude-opus-5" or "claude-sonnet-5-5";
}
