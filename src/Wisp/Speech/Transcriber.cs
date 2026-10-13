using System.IO;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;
using Wisp.Core;

namespace Wisp.Speech;

/// <summary>
/// Local speech-to-text with whisper.cpp. Runs on the GPU through Vulkan (just needs the normal
/// graphics driver), falling back to CPU. The model stays loaded so each utterance is fast.
/// </summary>
public sealed class Transcriber : IDisposable
{
    readonly Settings _settings;
    readonly SemaphoreSlim _lock = new(1, 1);
    WhisperFactory? _factory;
    string? _loadedModel;

    public string Status { get; private set; } = "Not loaded";
    public event Action<string>? StatusChanged;

    public Transcriber(Settings settings)
    {
        _settings = settings;
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
    }

    void SetStatus(string s) { Status = s; StatusChanged?.Invoke(s); Log.Info($"Whisper: {s}"); }

    /// <summary>Loads (downloading on first run) the configured model. Safe to call repeatedly.</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_factory is not null && _loadedModel == _settings.WhisperModel) return;
            _factory?.Dispose();
            _factory = null;

            var type = Enum.TryParse<GgmlType>(_settings.WhisperModel, out var t) ? t : GgmlType.LargeV3Turbo;
            var quant = type is GgmlType.LargeV3Turbo or GgmlType.LargeV3 ? QuantizationType.Q5_0 : QuantizationType.NoQuantization;
            var path = Path.Combine(Paths.Models, $"ggml-{type}-{quant}.bin".ToLowerInvariant());

            if (!File.Exists(path))
            {
                SetStatus($"Downloading speech model ({type})…");
                var tmp = path + ".part";
                await using (var src = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, quant, ct))
                await using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst, ct);
                File.Move(tmp, path, overwrite: true);
            }

            SetStatus("Loading speech model…");
            _factory = WhisperFactory.FromPath(path);
            _loadedModel = _settings.WhisperModel;
            SetStatus($"Ready ({type}, {RuntimeOptions.LoadedLibrary?.ToString() ?? "auto"})");
        }
        catch (Exception ex)
        {
            SetStatus("Speech model failed to load");
            Log.Error("Whisper load failed", ex);
            throw;
        }
        finally { _lock.Release(); }
    }

    public async Task<string> TranscribeAsync(float[] samples, string? prompt = null, CancellationToken ct = default)
    {
        // Ignore taps: under ~0.3s of audio is never real speech.
        if (samples.Length < Recorder.SampleRate * 3 / 10) return "";
        await EnsureLoadedAsync(ct);

        var builder = _factory!.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(_settings.Language) ? "auto" : _settings.Language)
            .WithNoContext();
        if (!string.IsNullOrWhiteSpace(prompt)) builder = builder.WithPrompt(prompt);

        await using var processor = builder.Build();
        var sb = new StringBuilder();
        await foreach (var seg in processor.ProcessAsync(samples, ct))
            sb.Append(seg.Text);

        var text = sb.ToString().Trim();
        // Whisper hallucinates these on silence.
        return text is "[BLANK_AUDIO]" or "(silence)" or "[ Silence ]" or "Thank you." or "you" ? "" : text;
    }

    public void Dispose() => _factory?.Dispose();
}
