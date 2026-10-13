using Windows.Devices.Radios;
using Wisp.Core;

namespace Wisp.Desktop;

/// <summary>Wi-Fi and Bluetooth on/off through the Windows radio API.</summary>
public static class Radios
{
    static async Task<Radio?> FindAsync(RadioKind kind)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception ex) { Log.Error($"Radio lookup ({kind}) failed", ex); return null; }
    }

    public static async Task<bool?> IsOnAsync(bool wifi) =>
        (await FindAsync(wifi ? RadioKind.WiFi : RadioKind.Bluetooth)) is { } r ? r.State == RadioState.On : null;

    public static async Task<bool> SetAsync(bool wifi, bool on)
    {
        var r = await FindAsync(wifi ? RadioKind.WiFi : RadioKind.Bluetooth);
        if (r is null) return false;
        return await r.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
    }
}
