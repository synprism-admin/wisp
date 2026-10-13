using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Wisp.Core;

namespace Wisp.AI;

/// <summary>Gemini via the Generative Language REST API (generateContent with function calling).</summary>
public sealed class GeminiProvider(Settings settings) : ILlmProvider
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public string Name => "Gemini";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.GeminiKey);

    public async Task<LlmResponse> CompleteAsync(LlmRequest req, CancellationToken ct)
    {
        var key = settings.GeminiKey ?? throw new LlmException("No Gemini API key set. Add one in Wisp settings.");

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = req.System }) },
            ["contents"] = new JsonArray(req.Messages.Select(ToContent).ToArray<JsonNode?>()),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = req.MaxTokens },
        };
        if (req.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray(req.Tools.Select(t => (JsonNode?)new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parametersJsonSchema"] = t.Schema.DeepClone(),
                }).ToArray()),
            });
        }

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{settings.GeminiModel}:generateContent";
        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("x-goog-api-key", key);

        using var resp = await Http.SendAsync(msg, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text;
            throw new LlmException($"Gemini error ({(int)resp.StatusCode}): {err}");
        }

        var json = JsonNode.Parse(text)!;
        var candidate = json["candidates"]?[0];
        var rawParts = candidate?["content"]?["parts"]?.AsArray() ?? [];
        var parts = new List<Part>();
        int n = 0;
        foreach (var p in rawParts)
        {
            if (p?["text"] is JsonNode t && p["thought"]?.GetValue<bool>() != true)
                parts.Add(new TextPart(t.GetValue<string>()));
            else if (p?["functionCall"] is JsonObject fc)
                parts.Add(new ToolCallPart($"call_{n++}_{fc["name"]}", fc["name"]!.GetValue<string>(),
                    fc["args"]?.DeepClone().AsObject() ?? []));
        }

        var finish = candidate?["finishReason"]?.GetValue<string>() ?? "STOP";
        // Raw parts carry thought signatures that Gemini requires back on the next turn.
        var native = rawParts.DeepClone().AsArray();
        return new LlmResponse(
            new ChatMessage { Role = ChatRole.Assistant, Parts = parts, Native = native, NativeProvider = Name },
            finish);
    }

    JsonNode ToContent(ChatMessage m)
    {
        if (m.Role == ChatRole.Assistant && m.NativeProvider == Name && m.Native is JsonArray native)
            return new JsonObject { ["role"] = "model", ["parts"] = native.DeepClone() };

        var parts = new JsonArray();
        foreach (var part in m.Parts)
        {
            switch (part)
            {
                case TextPart t when !string.IsNullOrEmpty(t.Text):
                    parts.Add(new JsonObject { ["text"] = t.Text });
                    break;
                case ImagePart i:
                    parts.Add(InlinePng(i.Png));
                    break;
                case ToolCallPart c:
                    parts.Add(new JsonObject { ["functionCall"] = new JsonObject { ["name"] = c.Name, ["args"] = c.Args.DeepClone() } });
                    break;
                case ToolResultPart r:
                    parts.Add(new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = r.Name,
                            ["response"] = new JsonObject { [r.IsError ? "error" : "result"] = r.Text },
                        },
                    });
                    // Function responses can't carry images, so the screenshot rides along as a sibling part.
                    if (r.Png is not null) parts.Add(InlinePng(r.Png));
                    break;
            }
        }
        return new JsonObject { ["role"] = m.Role == ChatRole.User ? "user" : "model", ["parts"] = parts };
    }

    static JsonObject InlinePng(byte[] png) => new()
    {
        ["inlineData"] = new JsonObject { ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(png) },
    };
}
