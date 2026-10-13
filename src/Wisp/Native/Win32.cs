using System.Runtime.InteropServices;

namespace Wisp.Native;

internal static partial class Win32
{
    // ---- keyboard hook ----
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const int LLKHF_INJECTED = 0x10;

    public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ---- message loop (hook thread) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y;
    }

    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    public const uint WM_QUIT = 0x0012;

    // ---- SendInput ----
    public const int INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_EXTENDEDKEY = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public int type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] inputs, int size);

    /// <summary>Marks input Wisp injects so its own hook ignores it.</summary>
    public static readonly IntPtr WispInputTag = new(0x57495350); // "WISP"

    public static INPUT Key(ushort vk, bool up, bool extended = false) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0),
                dwExtraInfo = WispInputTag,
            }
        }
    };

    public static INPUT Unicode(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wScan = ch,
                dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0),
                dwExtraInfo = WispInputTag,
            }
        }
    };

    public static void Send(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // ---- windows ----
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

    public static void SetExStyle(IntPtr hwnd, int add, int remove = 0)
    {
        var style = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        style = (style | (uint)add) & ~(long)(uint)remove;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    public const int SW_RESTORE = 9;

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    public const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOZORDER = 0x4;

    public static string WindowTitle(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len == 0) return "";
        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Reliably bring a window to the front, working around the foreground lock.</summary>
    public static void ForceForeground(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        var fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        if (fgThread != me) AttachThreadInput(me, fgThread, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (fgThread != me) AttachThreadInput(me, fgThread, false);
    }

    // ---- clipboard listener ----
    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr h);
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    // ---- power ----
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] public static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);

    // ---- wallpaper ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(uint action, uint param, string? pv, uint winIni);
    public const uint SPI_SETDESKWALLPAPER = 0x14, SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;

    // ---- cursor ----
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
}

/// <summary>Virtual-key codes used across Wisp.</summary>
internal static class VK
{
    public const int SHIFT = 0x10, CONTROL = 0x11, MENU = 0x12;
    public const int LSHIFT = 0xA0, RSHIFT = 0xA1, LCONTROL = 0xA2, RCONTROL = 0xA3, LMENU = 0xA4, RMENU = 0xA5;
    public const int LWIN = 0x5B, RWIN = 0x5C;
    public const int ESCAPE = 0x1B, RETURN = 0x0D, TAB = 0x09, BACK = 0x08, DELETE = 0x2E, SPACE = 0x20;
    public const int V = 0x56, C = 0x43, A = 0x41;
    public const int MEDIA_NEXT = 0xB0, MEDIA_PREV = 0xB1, MEDIA_STOP = 0xB2, MEDIA_PLAY_PAUSE = 0xB3;
    public const int VOLUME_MUTE = 0xAD, VOLUME_DOWN = 0xAE, VOLUME_UP = 0xAF;

    /// <summary>Unassigned VK; injected between Win down/up so the Start menu doesn't open.</summary>
    public const int MASK = 0xE8;

    public static bool IsCtrl(int vk) => vk is CONTROL or LCONTROL or RCONTROL;
    public static bool IsShift(int vk) => vk is SHIFT or LSHIFT or RSHIFT;
    public static bool IsAlt(int vk) => vk is MENU or LMENU or RMENU;
    public static bool IsWin(int vk) => vk is LWIN or RWIN;
    public static bool IsModifier(int vk) => IsCtrl(vk) || IsShift(vk) || IsAlt(vk) || IsWin(vk);
}
