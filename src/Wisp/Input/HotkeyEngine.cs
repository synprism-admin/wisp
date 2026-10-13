using System.Runtime.InteropServices;
using System.Windows.Threading;
using Wisp.Core;
using Wisp.Native;

namespace Wisp.Input;

[Flags]
public enum Mods { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Win = 8 }

public enum HoldAction { Dictate, Talk, Circle }

public sealed record Chord(HoldAction Action, Mods Mods, int ArmDelayMs);

/// <summary>
/// Detects "hold a modifier chord" gestures (e.g. hold Ctrl+Win) with a low-level keyboard hook.
///
/// A chord fires only when exactly its modifiers are held and no other key is pressed during the
/// hold, so normal shortcuts like Ctrl+Win+D or Ctrl+Shift+T keep working. The hook runs on its own
/// thread with its own message loop, so a busy UI thread can never make Windows drop it.
/// </summary>
public sealed class HotkeyEngine : IDisposable
{
    readonly Dispatcher _ui;
    readonly object _gate = new();
    readonly Thread _thread;
    uint _threadId;
    IntPtr _hook;
    Win32.LowLevelProc? _proc; // kept alive so the GC can't collect the delegate

    Mods _held;
    bool _otherKeyDuringHold;
    Chord? _armed, _active;
    Timer? _armTimer;
    long _armToken;

    public IReadOnlyList<Chord> Chords { get; set; } =
    [
        new(HoldAction.Dictate, Mods.Ctrl | Mods.Win, 120),
        new(HoldAction.Talk, Mods.Alt | Mods.Win, 120),
        new(HoldAction.Circle, Mods.Ctrl | Mods.Shift, 260),
    ];

    public event Action<HoldAction>? HoldStarted;
    public event Action<HoldAction>? HoldEnded;
    public event Action<HoldAction>? HoldCancelled;

    /// <summary>Win pressed and released on its own. When enabled, Start is suppressed and this fires instead.</summary>
    public event Action? WinTapped;
    public bool InterceptWinTap { get; set; }
    bool _winClean;

    /// <summary>Set while some UI wants Escape (e.g. the circle overlay); Escape then cancels the hold.</summary>
    public bool IsHolding { get { lock (_gate) return _active is not null; } }

    public HotkeyEngine(Dispatcher ui)
    {
        _ui = ui;
        _thread = new Thread(Run) { IsBackground = true, Name = "Wisp keyboard hook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        _proc = HookProc;
        _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Error($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        Log.Info("Keyboard hook installed");
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
        Win32.UnhookWindowsHookEx(_hook);
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        var k = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
        if (k.dwExtraInfo == Win32.WispInputTag) return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool down = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
        int vk = (int)k.vkCode;
        bool swallow = false;

        lock (_gate)
        {
            var mod = ModOf(vk);
            if (mod == Mods.Win && down && (_held & ~Mods.Win) == Mods.None && (_held & Mods.Win) == 0) _winClean = true;
            else if (down && mod != Mods.Win) _winClean = false;

            if (mod == Mods.Win && !down && _winClean && InterceptWinTap && _active is null && (_held & ~Mods.Win) == Mods.None)
            {
                // Swallow this Win-up, then replay it after an unassigned key so Windows sees a
                // "combo" and doesn't open Start.
                _winClean = false;
                _held &= ~Mods.Win;
                Disarm();
                Win32.Send(Win32.Key(VK.MASK, false), Win32.Key(VK.MASK, true), Win32.Key((ushort)vk, true, true));
                if (WinTapped is { } tap) _ui.BeginInvoke(tap);
                return new IntPtr(1);
            }

            if (mod != Mods.None)
            {
                if (down)
                {
                    if ((_held & mod) == 0) { _held |= mod; OnModsChanged(); }
                }
                else
                {
                    _held &= ~mod;
                    OnModsChanged();
                    if (_held == Mods.None) _otherKeyDuringHold = false;
                }
            }
            else if (down)
            {
                if (_active is not null && vk == VK.ESCAPE)
                {
                    var c = _active; _active = null;
                    Raise(HoldCancelled, c.Action);
                    swallow = true;
                }
                else
                {
                    _otherKeyDuringHold = true;
                    Disarm();
                    if (_active is not null)
                    {
                        // A real shortcut like Ctrl+Win+Right: drop the gesture, let the key through.
                        var c = _active; _active = null;
                        Raise(HoldCancelled, c.Action);
                    }
                }
            }
        }

        return swallow ? new IntPtr(1) : Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void OnModsChanged()
    {
        if (_active is not null)
        {
            // Ends as soon as any of the chord's modifiers is released; extra modifiers are tolerated.
            if ((_held & _active.Mods) != _active.Mods)
            {
                var c = _active; _active = null;
                Raise(HoldEnded, c.Action);
            }
            return;
        }

        Disarm();
        if (_otherKeyDuringHold) return;
        var chord = Chords.FirstOrDefault(c => c.Mods == _held);
        if (chord is null) return;

        _armed = chord;
        long token = ++_armToken;
        _armTimer = new Timer(_ => Fire(token), null, chord.ArmDelayMs, Timeout.Infinite);
    }

    void Fire(long token)
    {
        Chord? chord;
        lock (_gate)
        {
            if (token != _armToken || _armed is null || _held != _armed.Mods || _otherKeyDuringHold) return;
            chord = _armed;
            _armed = null;
            _active = chord;
            _winClean = false;
        }
        // Tap an unassigned key while Win/Alt are down: Windows then treats the hold as a
        // "combo", so releasing them won't pop the Start menu or focus an app's menu bar.
        if ((chord.Mods & (Mods.Win | Mods.Alt)) != 0)
            Win32.Send(Win32.Key(VK.MASK, false), Win32.Key(VK.MASK, true));
        Raise(HoldStarted, chord.Action);
    }

    void Disarm()
    {
        _armed = null;
        _armToken++;
        _armTimer?.Dispose();
        _armTimer = null;
    }

    /// <summary>Lets the UI end an active hold (e.g. the overlay was closed by mouse).</summary>
    public void CancelActive()
    {
        lock (_gate)
        {
            if (_active is null) return;
            var c = _active; _active = null;
            Raise(HoldCancelled, c.Action);
        }
    }

    void Raise(Action<HoldAction>? evt, HoldAction a)
    {
        if (evt is null) return;
        _ui.BeginInvoke(() =>
        {
            try { evt(a); } catch (Exception ex) { Log.Error($"Hotkey handler for {a} failed", ex); }
        });
    }

    static Mods ModOf(int vk) =>
        VK.IsCtrl(vk) ? Mods.Ctrl :
        VK.IsShift(vk) ? Mods.Shift :
        VK.IsAlt(vk) ? Mods.Alt :
        VK.IsWin(vk) ? Mods.Win : Mods.None;

    public void Dispose()
    {
        _armTimer?.Dispose();
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
