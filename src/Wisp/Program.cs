using Velopack;
using Wisp.Desktop;

namespace Wisp;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Installer hooks run first, before any UI: Velopack invokes the exe with special arguments
        // during install/update/uninstall, handles them here, and exits.
        VelopackApp.Build()
            .OnBeforeUpdateFastCallback(_ => SystemRestore.QuitRunningInstance())
            .OnBeforeUninstallFastCallback(_ => SystemRestore.UndoEverything())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
