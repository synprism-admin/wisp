using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Wisp.Core;

namespace Wisp.AI;

/// <summary>Claude via the official Anthropic SDK (beta endpoint, for server-side refusal fallbacks).</summary>
public sealed class ClaudeProvider(Settings settings) : ILlmProvider
{
    AnthropicClient? _client;
    string? _clientKey;

    public string Name => "Claude";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.ClaudeKey);

    AnthropicClient Client()
    {
        var key = settings.ClaudeKey ?? throw new LlmException("No Claude API key set. Add one in Wisp settings.");
        if (_client is null || _clientKey != key)
        {
            _client = new AnthropicClient { ApiKey = key };
            _clientKey = key;
        }
        return _client;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest req, CancellationToken ct)
    {
        var p = new MessageCreateParams
        {
            Model = settings.ClaudeModel,
            MaxTokens = req.MaxTokens,
            System = req.System,
            Messages = req.Messages.Select(ToParam).ToList(),
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(req.Effort) },
        };
        // If a safety classifier declines, the server re-serves the turn on a suitable model.
        // Only some models accept this; sending it to others (e.g. Haiku) is an error.
        if (ModelCatalog.SupportsRefusalFallback(settings.ClaudeModel))
            p = p with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
        if (req.Tools.Count > 0)
            p = p with { Tools = req.Tools.Select(ToTool).ToList() };

        BetaMessage resp;
        try
        {
            resp = await Client().Beta.Messages.Create(p, ct);
        }
        catch (AnthropicApiException ex)
        {
            throw new LlmException($"Claude error: {ex.Message}", ex);
        }

        var parts = new List<Part>();
        var echo = new List<BetaContentBlockParam>();
        foreach (var block in resp.Content)
        {
            if (block.TryPickText(out var t))
            {
                parts.Add(new TextPart(t.Text));
                echo.Add(new BetaTextBlockParam { Text = t.Text });
            }
            else if (block.TryPickThinking(out var th))
            {
                echo.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            }
            else if (block.TryPickRedactedThinking(out var rt))
            {
                echo.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
            }
            else if (block.TryPickToolUse(out var tu))
            {
                parts.Add(new ToolCallPart(tu.ID, tu.Name, Schema.FromElements(tu.Input)));
                echo.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
            }
        }

        var stop = resp.StopReason?.ToString() ?? "end_turn";
        if (stop.Contains("refusal", StringComparison.OrdinalIgnoreCase) && parts.Count == 0)
            parts.Add(new TextPart("I can't help with that one."));

        var msg = new ChatMessage { Role = ChatRole.Assistant, Parts = parts, Native = echo, NativeProvider = Name };
        return new LlmResponse(msg, stop);
    }

    BetaMessageParam ToParam(ChatMessage m)
    {
        if (m.Role == ChatRole.Assistant && m.NativeProvider == Name && m.Native is List<BetaContentBlockParam> native)
            return new BetaMessageParam { Role = Role.Assistant, Content = native };

        var blocks = new List<BetaContentBlockParam>();
        foreach (var part in m.Parts)
        {
            switch (part)
            {
                case TextPart t when !string.IsNullOrEmpty(t.Text):
                    blocks.Add(new BetaTextBlockParam { Text = t.Text });
                    break;
                case ImagePart i:
                    blocks.Add(Image(i.Png));
                    break;
                case ToolCallPart c:
                    blocks.Add(new BetaToolUseBlockParam
                    {
                        ID = c.Id, Name = c.Name,
                        Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(c.Args.ToJsonString())!,
                    });
                    break;
                case ToolResultPart r:
                    blocks.Add(ToolResult(r));
                    break;
            }
        }
        return new BetaMessageParam { Role = m.Role == ChatRole.User ? Role.User : Role.Assistant, Content = blocks };
    }

    static BetaImageBlockParam Image(byte[] png) => new()
    {
        Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(png), MediaType = MediaType.ImagePng },
    };

    static BetaToolResultBlockParam ToolResult(ToolResultPart r)
    {
        if (r.Png is null)
            return new BetaToolResultBlockParam { ToolUseID = r.Id, Content = r.Text, IsError = r.IsError };

        List<Block> content =
        [
            new BetaTextBlockParam { Text = r.Text },
            Image(r.Png),
        ];
        return new BetaToolResultBlockParam { ToolUseID = r.Id, Content = content, IsError = r.IsError };
    }

    static BetaToolUnion ToTool(ToolSpec s)
    {
        var props = s.Schema["properties"]?.AsObject() ?? [];
        var required = s.Schema["required"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        return new BetaTool
        {
            Name = s.Name,
            Description = s.Description,
            InputSchema = new()
            {
                Properties = props.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
                Required = required,
            },
        };
    }

    static Effort ParseEffort(string e) => e.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };
}
