using System.Windows;
using System.Windows.Interop;
using Wisp.Core;
using Wisp.Native;

namespace Wisp.Input;

/// <summary>Records copied text into the clipboard history.</summary>
public sealed class ClipboardWatcher : IDisposable
{
    /// <summary>Set by Wisp before it writes the clipboard itself, so its own pastes aren't recorded.</summary>
    public static bool SuppressNext;

    readonly HwndSource _source;
    readonly Store _store;

    public ClipboardWatcher(Store store)
    {
        _store = store;
        _source = new HwndSource(new HwndSourceParameters("WispClipboard") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
        Win32.AddClipboardFormatListener(_source.Handle);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg != Win32.WM_CLIPBOARDUPDATE) return IntPtr.Zero;
        if (SuppressNext) { SuppressNext = false; return IntPtr.Zero; }
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                Task.Run(() => _store.AddClip(text));
            }
        }
        catch { /* another app is holding the clipboard */ }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Win32.RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }
}
