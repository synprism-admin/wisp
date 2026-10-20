using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.Desktop;

/// <summary>
/// What Windows itself uses to tell apps apart on the taskbar: the AppUserModelID, plus the
/// relaunch name/icon/command an app can attach to a window. Chrome, for example, gives each
/// profile's windows their own ID ("Chrome" vs "Chrome.UserData.Profile1"), profile name and avatar.
/// </summary>
public sealed record WindowIdentity(string? AppId, string? DisplayName, string? IconResource, string? RelaunchCommand);

public static class AppIdentity
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    // PROPVARIANT is 24 bytes on x64; declaring it smaller corrupts reads.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint c);
        void GetAt(uint i, out PROPERTYKEY k);
        void GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
        void SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
        void Commit();
    }

    static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    static readonly Guid AppUserModelProps = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    const uint PID_RelaunchCommand = 2, PID_RelaunchIcon = 3, PID_RelaunchName = 4, PID_ID = 5;
    const ushort VT_LPWSTR = 31;

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ps);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bc, int flags, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ps);

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT v);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? id);

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static string? Read(IPropertyStore ps, uint pid)
    {
        var key = new PROPERTYKEY { fmtid = AppUserModelProps, pid = pid };
        ps.GetValue(ref key, out var v);
        try { return v.vt == VT_LPWSTR && v.p != IntPtr.Zero ? Marshal.PtrToStringUni(v.p) : null; }
        finally { PropVariantClear(ref v); }
    }

    static readonly Dictionary<IntPtr, WindowIdentity> WindowCache = [];

    /// <summary>The window's own identity, if its app set one (cached per window).</summary>
    public static WindowIdentity ForWindow(IntPtr hwnd)
    {
        if (WindowCache.TryGetValue(hwnd, out var cached)) return cached;
        var id = new WindowIdentity(null, null, null, null);
        try
        {
            var iid = IID_IPropertyStore;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var ps) == 0)
            {
                try
                {
                    id = new WindowIdentity(
                        Blank(Read(ps, PID_ID)),
                        Blank(Indirect(Read(ps, PID_RelaunchName))),
                        Blank(Read(ps, PID_RelaunchIcon)),
                        Blank(Read(ps, PID_RelaunchCommand)));
                }
                finally { Marshal.ReleaseComObject(ps); }
            }
        }
        catch { }
        if (WindowCache.Count > 1000) WindowCache.Clear();
        WindowCache[hwnd] = id;
        return id;
    }

    /// <summary>Store/packaged apps: the AppUserModelID of the process (matches the Start menu's id).</summary>
    public static string? ForPackagedProcess(uint pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint len = 0;
            if (GetApplicationUserModelId(h, ref len, null) != 122 /* ERROR_INSUFFICIENT_BUFFER */) return null;
            var sb = new StringBuilder((int)len);
            return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>The AppUserModelID baked into a shortcut (.lnk), e.g. a Chrome profile shortcut.</summary>
    public static string? ForShortcut(string path)
    {
        try
        {
            var iid = IID_IPropertyStore;
            if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var ps) != 0) return null;
            try { return Blank(Read(ps, PID_ID)); }
            finally { Marshal.ReleaseComObject(ps); }
        }
        catch { return null; }
    }

    /// <summary>Loads an icon given as "path,index" (an .ico file or an icon inside an exe/dll).</summary>
    public static ImageSource? LoadIconResource(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource)) return null;
        var comma = resource.LastIndexOf(',');
        var path = Environment.ExpandEnvironmentVariables((comma > 0 ? resource[..comma] : resource).Trim('"', ' '));
        int.TryParse(comma > 0 ? resource[(comma + 1)..] : "0", out int index);
        if (!File.Exists(path)) return null;
        if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return ShellIcons.Get(path);
        var large = new IntPtr[1];
        if (ExtractIconEx(path, index, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DestroyIcon(large[0]); }
    }

    static string? Indirect(string? s)
    {
        if (s is null || !s.StartsWith('@')) return s;
        var sb = new StringBuilder(512);
        return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Splits a relaunch command ("\"C:\x\chrome.exe\" --profile-directory=\"Profile 1\"") into exe + arguments.</summary>
    public static (string Exe, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }
        int space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].Trim());
    }
}
