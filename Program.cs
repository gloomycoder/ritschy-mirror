using System.Threading;
using System.Windows.Forms;

namespace RitschyMirror;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // Named objects for single instance (mutex) + "show settings" signal to the running instance.
    internal const string ShowSettingsEvent = "RitschyMirror.ShowSettings.Event";
    private const string MutexName = "RitschyMirror.SingleInstance.Mutex";

    [STAThread]
    private static void Main()
    {
        // Single instance: if a copy is already running, we only ask it to open the settings
        // and exit. Prevents duplicate tray icons + agent port conflicts.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(ShowSettingsEvent).Set(); } catch { }
            return;
        }

        // Per-Monitor-V2 programmatically (more reliable than a manifest on .NET) —
        // otherwise fullscreen coordinates/sizes get distorted by Windows scaling.
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext(Environment.GetCommandLineArgs()));
        GC.KeepAlive(mutex);  // keep the mutex alive until app exit
    }
}
