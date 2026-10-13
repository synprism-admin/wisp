using NAudio.CoreAudioApi;
using Windows.Media.Control;
using Wisp.Native;

namespace Wisp.Core;

public sealed record NowPlaying(string Title, string Artist, string App, bool Playing);

/// <summary>Now-playing info + transport (System Media Transport Controls) and master volume.</summary>
public sealed class Media
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;

    public event Action? Changed;

    public async Task InitAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => { Hook(); Changed?.Invoke(); };
            Hook();
        }
        catch (Exception ex) { Log.Error("Media session manager unavailable", ex); }
    }

    GlobalSystemMediaTransportControlsSession? _hooked;
    void Hook()
    {
        var s = _mgr?.GetCurrentSession();
        if (s == _hooked) return;
        _hooked = s;
        if (s is null) return;
        s.MediaPropertiesChanged += (_, _) => Changed?.Invoke();
        s.PlaybackInfoChanged += (_, _) => Changed?.Invoke();
    }

    public async Task<NowPlaying?> CurrentAsync()
    {
        var s = _mgr?.GetCurrentSession();
        if (s is null) return null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var app = s.SourceAppUserModelId.Split('!')[0].Split('.').LastOrDefault() ?? "";
            return new NowPlaying(props.Title, props.Artist, app.Replace(".exe", ""), playing);
        }
        catch { return null; }
    }

    public void PlayPause() => Tap(VK.MEDIA_PLAY_PAUSE);
    public void Next() => Tap(VK.MEDIA_NEXT);
    public void Previous() => Tap(VK.MEDIA_PREV);

    static void Tap(int vk) => Win32.Send(Win32.Key((ushort)vk, false, true), Win32.Key((ushort)vk, true, true));

    static MMDevice Speakers()
    {
        using var e = new MMDeviceEnumerator();
        return e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public int Volume
    {
        get { using var d = Speakers(); return (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100); }
        set { using var d = Speakers(); d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0, 100) / 100f; }
    }

    public bool Muted
    {
        get { using var d = Speakers(); return d.AudioEndpointVolume.Mute; }
        set { using var d = Speakers(); d.AudioEndpointVolume.Mute = value; }
    }
}
