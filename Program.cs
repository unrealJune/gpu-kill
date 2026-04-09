using System.Threading;
using System.Windows.Forms;

namespace GPUKill;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single-instance guard
        using var mutex = new Mutex(true, "GPUKill.SingleInstance.Mutex", out bool created);
        if (!created)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        using var trayApp = new TrayApp();
        Application.Run();
    }
}
