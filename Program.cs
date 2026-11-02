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

        // Single instance: prevents duplicate tray icons, two mirrors and agent port conflicts. A second
        // start says so and exits; the "Settings" shortcut (--settings) instead opens the running
        // copy's settings window, without a message.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            bool settingsShortcut = Array.Exists(Environment.GetCommandLineArgs(),
                a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));
            if (settingsShortcut)
                try { EventWaitHandle.OpenExisting(ShowSettingsEvent).Set(); } catch { }
            else
                ShowAlreadyRunning();
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

    /// <summary>"Already running" notice in the user's UI language (built-in English if the packs
    /// or the settings can't be read).</summary>
    private static void ShowAlreadyRunning()
    {
        try { L.Init(AppPaths.Base, AppSettings.Load(AppPaths.AppSettings).Language); }
        catch (Exception) { /* keep the built-in English text */ }
        Application.EnableVisualStyles();
        MessageBox.Show(L.T("app.already_running"), "RitschyMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static Assembly? ResolveFromBin(AssemblyLoadContext context, AssemblyName name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "bin", name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}
