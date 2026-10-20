namespace Wisp.AI;

/// <summary>Stands in for the real provider when AI features are switched off: sends nothing, ever.</summary>
public sealed class DisabledProvider : ILlmProvider
{
    public string Name => "AI features";
    public bool IsConfigured => false;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
        throw new LlmException("AI features are turned off. Turn them on in Settings to use this.");
}
