using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Threading;
using Wisp.Core;
using Forms = System.Windows.Forms;

namespace Wisp;

public partial class App : Application
{
    Mutex? _single;
    WispController? _controller;
    Forms.NotifyIcon? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "Wisp.exe --quit" asks the running instance to exit cleanly (restoring the taskbar).
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Wisp.Quit");
        if (e.Args.Contains("--quit")) { quit.Set(); Shutdown(); return; }

        if (!e.Args.Contains("--snapshot"))
        {
            _single = new Mutex(true, @"Local\Wisp.SingleInstance", out bool first);
            if (!first) { Shutdown(); return; }
            new Thread(() => { quit.WaitOne(); Dispatcher.BeginInvoke(Shutdown); }) { IsBackground = true, Name = "Wisp quit listener" }.Start();
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled exception", ex.ExceptionObject as Exception);
            // About to die: never leave the user without a taskbar.
            if (Desktop.ShellChrome.TaskbarHidden) Desktop.ShellChrome.ShowTaskbar();
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        Log.Info("Wisp starting");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _controller = new WispController { SnapshotMode = e.Args.Contains("--snapshot") };
        Log.Info($"Controller built in {sw.ElapsedMilliseconds} ms");
        CreateTray();
        Log.Info($"Tray ready at {sw.ElapsedMilliseconds} ms");
        try
        {
            await _controller.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            Desktop.ShellChrome.ShowTaskbar();
            System.Windows.MessageBox.Show($"Wisp couldn't start:\n\n{ex.Message}\n\nDetails are in the log.", "Wisp");
            Shutdown();
            return;
        }
        Log.Info($"Started in {sw.ElapsedMilliseconds} ms");

        int snap = Array.IndexOf(e.Args, "--snapshot");
        if (snap >= 0 && snap + 2 < e.Args.Length)
        {
            try { await UI.Snapshot.RunAsync(_controller, e.Args[snap + 1], e.Args[snap + 2], snap + 3 < e.Args.Length ? e.Args[snap + 3] : null); }
            catch (Exception ex) { Log.Error("Snapshot failed", ex); }
            Shutdown();
            return;
        }

        // "Wisp.exe --open controls" opens the panel on a tab (handy for shortcuts and testing).
        int i = Array.IndexOf(e.Args, "--open");
        if (i >= 0) _controller.Island.OpenPanel(i + 1 < e.Args.Length ? e.Args[i + 1] : "home");
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Wisp", null, (_, _) => _controller?.Island.OpenPanel());
        menu.Items.Add("Settings", null, (_, _) => _controller?.ShowSettings());
        menu.Items.Add("Restore Windows desktop", null, (_, _) => _controller?.Desktop.SetAll(false));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Wisp",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, ev) => { if (ev.Button == Forms.MouseButtons.Left) _controller?.Island.OpenPanel(); };
    }

    /// <summary>Draws the tray icon (a glowing orb) so there's no binary asset to ship.</summary>
    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var glow = new GraphicsPath();
            glow.AddEllipse(2, 2, 28, 28);
            using var pgb = new PathGradientBrush(glow)
            {
                CenterColor = Color.FromArgb(255, 255, 214, 190),
                SurroundColors = [Color.FromArgb(255, 240, 112, 58)],
            };
            g.FillEllipse(pgb, 4, 4, 24, 24);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Wisp exiting");
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _controller?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
