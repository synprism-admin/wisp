using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Wisp.AI;
using Wisp.Core;
using Wisp.Native;

namespace Wisp.Tools;

/// <summary>
/// Real PC control. The model reads a window's UI Automation tree (the same interface screen
/// readers use) and acts on elements by id. Screenshots + coordinate clicks cover the cases
/// the tree can't see (canvases, games, some web content).
/// </summary>
public sealed class PcControl : IDisposable
{
    readonly UIA3Automation _uia = new();
    readonly Dictionary<int, AutomationElement> _elements = [];
    Rectangle _lastShot;
    double _lastShotScale = 1;

    static readonly HashSet<ControlType> Interesting =
    [
        ControlType.Button, ControlType.Edit, ControlType.MenuItem, ControlType.ListItem, ControlType.TabItem,
        ControlType.Hyperlink, ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox,
        ControlType.TreeItem, ControlType.Document, ControlType.SplitButton, ControlType.DataItem,
        ControlType.Slider, ControlType.Spinner, ControlType.Text, ControlType.Header, ControlType.HeaderItem,
    ];

    public IEnumerable<ITool> Tools()
    {
        yield return FnTool.Sync("list_windows", "List open top-level windows (title and app).", Schema.Empty(),
            (_, _) => ToolOutput.Ok(ListWindows()));

        yield return FnTool.Sync("focus_window",
            "Bring a window to the front by (partial) title or process name.",
            Schema.Object(("query", "string", "Part of the window title or the process name, e.g. 'Chrome' or 'notepad'.", true)),
            (a, _) => FocusWindow(a.Str("query") ?? ""));

        yield return new FnTool("inspect_window",
            "Read the controls of the foreground window (or one matching 'query'). Returns elements with ids for click_element/set_text. Call again after the UI changes.",
            Schema.Object(("query", "string", "Optional window title/process to inspect instead of the foreground window.", false)),
            (a, _, _) => Task.Run(() => Inspect(a.Str("query"))));

        yield return new FnTool("click_element", "Click/activate an element by id from the last inspect_window.",
            Schema.Object(("id", "integer", "Element id.", true)),
            (a, _, _) => Task.Run(() => ClickElement(a.Int("id") ?? -1)));

        yield return new FnTool("set_text", "Replace the text of an input element (by id) with new text.",
            Schema.Object(("id", "integer", "Element id.", true), ("text", "string", "Text to enter.", true)),
            (a, _, _) => Task.Run(() => SetText(a.Int("id") ?? -1, a.Str("text") ?? "")));

        yield return FnTool.Sync("type_text", "Type text into whatever currently has keyboard focus.",
            Schema.Object(("text", "string", "Text to type.", true)),
            (a, _) => { Input.TextInjector.TypeText(a.Str("text") ?? ""); return ToolOutput.Ok("Typed."); });

        yield return new FnTool("press_keys",
            "Press a key or shortcut, e.g. 'enter', 'ctrl+s', 'alt+tab', 'ctrl+shift+t', 'win+d', 'f5', 'down'. Several separated by spaces run in order.",
            Schema.Object(("keys", "string", "Key combo(s).", true)),
            async (a, _, ct) => { await PressKeysAsync(a.Str("keys") ?? "", ct); return ToolOutput.Ok("Pressed."); });

        yield return FnTool.Sync("screenshot",
            "Look at the screen. Returns an image of the foreground window (or the whole screen). Coordinates in it can be used with click_at.",
            Schema.Object(("whole_screen", "boolean", "Capture the whole monitor instead of the foreground window.", false)),
            (a, _) => Screenshot(a.Bool("whole_screen") ?? false));

        yield return new FnTool("click_at",
            "Click at a pixel position in the most recent screenshot image.",
            Schema.Object(("x", "integer", "X in screenshot pixels.", true), ("y", "integer", "Y in screenshot pixels.", true),
                ("double", "boolean", "Double-click.", false), ("right", "boolean", "Right-click.", false)),
            (a, _, _) => Task.Run(() => ClickAt(a.Int("x") ?? 0, a.Int("y") ?? 0, a.Bool("double") ?? false, a.Bool("right") ?? false)));

        yield return FnTool.Sync("scroll", "Scroll the window under the last screenshot position or the foreground window.",
            Schema.Object(("direction", "string", "'up' or 'down'.", true), ("amount", "integer", "Notches, default 5.", false)),
            (a, _) =>
            {
                int amt = (a.Int("amount") ?? 5) * (a.Str("direction") == "up" ? 1 : -1);
                FlaUI.Core.Input.Mouse.Scroll(amt);
                return ToolOutput.Ok("Scrolled.");
            });
    }

    // ---------------------------------------------------------------- windows

