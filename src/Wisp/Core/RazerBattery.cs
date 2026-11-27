using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wisp.Core;

/// <summary>
/// Battery levels of Razer wireless devices on their own USB receiver, which Windows doesn't know.
/// Asks the receiver with Razer's "get battery level" request (command class 0x07, id 0x80, as
/// documented by the OpenRazer project) over a HID feature report. Read-only: nothing on the device
/// is changed. A device that's asleep or off doesn't answer; its last reading is kept, marked not
/// connected.
/// </summary>
static class RazerBattery
{
    const ushort RazerVid = 0x1532;
    const int ReportLength = 91; // report id + Razer's 90-byte report
    static readonly byte[] TransactionIds = [0x1F, 0xFF, 0x3F, 0x9F];

    sealed record Last(BatteryDevice Device, byte Transaction);
    static readonly Dictionary<ushort, Last> Known = [];
    static readonly object Gate = new();

    public static List<BatteryDevice> Scan()
    {
        lock (Gate)
        {
            var answered = new HashSet<ushort>();
            foreach (var path in HidPaths().Where(p => p.Contains("vid_1532", StringComparison.OrdinalIgnoreCase)))
            {
                try { Probe(path, answered); }
                catch (Exception ex) { Log.Warn($"Razer battery query failed on {path}: {ex.Message}"); }
            }
            // Devices that didn't answer this time keep their last reading, as not connected.
            foreach (var (pid, last) in Known.ToList())
                if (!answered.Contains(pid) && last.Device.Connected)
                    Known[pid] = last with { Device = last.Device with { Connected = false } };
            return Known.Values.Select(k => k.Device).ToList();
        }
    }

    static void Probe(string path, HashSet<ushort> answered)
    {
        using var h = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return;
        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attr) || attr.VendorID != RazerVid || answered.Contains(attr.ProductID)) return;
        if (Caps(h) is not { } caps || caps.FeatureReportByteLength != ReportLength) return;

        Known.TryGetValue(attr.ProductID, out var last);
        var order = last is null ? TransactionIds : [last.Transaction, .. TransactionIds.Where(t => t != last.Transaction)];
        foreach (var tid in order)
        {
            if (Query(h, tid, 0x80) is not { } level) continue;
            bool charging = Query(h, tid, 0x84) is 1;
            var name = Product(h) ?? last?.Device.Name ?? "Razer device";
            int pct = (int)Math.Round(level * 100 / 255.0);
            var device = new BatteryDevice(name, KindOf(name, caps), pct, true, DateTime.Now) { Charging = charging };
            if (last is null) Log.Info($"Razer battery: {name} {pct}% (receiver {attr.ProductID:X4})");
            Known[attr.ProductID] = new Last(device, tid);
            answered.Add(attr.ProductID);
            return;
        }
    }

    /// <summary>Sends one class-0x07 request; returns the first value byte, or null if it didn't answer.</summary>
    static byte? Query(SafeFileHandle h, byte transaction, byte command)
    {
        var req = new byte[ReportLength];
        // req[0] is the report id (0); the Razer report follows.
        req[1 + 1] = transaction;
        req[1 + 5] = 0x02;       // data size
        req[1 + 6] = 0x07;       // command class: power
        req[1 + 7] = command;    // 0x80 battery level, 0x84 charging
        byte crc = 0;
        for (int i = 2; i < 88; i++) crc ^= req[1 + i];
        req[1 + 88] = crc;
        if (!HidD_SetFeature(h, req, req.Length)) return null;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            Thread.Sleep(attempt == 0 ? 40 : 60);
            var res = new byte[ReportLength];
            if (!HidD_GetFeature(h, res, res.Length)) return null;
            byte status = res[1];
            if (status == 0x01) continue; // busy: the receiver is still asking the device
            if (status != 0x02 || res[1 + 6] != 0x07 || res[1 + 7] != command) return null;
            return res[1 + 9];
        }
        return null;
    }

    static DeviceKind KindOf(string name, HIDP_CAPS caps)
    {
        string[] keyboards = ["Keyboard", "Type", "Huntsman", "BlackWidow", "Ornata", "Cynosa", "DeathStalker", "Turret"];
        string[] headsets = ["Headset", "Kraken", "Barracuda", "BlackShark", "Nari", "Hammerhead"];
        if (keyboards.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) return DeviceKind.Keyboard;
        if (headsets.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) return DeviceKind.Headphones;
        if (name.Contains("Controller", StringComparison.OrdinalIgnoreCase) || name.Contains("Wolverine", StringComparison.OrdinalIgnoreCase)) return DeviceKind.Controller;
        return DeviceKind.Mouse; // most Razer receivers belong to mice
    }

    static string? Product(SafeFileHandle h)
    {
        var buf = new byte[256];
        if (!HidD_GetProductString(h, buf, buf.Length)) return null;
        var s = System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
        // Receivers often name themselves after the device plus "Receiver"/"Dongle"; the device is what matters.
        foreach (var suffix in new[] { " Receiver", " Dongle", " (Receiver)" })
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) s = s[..^suffix.Length];
        return s.Length == 0 ? null : s;
    }

    static HIDP_CAPS? Caps(SafeFileHandle h)
    {
        if (!HidD_GetPreparsedData(h, out var pp)) return null;
        try { return HidP_GetCaps(pp, out var caps) == HIDP_STATUS_SUCCESS ? caps : null; }
        finally { HidD_FreePreparsedData(pp); }
    }

    static IEnumerable<string> HidPaths()
    {
        HidD_GetHidGuid(out var guid);
        if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, null, 0) != 0 || len <= 1) return [];
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref guid, null, buf, len, 0) != 0) return [];
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    // ------------------------------------------------------------------ interop

    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID, ProductID, VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attr);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid cls, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid cls, string? deviceId, char[] buffer, uint len, uint flags);
}
