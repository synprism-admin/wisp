using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Wisp.Core;

public enum DeviceKind { Keyboard, Mouse, Headphones, Controller, Other }

/// <summary>A wireless device and its battery as Windows last heard it.</summary>
public sealed record BatteryDevice(string Name, DeviceKind Kind, int Percent, bool Connected, DateTime? Reported)
{
    public bool Charging { get; init; }
    public bool Low => Percent <= 20 && !Charging;
}

/// <summary>
/// Battery levels of paired Bluetooth devices (keyboards, mice, headphones, controllers), read from
/// the same device properties the Settings app uses. Nothing leaves the PC. Devices on a vendor USB
/// receiver don't report their battery to Windows; Razer receivers are asked directly (RazerBattery).
/// Rescans every minute.
/// </summary>
public sealed class DeviceBatteries
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    bool _busy, _sample;

    public IReadOnlyList<BatteryDevice> All { get; private set; } = [];
    public IEnumerable<BatteryDevice> Connected => All.Where(d => d.Connected);
    public DateTime? Scanned { get; private set; }
    public event Action? Changed;

    public DeviceBatteries() => _timer.Tick += async (_, _) => await RefreshAsync();

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_busy || _sample) return;
        _busy = true;
        try
        {
            var found = await Task.Run(Scan);
            Scanned = DateTime.Now;
            if (!found.SequenceEqual(All))
            {
                All = found;
                Changed?.Invoke();
            }
        }
        catch (Exception ex) { Log.Error("Device battery scan failed", ex); }
        finally { _busy = false; }
    }

    /// <summary>For snapshots: a believable set of devices, one of them low.</summary>
    public void UseSample()
    {
        _sample = true;
        Scanned = DateTime.Now;
        All =
        [
            new("PT Ergo", DeviceKind.Keyboard, 100, true, DateTime.Now),
            new("MX Master 3", DeviceKind.Mouse, 15, true, DateTime.Now),
            new("WH-1000XM5", DeviceKind.Headphones, 60, false, DateTime.Now.AddDays(-3)),
        ];
        Changed?.Invoke();
    }

    /// <summary>Segoe Fluent Icons glyph for the kind of device.</summary>
    public static string Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Keyboard => "",
        DeviceKind.Mouse => "",
        DeviceKind.Headphones => "",
        DeviceKind.Controller => "",
        _ => "",
    };

    // ------------------------------------------------------------------ scanning

    sealed record Node(string Id, uint Dev, Guid Container);

    static List<BatteryDevice> Scan()
    {
        var nodes = new List<Node>();
        foreach (var enumerator in new[] { "BTHLE", "BTHENUM" })
            foreach (var id in DeviceIds(enumerator))
                if (CM_Locate_DevNodeW(out uint dev, id, 0) == 0 && ReadGuid(dev, ContainerId) is { } c && c != System.Guid.Empty)
                    nodes.Add(new Node(id, dev, c));

        // One physical device shows up as several nodes (its services) sharing a container id;
        // the battery can sit on any of them, the name and type on the device node itself.
        var list = new List<BatteryDevice>();
        foreach (var g in nodes.GroupBy(n => n.Container))
        {
            int? pct = null;
            DateTime? reported = null;
            foreach (var n in g)
                if (ReadByte(n.Dev, BatteryLevel) is byte b)
                {
                    pct = Math.Max(pct ?? 0, b);
                    reported = ReadFileTime(n.Dev, BatteryReported) ?? reported;
                }
            if (pct is null) continue;

            var main = g.FirstOrDefault(n => n.Id.Contains(@"\DEV_", StringComparison.OrdinalIgnoreCase)) ?? g.First();
            var name = ReadString(main.Dev, FriendlyName)
                ?? g.Select(n => ReadString(n.Dev, FriendlyName)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                ?? "Bluetooth device";
            bool connected = g.Any(n => ReadBool(n.Dev, IsConnected) == true);
            var category = string.Join(" ", g.SelectMany(n => ReadStrings(n.Dev, Category)).Distinct());
            list.Add(new BatteryDevice(name, KindOf(category), Math.Clamp(pct.Value, 0, 100), connected, reported));
        }
        try { list.AddRange(RazerBattery.Scan()); }
        catch (Exception ex) { Log.Error("Razer battery scan failed", ex); }
        return list.OrderByDescending(d => d.Connected).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Device container categories look like "Input.Keyboard", "Input.Mouse", "Audio.Headphone".</summary>
    static DeviceKind KindOf(string category) =>
        category.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Keyboard
        : category.Contains("Mouse", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Mouse
        : category.Contains("Gaming", StringComparison.OrdinalIgnoreCase) || category.Contains("Joystick", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Controller
        : category.Contains("Head", StringComparison.OrdinalIgnoreCase) || category.Contains("Audio", StringComparison.OrdinalIgnoreCase) ? DeviceKind.Headphones
        : DeviceKind.Other;

    static IEnumerable<string> DeviceIds(string enumerator)
    {
        const uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        if (CM_Get_Device_ID_List_SizeW(out uint len, enumerator, flags) != 0 || len == 0) return [];
        var buf = new char[len];
        if (CM_Get_Device_ID_ListW(enumerator, buf, len, flags) != 0) return [];
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    // ------------------------------------------------------------------ device properties

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY(Guid fmtid, uint pid)
    {
        public Guid Fmtid = fmtid;
        public uint Pid = pid;
    }

    static readonly DEVPROPKEY BatteryLevel = new(new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    static readonly DEVPROPKEY BatteryReported = new(new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 7);
    static readonly DEVPROPKEY IsConnected = new(new("83DA6326-97A6-4088-9453-A1923F573B29"), 15);
    static readonly DEVPROPKEY FriendlyName = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    static readonly DEVPROPKEY ContainerId = new(new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    static readonly DEVPROPKEY Category = new(new("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 90);

    const uint DEVPROP_TYPE_BYTE = 0x03, DEVPROP_TYPE_GUID = 0x0D, DEVPROP_TYPE_FILETIME = 0x10,
        DEVPROP_TYPE_BOOLEAN = 0x11, DEVPROP_TYPE_STRING = 0x12, DEVPROP_TYPE_STRING_LIST = 0x2012;

    static byte[]? Prop(uint dev, DEVPROPKEY key, uint expectedType)
    {
        uint size = 0;
        if (CM_Get_DevNode_PropertyW(dev, ref key, out uint type, null, ref size, 0) != CR_BUFFER_SMALL || size == 0) return null;
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(dev, ref key, out type, buf, ref size, 0) != 0 || type != expectedType) return null;
        return buf;
    }

    static byte? ReadByte(uint dev, DEVPROPKEY key) => Prop(dev, key, DEVPROP_TYPE_BYTE) is { Length: > 0 } b ? b[0] : null;
    static bool? ReadBool(uint dev, DEVPROPKEY key) => Prop(dev, key, DEVPROP_TYPE_BOOLEAN) is { Length: > 0 } b ? b[0] != 0 : null;
    static Guid? ReadGuid(uint dev, DEVPROPKEY key) => Prop(dev, key, DEVPROP_TYPE_GUID) is { Length: 16 } b ? new Guid(b) : null;

    static DateTime? ReadFileTime(uint dev, DEVPROPKEY key)
    {
        if (Prop(dev, key, DEVPROP_TYPE_FILETIME) is not { Length: 8 } b) return null;
        try { return DateTime.FromFileTime(BitConverter.ToInt64(b)); } catch { return null; }
    }

    static string? ReadString(uint dev, DEVPROPKEY key) =>
        Prop(dev, key, DEVPROP_TYPE_STRING) is { } b ? System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0') : null;

    static string[] ReadStrings(uint dev, DEVPROPKEY key) =>
        Prop(dev, key, DEVPROP_TYPE_STRING_LIST) is { } b
            ? System.Text.Encoding.Unicode.GetString(b).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            : [];

    const int CR_BUFFER_SMALL = 0x1A;
    const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1, CM_GETIDLIST_FILTER_PRESENT = 0x100;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_List_SizeW(out uint len, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_ListW(string? filter, char[] buffer, uint len, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint dev, string id, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint dev, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);
}