    static IEnumerable<(IntPtr hwnd, string title, string process)> TopWindows()
    {
        var list = new List<(IntPtr, string, string)>();
        Win32.EnumWindows((h, _) =>
        {
            if (!Win32.IsWindowVisible(h)) return true;
            var title = Win32.WindowTitle(h);
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (Win32.DwmGetWindowAttribute(h, Win32.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            Win32.GetWindowThreadProcessId(h, out uint pid);
            string proc;
            try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
            if (proc is "Wisp" or "TextInputHost" || title is "Program Manager") return true;
            list.Add((h, title, proc));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static string ListWindows()
    {
        var fg = Win32.GetForegroundWindow();
        var sb = new StringBuilder();
        foreach (var (h, title, proc) in TopWindows())
            sb.AppendLine($"{(h == fg ? "* " : "  ")}{title}  [{proc}]");
        return sb.Length == 0 ? "No windows." : "(* = foreground)\n" + sb;
    }

    static IntPtr FindWindow(string query) =>
        TopWindows().FirstOrDefault(w =>
            w.title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            w.process.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            w.process.Contains(query, StringComparison.OrdinalIgnoreCase)).hwnd;

    static ToolOutput FocusWindow(string query)
    {
        var h = FindWindow(query);
        if (h == IntPtr.Zero) return ToolOutput.Fail($"No window matching '{query}'. Use list_windows.");
        Win32.ForceForeground(h);
        return ToolOutput.Ok($"Focused: {Win32.WindowTitle(h)}");
    }

    // ---------------------------------------------------------------- UI Automation

    ToolOutput Inspect(string? query)
    {
        var hwnd = string.IsNullOrWhiteSpace(query) ? Win32.GetForegroundWindow() : FindWindow(query);
        if (hwnd == IntPtr.Zero) return ToolOutput.Fail("No such window.");

        var root = _uia.FromHandle(hwnd);
        _elements.Clear();
        var sb = new StringBuilder();
        sb.AppendLine($"Window: {Win32.WindowTitle(hwnd)}");
        int next = 1;
        var walker = _uia.TreeWalkerFactory.GetControlViewWalker();

        void Walk(AutomationElement el, int depth)
        {
            if (next > 300 || depth > 25) return;
            AutomationElement? child;
            try { child = walker.GetFirstChild(el); } catch { return; }
            while (child is not null && next <= 300)
            {
                try
                {
                    if (!child.Properties.IsOffscreen.ValueOrDefault)
                    {
                        var type = child.Properties.ControlType.ValueOrDefault;
                        var name = Clean(child.Properties.Name.ValueOrDefault);
                        if (Interesting.Contains(type) && (name.Length > 0 || type is ControlType.Edit or ControlType.Document or ControlType.ComboBox))
                        {
                            if (type != ControlType.Text || name.Length > 1)
                            {
                                int id = next++;
                                _elements[id] = child;
                                var line = new StringBuilder($"{new string(' ', Math.Min(depth, 8))}[{id}] {type} \"{name}\"");
                                if (type is ControlType.Edit or ControlType.ComboBox &&
                                    child.Patterns.Value.TryGetPattern(out var vp) && vp.Value.ValueOrDefault is { Length: > 0 } v)
                                    line.Append($" = \"{Clean(v, 80)}\"");
                                if (child.Patterns.Toggle.TryGetPattern(out var tp))
                                    line.Append($" ({tp.ToggleState.ValueOrDefault})");
                                if (!child.Properties.IsEnabled.ValueOrDefault) line.Append(" (disabled)");
                                sb.AppendLine(line.ToString());
                            }
                        }
                    }
                    Walk(child, depth + 1);
                }
                catch { /* elements vanish mid-walk; skip */ }
                try { child = walker.GetNextSibling(child); } catch { break; }
            }
        }

        Walk(root, 0);
        if (next > 300) sb.AppendLine("(more elements not shown; use screenshot or narrow down)");
        if (next == 1) sb.AppendLine("(no accessible controls; use screenshot + click_at)");
        return ToolOutput.Ok(sb.ToString());
    }

    static string Clean(string? s, int max = 120)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    ToolOutput ClickElement(int id)
    {
        if (!_elements.TryGetValue(id, out var el)) return ToolOutput.Fail($"No element {id}. Call inspect_window again.");
        try
        {
            if (el.Patterns.Invoke.TryGetPattern(out var inv)) inv.Invoke();
            else if (el.Patterns.Toggle.TryGetPattern(out var tog)) tog.Toggle();
            else if (el.Patterns.SelectionItem.TryGetPattern(out var sel)) sel.Select();
            else if (el.Patterns.ExpandCollapse.TryGetPattern(out var ec)) ec.Expand();
            else el.Click(moveMouse: true);
            return ToolOutput.Ok($"Clicked {Clean(el.Properties.Name.ValueOrDefault)}.");
        }
        catch
        {
            el.Click(moveMouse: true);
            return ToolOutput.Ok("Clicked (mouse).");
        }
    }

    ToolOutput SetText(int id, string text)
    {
        if (!_elements.TryGetValue(id, out var el)) return ToolOutput.Fail($"No element {id}. Call inspect_window again.");
        if (el.Patterns.Value.TryGetPattern(out var vp) && !vp.IsReadOnly.ValueOrDefault)
        {
            vp.SetValue(text);
            return ToolOutput.Ok("Text set.");
        }
        el.Focus();
        Win32.Send(Win32.Key(VK.CONTROL, false), Win32.Key(VK.A, false), Win32.Key(VK.A, true), Win32.Key(VK.CONTROL, true));
        Input.TextInjector.TypeText(text);
        return ToolOutput.Ok("Text typed.");
    }

    // ---------------------------------------------------------------- vision

    ToolOutput Screenshot(bool wholeScreen)
    {
        Rectangle rect;
        var fg = Win32.GetForegroundWindow();
        if (wholeScreen || fg == IntPtr.Zero)
            rect = ScreenCapture.CursorScreenBounds();
        else
        {
            var el = _uia.FromHandle(fg);
            var b = el.BoundingRectangle;
            rect = new Rectangle(b.X, b.Y, b.Width, b.Height);
            rect.Intersect(System.Windows.Forms.SystemInformation.VirtualScreen);
            if (rect.Width < 50 || rect.Height < 50) rect = ScreenCapture.CursorScreenBounds();
        }

        const int maxEdge = 1568;
        _lastShot = rect;
        _lastShotScale = Math.Min(1.0, (double)maxEdge / Math.Max(rect.Width, rect.Height));
        var png = ScreenCapture.Capture(rect, maxEdge);
        return new ToolOutput(
            $"Screenshot of {(wholeScreen ? "the screen" : Win32.WindowTitle(fg))}, {(int)(rect.Width * _lastShotScale)}x{(int)(rect.Height * _lastShotScale)} px.", png);
    }

    ToolOutput ClickAt(int x, int y, bool dbl, bool right)
    {
        if (_lastShot.IsEmpty) return ToolOutput.Fail("Take a screenshot first.");
        var p = new Point(_lastShot.Left + (int)(x / _lastShotScale), _lastShot.Top + (int)(y / _lastShotScale));
        FlaUI.Core.Input.Mouse.MoveTo(p);
        if (right) FlaUI.Core.Input.Mouse.RightClick();
        else if (dbl) FlaUI.Core.Input.Mouse.DoubleClick();
        else FlaUI.Core.Input.Mouse.LeftClick();
        return ToolOutput.Ok($"Clicked at {x},{y}.");
    }

    // ---------------------------------------------------------------- keys

    public static async Task PressKeysAsync(string spec, CancellationToken ct)
    {
        foreach (var combo in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var keys = combo.Split('+').Select(k => KeyCode(k.Trim().ToLowerInvariant())).ToList();
            if (keys.Any(k => k == 0)) throw new ArgumentException($"Unknown key in '{combo}'.");
            var inputs = new List<Win32.INPUT>();
            foreach (var k in keys) inputs.Add(Win32.Key((ushort)k, false, IsExtended(k)));
            foreach (var k in Enumerable.Reverse(keys)) inputs.Add(Win32.Key((ushort)k, true, IsExtended(k)));
            Win32.Send(inputs.ToArray());
            await Task.Delay(60, ct);
        }
    }

    static bool IsExtended(int vk) => vk is >= 0x21 and <= 0x2E or VK.LWIN or VK.RWIN;

    static int KeyCode(string k) => k switch
    {
        "ctrl" or "control" => VK.CONTROL,
        "shift" => VK.SHIFT,
        "alt" => VK.MENU,
        "win" or "windows" or "super" => VK.LWIN,
        "enter" or "return" => VK.RETURN,
        "esc" or "escape" => VK.ESCAPE,
        "tab" => VK.TAB,
        "space" => VK.SPACE,
        "backspace" => VK.BACK,
        "delete" or "del" => VK.DELETE,
        "insert" => 0x2D,
        "home" => 0x24, "end" => 0x23, "pageup" or "pgup" => 0x21, "pagedown" or "pgdn" => 0x22,
        "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28,
        "printscreen" => 0x2C,
        "playpause" => VK.MEDIA_PLAY_PAUSE, "nexttrack" => VK.MEDIA_NEXT, "prevtrack" => VK.MEDIA_PREV,
        "volumeup" => VK.VOLUME_UP, "volumedown" => VK.VOLUME_DOWN, "mute" => VK.VOLUME_MUTE,
        _ when k.Length == 1 && char.IsLetterOrDigit(k[0]) => char.ToUpperInvariant(k[0]),
        _ when k.Length is 2 or 3 && k[0] == 'f' && int.TryParse(k[1..], out var n) && n is >= 1 and <= 24 => 0x6F + n,
        "-" or "minus" => 0xBD, "=" or "plus" => 0xBB, "," => 0xBC, "." => 0xBE, "/" => 0xBF,
        _ => 0,
    };

    public void Dispose() => _uia.Dispose();
}
