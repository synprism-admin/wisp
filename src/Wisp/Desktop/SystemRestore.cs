using System.Diagnostics;
using Microsoft.Win32;
using Wisp.Core;

namespace Wisp.Desktop;

/// <summary>
/// Puts Windows back the way it was. Used by the uninstaller (and before updates), so removing
/// Wisp can never leave someone without a taskbar or stuck in Wisp's theme.
/// </summary>
public static class SystemRestore
{
    /// <summary>Asks a running Wisp to exit cleanly (it restores the taskbar itself); kills it as a last resort.</summary>
    public static void QuitRunningInstance()
    {
        var others = Process.GetProcessesByName("Wisp").Where(p => p.Id != Environment.ProcessId).ToList();
        if (others.Count == 0) return;

        if (EventWaitHandle.TryOpenExisting(@"Local\Wisp.Quit", out var quit))
            using (quit) quit.Set();

        foreach (var p in others)
        {
            // Velopack gives install hooks about 30 seconds in total; leave room for the rest.
            try { if (!p.WaitForExit(5000)) p.Kill(entireProcessTree: true); p.WaitForExit(2000); } catch { }
        }
    }

    /// <summary>Everything the uninstaller needs: quit Wisp, then undo every system change it made.</summary>
    public static void UndoEverything()
    {
        Log.Info("Uninstall: restoring Windows");
        QuitRunningInstance();

        // Belt and braces: even if Wisp had been killed earlier, bring the taskbar and icons back.
        ShellChrome.ShowTaskbar();
        ShellChrome.SetDesktopIconsVisible(true);

        var settings = Settings.Load();
        if (settings.Desktop.OriginalTheme is { } original)
        {
            WindowsTheme.Restore(original, waitForBroadcast: true);
            settings.Desktop.OriginalTheme = null;
            settings.Desktop.ThemeWindows = false;
            settings.Save();
        }

        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            run?.DeleteValue("Wisp", throwOnMissingValue: false);

        // Apps re-register their tray icons with Explorer.
        Native.Win32.SendNotifyMessage(new IntPtr(0xFFFF), ShellChrome.TaskbarCreatedMessage, UIntPtr.Zero, IntPtr.Zero);
        Log.Info("Uninstall: Windows restored");
    }
}
