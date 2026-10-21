# Removes a stuck or partial Wisp install so a fresh install works.
#
# Use when uninstalling left %LOCALAPPDATA%\Wisp behind ("in use"), or Setup says Wisp is already
# installed and Repair doesn't help.
#
#   powershell -ExecutionPolicy Bypass -File clean-uninstall.ps1              # keep settings
#   powershell -ExecutionPolicy Bypass -File clean-uninstall.ps1 -RemoveData  # also delete settings, speech model, history
param([switch]$RemoveData)

$ErrorActionPreference = 'Continue'
$install = Join-Path $env:LOCALAPPDATA 'Wisp'
$data = Join-Path $env:APPDATA 'Wisp'

function Step($text) { Write-Host "-> $text" -ForegroundColor Cyan }

# 1. Close anything running from the install folder. Ask Wisp to quit first so it restores the
#    taskbar itself; force-stop only what's left.
Step 'Closing Wisp'
$running = @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($install, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) {
    $exe = Join-Path $install 'current\Wisp.exe'
    if (Test-Path $exe) { Start-Process $exe -ArgumentList '--quit' -ErrorAction SilentlyContinue }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and @($running | Where-Object { -not $_.HasExited }).Count -gt 0) { Start-Sleep -Milliseconds 300 }
    $left = @($running | Where-Object { -not $_.HasExited })
    foreach ($p in $left) { Write-Host "   force-stopping $($p.Name) ($($p.Id))"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
} else { Write-Host '   nothing running' }

# 2. Put the Windows taskbar, desktop icons and tray icons back (in case Wisp had hidden them).
Step 'Restoring the Windows desktop'
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class WispClean {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct ABD { public int cb; public IntPtr h; public uint cbm, edge; public RECT rc; public IntPtr lp; }
  [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint m, ref ABD d);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
  delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EP cb, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string s);
  [DllImport("user32.dll")] static extern bool SendNotifyMessage(IntPtr h, uint m, UIntPtr w, IntPtr l);
  public static int Run() {
    int n = 0;
    EnumWindows((h, l) => {
      var sb = new StringBuilder(64); GetClassName(h, sb, 64); var cls = sb.ToString();
      if (cls == "Progman" || cls == "WorkerW") {
        var dv = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (dv != IntPtr.Zero) { var lv = FindWindowEx(dv, IntPtr.Zero, "SysListView32", null); if (lv != IntPtr.Zero) ShowWindow(lv, 5); }
      }
      if (cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd") return true;
      uint pid; GetWindowThreadProcessId(h, out pid);
      try { if (System.Diagnostics.Process.GetProcessById((int)pid).ProcessName != "explorer") return true; } catch { return true; }
      if (cls == "Shell_TrayWnd") { var d = new ABD { cb = Marshal.SizeOf(typeof(ABD)), h = h, lp = (IntPtr)2 }; SHAppBarMessage(10, ref d); }
      ShowWindow(h, 5); n++; return true;
    }, IntPtr.Zero);
    SendNotifyMessage((IntPtr)0xFFFF, RegisterWindowMessage("TaskbarCreated"), UIntPtr.Zero, IntPtr.Zero);
    return n;
  }
}
"@
Write-Host "   restored $([WispClean]::Run()) taskbar(s)"

# 3. Delete the install folder.
Step "Deleting $install"
if (Test-Path $install) {
    Remove-Item $install -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $install) {
        $locked = Get-ChildItem $install -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
            try { [IO.File]::Open($_.FullName, 'Open', 'ReadWrite', 'None').Close(); $false } catch { $true } } | Select-Object -First 5
        Write-Host "   Some files are still in use:" -ForegroundColor Yellow
        $locked | ForEach-Object { Write-Host "     $($_.FullName)" }
        Write-Host "   Sign out of Windows (or restart), then run this script again." -ForegroundColor Yellow
    } else { Write-Host '   deleted' }
} else { Write-Host '   already gone' }

# 4. Remove the "installed" record, startup entry and shortcuts, so Setup sees a clean PC.
Step 'Removing install records and shortcuts'
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Wisp' -Recurse -Force -ErrorAction SilentlyContinue
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Wisp' -ErrorAction SilentlyContinue
foreach ($lnk in @(
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Wisp.lnk'),
    (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Wisp.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Wisp.lnk'))) {
    if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "   removed $lnk" }
}

# 5. Settings and downloads (only if asked).
if ($RemoveData) {
    Step "Deleting settings and data in $data"
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host ($(if (Test-Path $data) { '   could not delete everything' } else { '   deleted' }))
} else {
    Write-Host "-> Kept your settings in $data (run with -RemoveData to delete them)" -ForegroundColor DarkGray
}

if (-not (Test-Path $install)) { Write-Host "`nDone. You can install Wisp again now." -ForegroundColor Green }
