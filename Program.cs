using System.Reflection;
using System.Runtime.Loader;
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
        // Dependency assemblies (Vortice, NAudio, WinRT projections) live in bin\ next to the exe in
        // normal builds — resolve them from there. Registered before anything touches those types;
        // a single-file publish bundles them and never raises this event.
        AssemblyLoadContext.Default.Resolving += ResolveFromBin;

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

    private static Assembly? ResolveFromBin(AssemblyLoadContext context, AssemblyName name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "bin", name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}
