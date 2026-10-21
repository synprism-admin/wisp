using Wisp.Core;

namespace Wisp.AI;

/// <summary>Tokens a provider reported for one request.</summary>
public sealed record TokenUsage(long Input, long Output, long CacheRead = 0, long CacheWrite = 0);

/// <summary>
/// Published per-model prices (USD per million tokens) used to estimate Claude spend. An estimate
/// only: the provider's own billing is authoritative. Gemini isn't priced here (it has a free tier
/// and tiered pricing), so it's tracked in tokens.
/// </summary>
public static class Pricing
{
    static readonly Dictionary<string, (double Input, double Output)> Claude = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5-1"] = (10, 50), ["claude-fable-5"] = (10, 50),
        ["claude-opus-5-5"] = (4, 20), ["claude-opus-5"] = (5, 25),
        ["claude-opus-4-8"] = (5, 25), ["claude-opus-4-7"] = (5, 25), ["claude-opus-4-6"] = (5, 25),
        ["claude-sonnet-5-5"] = (2, 10), ["claude-sonnet-5"] = (2, 10), ["claude-sonnet-4-6"] = (3, 15),
        ["claude-haiku-5-5"] = (0.10, 0.50), ["claude-haiku-4-5"] = (1, 5),
    };

    /// <summary>Estimated cost in USD, or null when the model has no known price.</summary>
    public static double? Estimate(string provider, string model, TokenUsage u)
    {
        if (provider != "Claude" || !Claude.TryGetValue(model, out var p)) return null;
        // Cache reads bill at roughly a tenth of input; cache writes a little above input.
        return (u.Input * p.Input + u.CacheRead * p.Input * 0.1 + u.CacheWrite * p.Input * 1.25 + u.Output * p.Output) / 1_000_000;
    }
}

/// <summary>
/// Wraps a provider to record every request's usage, and to stop sending once the estimated spend
/// this month reaches the user's monthly limit.
/// </summary>
public sealed class MeteredProvider(ILlmProvider inner, Store store, Settings settings, Func<string> model) : ILlmProvider
{
    public string Name => inner.Name;
    public bool IsConfigured => inner.IsConfigured;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (settings.MonthlyLimitUsd > 0)
        {
            var spent = store.UsageThisMonth().Sum(u => u.Cost ?? 0);
            if (spent >= settings.MonthlyLimitUsd)
                throw new LlmException($"This month's AI limit (${settings.MonthlyLimitUsd:0.##}) is reached, so Wisp has paused AI features. " +
                                       "Raise or clear the limit in Settings to continue.");
        }

        var resp = await inner.CompleteAsync(request, ct);
        if (resp.Usage is { } usage)
        {
            var m = model();
            store.RecordUsage(inner.Name, m, usage, Pricing.Estimate(inner.Name, m, usage));
        }
        return resp;
    }
}
