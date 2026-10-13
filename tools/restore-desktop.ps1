# Emergency restore: shows the Windows taskbar on every monitor and asks apps to re-register
# their tray icons with Explorer. Safe to run any time (e.g. if Wisp was killed while the
# taskbar was hidden).
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class WispRestore {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct ABD { public int cb; public IntPtr h; public uint cbm, edge; public RECT rc; public IntPtr lp; }
  [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint m, ref ABD d);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EP cb, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
  [DllImport("user32.dll")] static extern bool SendNotifyMessage(IntPtr h, uint m, UIntPtr w, IntPtr l);

  public static int Run() {
    int n = 0;
    EnumWindows((h, l) => {
      var sb = new StringBuilder(64); GetClassName(h, sb, 64);
      var cls = sb.ToString();
      if (cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd") return true;
      uint pid; GetWindowThreadProcessId(h, out pid);
      try { if (System.Diagnostics.Process.GetProcessById((int)pid).ProcessName != "explorer") return true; } catch { return true; }
      if (cls == "Shell_TrayWnd") {
        var d = new ABD { cb = Marshal.SizeOf(typeof(ABD)), h = h, lp = (IntPtr)2 }; // always-on-top, no auto-hide
        SHAppBarMessage(10, ref d);
      }
      ShowWindow(h, 5); n++;
      return true;
    }, IntPtr.Zero);
    SendNotifyMessage((IntPtr)0xFFFF, RegisterWindowMessage("TaskbarCreated"), UIntPtr.Zero, IntPtr.Zero);
    return n;
  }
}
"@
"Restored $([WispRestore]::Run()) taskbar(s); apps asked to re-add tray icons."
