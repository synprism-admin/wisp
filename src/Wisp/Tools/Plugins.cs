using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wisp.AI;
using Wisp.Core;

namespace Wisp.Tools;

/// <summary>
/// User plugins: JSON manifests in %APPDATA%\Wisp\plugins that turn HTTP endpoints, PowerShell
/// snippets or executables into tools the assistant can call. This is how Wisp reaches your own
/// systems without recompiling. Manifests hot-reload when saved.
///
/// Values like "${env:NEXUS_TOKEN}" are read from environment variables so secrets stay out of the file.
/// </summary>
public sealed partial class PluginHost : IDisposable
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    // For home-lab gear with self-signed certs (UniFi, Proxmox, ...). Opt-in per manifest.
    static readonly HttpClient InsecureHttp = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    }) { Timeout = TimeSpan.FromSeconds(30) };
    readonly ToolRegistry _registry;
    readonly FileSystemWatcher _watcher;
    Timer? _debounce;

    public IReadOnlyList<string> Loaded { get; private set; } = [];
    public IReadOnlyList<string> Errors { get; private set; } = [];
    public event Action? Reloaded;

    public PluginHost(ToolRegistry registry)
    {
        _registry = registry;
        _watcher = new FileSystemWatcher(Paths.Plugins, "*.json") { EnableRaisingEvents = true, IncludeSubdirectories = false };
        _watcher.Changed += (_, _) => Debounce();
        _watcher.Created += (_, _) => Debounce();
        _watcher.Deleted += (_, _) => Debounce();
        _watcher.Renamed += (_, _) => Debounce();
    }

    void Debounce()
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => Reload(), null, 400, Timeout.Infinite);
    }

    public void Reload()
    {
        _registry.RemoveWhere(t => t is PluginTool);
        var loaded = new List<string>();
        var errors = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Paths.Plugins, "*.json"))
        {
            try
            {
                var manifest = JsonNode.Parse(File.ReadAllText(file), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
                if (manifest["enabled"]?.GetValue<bool>() == false) continue;
                var plugin = Sanitize(manifest["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file));
                var vars = manifest["variables"]?.AsObject() ?? [];
                foreach (var t in manifest["tools"]?.AsArray() ?? [])
                    _registry.Add(new PluginTool(plugin, t!.AsObject(), vars, manifest["description"]?.GetValue<string>(),
                        manifest["allowSelfSignedCerts"]?.GetValue<bool>() == true));
                loaded.Add(plugin);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                Log.Error($"Plugin {file} failed to load", ex);
            }
        }
        Loaded = loaded;
        Errors = errors;
        Log.Info($"Plugins loaded: {string.Join(", ", loaded)}");
        Reloaded?.Invoke();
    }

    /// <summary>Copies the bundled examples (disabled) into the plugins folder on first run.</summary>
    public static void SeedExamples()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "plugins.examples");
        if (!Directory.Exists(src)) return;
        foreach (var f in Directory.EnumerateFiles(src, "*.json"))
        {
            var dst = Path.Combine(Paths.Plugins, Path.GetFileName(f));
            if (!File.Exists(dst)) File.Copy(f, dst);
        }
    }

    internal static string Sanitize(string s) => NonWord().Replace(s.ToLowerInvariant(), "_").Trim('_');

    [GeneratedRegex(@"[^a-z0-9_]+")] private static partial Regex NonWord();

    public void Dispose() { _watcher.Dispose(); _debounce?.Dispose(); }

    // ------------------------------------------------------------------ tool

    sealed partial class PluginTool : ITool
    {
        readonly JsonObject _def;
        readonly JsonObject _vars;
        readonly bool _insecure;

        public string Name { get; }
        public string Description { get; }
        public JsonObject Schema { get; }
        public Risk Risk { get; }

        public PluginTool(string plugin, JsonObject def, JsonObject vars, string? pluginDescription, bool insecure)
        {
            _insecure = insecure;
            _def = def;
            _vars = vars;
            var name = Sanitize(def["name"]?.GetValue<string>() ?? throw new InvalidDataException("tool without name"));
            Name = $"plugin_{plugin}_{name}";
            if (Name.Length > 64) Name = Name[..64];
            Description = (pluginDescription is null ? "" : $"[{pluginDescription}] ") + (def["description"]?.GetValue<string>() ?? name);
            Risk = def["risk"]?.GetValue<string>() == "confirm" ? Risk.Confirm : Risk.Safe;

            var props = def["parameters"]?.DeepClone().AsObject() ?? [];
            var required = def["required"]?.DeepClone().AsArray() ?? [];
            Schema = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required };
        }

        public string Describe(JsonObject args) =>
            $"{Name.Replace("plugin_", "").Replace('_', ' ')}" + (args.Count > 0 ? $"\n{args.ToJsonString()}" : "");

        public async Task<ToolOutput> RunAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
        {
            if (_def["http"] is JsonObject http) return await RunHttpAsync(http, args, ct);
            if (_def["powershell"] is JsonNode ps) return await SystemTools.RunPowerShellAsync(Fill(ps.GetValue<string>(), args, PsQuote), ct);
            if (_def["command"] is JsonObject cmd) return await RunCommandAsync(cmd, args, ct);
            return ToolOutput.Fail("Plugin tool has no http/powershell/command action.");
        }

        async Task<ToolOutput> RunHttpAsync(JsonObject http, JsonObject args, CancellationToken ct)
        {
            var method = new HttpMethod((http["method"]?.GetValue<string>() ?? "GET").ToUpperInvariant());
            var url = Fill(http["url"]!.GetValue<string>(), args, Uri.EscapeDataString);
            using var req = new HttpRequestMessage(method, url);
            foreach (var (k, v) in http["headers"]?.AsObject() ?? [])
                req.Headers.TryAddWithoutValidation(k, Fill(v!.GetValue<string>(), args, s => s));
            if (http["body"] is JsonNode body)
            {
                var filled = body is JsonValue jv && jv.TryGetValue<string>(out var s)
                    ? Fill(s, args, x => x)
                    : FillJson(body.DeepClone(), args).ToJsonString();
                req.Content = new StringContent(filled, Encoding.UTF8, http["contentType"]?.GetValue<string>() ?? "application/json");
            }

            using var resp = await (_insecure ? InsecureHttp : Http).SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (text.Length > 20_000) text = text[..20_000] + "…[truncated]";
            return new ToolOutput($"HTTP {(int)resp.StatusCode}\n{text}", IsError: !resp.IsSuccessStatusCode);
        }

        async Task<ToolOutput> RunCommandAsync(JsonObject cmd, JsonObject args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(Fill(cmd["exe"]!.GetValue<string>(), args, s => s))
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in cmd["args"]?.AsArray() ?? []) psi.ArgumentList.Add(Fill(a!.GetValue<string>(), args, s => s));
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct) + await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return new ToolOutput($"exit {p.ExitCode}\n{output}".Trim(), IsError: p.ExitCode != 0);
        }

        /// <summary>Replaces {{param}} with call arguments and {{var}} with manifest variables.</summary>
        string Fill(string template, JsonObject args, Func<string, string> encode) =>
            Placeholder().Replace(template, m =>
            {
                var key = m.Groups[1].Value;
                if (args.TryGetPropertyValue(key, out var v) && v is not null) return encode(Plain(v));
                if (_vars.TryGetPropertyValue(key, out var gv) && gv is not null) return ResolveEnv(Plain(gv));
                return "";
            });

        JsonNode FillJson(JsonNode node, JsonObject args)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var k in o.Select(kv => kv.Key).ToList()) o[k] = FillJson(o[k]!, args);
                    return o;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++) a[i] = FillJson(a[i]!, args);
                    return a;
                case JsonValue v when v.TryGetValue<string>(out var s):
                    // A value that is exactly "{{param}}" keeps the argument's JSON type (number, bool, object).
                    var whole = Placeholder().Match(s);
                    if (whole.Success && whole.Length == s.Length && args.TryGetPropertyValue(whole.Groups[1].Value, out var arg) && arg is not null)
                        return arg.DeepClone();
                    return JsonValue.Create(Fill(s, args, x => x));
                default:
                    return node;
            }
        }

        static string Plain(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();

        static string ResolveEnv(string s) => EnvRef().Replace(s, m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");

        static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

        [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}")] private static partial Regex Placeholder();
        [GeneratedRegex(@"\$\{env:([A-Za-z0-9_]+)\}")] private static partial Regex EnvRef();
    }
}
