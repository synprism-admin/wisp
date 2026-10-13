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

        _single = new Mutex(true, @"Local\Wisp.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        Log.Info("Wisp starting");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _controller = new WispController();
        Log.Info($"Controller built in {sw.ElapsedMilliseconds} ms");
        CreateTray();
        Log.Info($"Tray ready at {sw.ElapsedMilliseconds} ms");
        await _controller.StartAsync();
        Log.Info($"Started in {sw.ElapsedMilliseconds} ms");
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Wisp", null, (_, _) => _controller?.Island.OpenPanel());
        menu.Items.Add("Settings", null, (_, _) => _controller?.ShowSettings());
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
