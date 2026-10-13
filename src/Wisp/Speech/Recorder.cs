using NAudio.Wave;
using Wisp.Core;

namespace Wisp.Speech;

/// <summary>
/// Push-to-talk microphone capture at 16 kHz mono, the format Whisper wants.
/// The device is opened once and kept warm so recording starts instantly on the hotkey.
/// </summary>
public sealed class Recorder : IDisposable
{
    public const int SampleRate = 16000;

    readonly object _gate = new();
    WaveInEvent? _wave;
    List<float>? _samples;
    int _device;

    /// <summary>RMS level 0..1 of the latest buffer, for the island's waveform.</summary>
    public event Action<float>? Level;

    public bool IsRecording { get { lock (_gate) return _samples is not null; } }

    public static IReadOnlyList<string> Devices()
    {
        var list = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++) list.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return list;
    }

    public void Start(int device)
    {
        lock (_gate)
        {
            if (_wave is null || _device != device) Open(device);
            _samples = new List<float>(SampleRate * 30);
        }
    }

    /// <summary>Stops capture and returns the recorded audio as float samples in [-1, 1].</summary>
    public float[] Stop()
    {
        lock (_gate)
        {
            var s = _samples?.ToArray() ?? [];
            _samples = null;
            return s;
        }
    }

    void Open(int device)
    {
        _wave?.Dispose();
        _device = Math.Clamp(device, 0, Math.Max(0, WaveInEvent.DeviceCount - 1));
        _wave = new WaveInEvent
        {
            DeviceNumber = _device,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 40,
        };
        _wave.DataAvailable += OnData;
        _wave.RecordingStopped += (_, e) => { if (e.Exception is not null) Log.Error("Mic stopped", e.Exception); };
        _wave.StartRecording();
        Log.Info($"Microphone open: {WaveInEvent.GetCapabilities(_device).ProductName}");
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int n = e.BytesRecorded / 2;
        double sum = 0;
        lock (_gate)
        {
            for (int i = 0; i < n; i++)
            {
                float v = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                sum += v * v;
                _samples?.Add(v);
            }
            if (_samples is null) return;
        }
        Level?.Invoke((float)Math.Min(1, Math.Sqrt(sum / Math.Max(1, n)) * 6));
    }

    public void Dispose()
    {
        lock (_gate) { _wave?.Dispose(); _wave = null; }
    }
}
