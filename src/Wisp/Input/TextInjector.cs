using System.Windows;
using Wisp.Core;
using Wisp.Native;

namespace Wisp.Input;

/// <summary>Puts text into whatever app has focus.</summary>
public static class TextInjector
{
    /// <summary>
    /// Paste via the clipboard (fast, works with nearly every app, keeps formatting sane),
    /// then restore whatever the user had on the clipboard before.
    /// Must be called on the UI (STA) thread.
    /// </summary>
    public static async Task PasteAsync(string text, IntPtr target = default)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (target != IntPtr.Zero && Win32.GetForegroundWindow() != target)
            Win32.ForceForeground(target);

        IDataObject? previous = null;
        try { previous = Clipboard.GetDataObject(); } catch { }

        ClipboardWatcher.SuppressNext = true;
        if (!TrySetClipboard(text))
        {
            // Clipboard locked by another app: fall back to typing it.
            TypeText(text);
            return;
        }

        // Make sure no physical modifiers are still held from the hotkey, then Ctrl+V.
        await WaitForModifiersReleasedAsync();
        Win32.Send(
            Win32.Key(VK.CONTROL, false), Win32.Key(VK.V, false),
            Win32.Key(VK.V, true), Win32.Key(VK.CONTROL, true));

        // Give the target app time to read the clipboard before restoring it.
        await Task.Delay(350);
        if (previous is not null)
        {
            ClipboardWatcher.SuppressNext = true;
            try { Clipboard.SetDataObject(previous, true); } catch { }
        }
    }

    /// <summary>Types text as Unicode keystrokes. Slower, but works where paste is blocked.</summary>
    public static void TypeText(string text)
    {
        var inputs = new List<Win32.INPUT>(text.Length * 2);
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n')
            {
                inputs.Add(Win32.Key(VK.RETURN, false));
                inputs.Add(Win32.Key(VK.RETURN, true));
            }
            else
            {
                inputs.Add(Win32.Unicode(ch, false));
                inputs.Add(Win32.Unicode(ch, true));
            }
        }
        foreach (var chunk in inputs.Chunk(64)) Win32.Send(chunk);
    }

    static bool TrySetClipboard(string text)
    {
        for (int i = 0; i < 8; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(25); }
        }
        Log.Warn("Clipboard busy; falling back to typing");
        return false;
    }

    public static async Task WaitForModifiersReleasedAsync(int timeoutMs = 1500)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until &&
               (Win32.IsDown(VK.CONTROL) || Win32.IsDown(VK.SHIFT) || Win32.IsDown(VK.MENU) ||
                Win32.IsDown(VK.LWIN) || Win32.IsDown(VK.RWIN)))
            await Task.Delay(15);
    }
}
