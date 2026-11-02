using System.Runtime.InteropServices;
using System.Text;

namespace RitschyMirror;

/// <summary>A capturable top-level window (for GUI/dock picker + agent /windows).</summary>
public sealed class WindowInfo
{
    public IntPtr Hwnd { get; set; }
    public string Title { get; set; } = "";
    public string Exe { get; set; } = "";   // process exe (e.g. "eldenring.exe") = stable identity
    public int Pid { get; set; }

    /// <summary>Display name for dropdowns: "Title  (exe)".</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Exe) ? Title : $"{Title}  ({Exe})";
}

/// <summary>
/// List top-level windows (for the window picker) and resolve a saved selection
/// identity-stably against the CURRENTLY open windows.
///
/// Identity = process exe (primary, survives title changes like "Game — Level 2") + window title
/// (secondary, to tell apart several windows of the same exe). HWNDs are NOT stable across
/// program starts → so we remember exe+title, not the handle (same philosophy as the
/// replug-proof monitor selection in <see cref="MirrorEngine.ResolveSelection"/>).
/// </summary>
public static class WindowEnum
{
    /// <summary>Visible, "real" top-level windows (with a title, no tool/ghost windows,
    /// not our own process). Sorted by title for a calm dropdown list.</summary>
    public static List<WindowInfo> List()
    {
        var result = new List<WindowInfo>();
        uint ownPid = (uint)Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsCapturable(hWnd, ownPid, out string title, out int pid, out string exe))
                return true; // continue

            result.Add(new WindowInfo { Hwnd = hWnd, Title = title, Pid = pid, Exe = exe });
            return true;
        }, IntPtr.Zero);

        result.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Resolve saved (exe, title) against the currently open windows.
    /// Order: same exe + exact title → same exe + title prefix → sole exe match
    /// → exact title (no exe info) → nothing. Returns (hwnd, "") on a match, otherwise (Zero, plain-text error).
    /// </summary>
    public static (IntPtr hwnd, string error) Resolve(string exe, string title)
    {
        var wins = List();
        if (wins.Count == 0) return (IntPtr.Zero, "Keine aufnehmbaren Fenster gefunden");

        bool hasExe = !string.IsNullOrWhiteSpace(exe);
        bool hasTitle = !string.IsNullOrWhiteSpace(title);
        if (!hasExe && !hasTitle)
            return (IntPtr.Zero, "Kein Fenster ausgewählt");

        if (hasExe)
        {
            var sameExe = wins.Where(w => string.Equals(w.Exe, exe, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameExe.Count == 1) return (sameExe[0].Hwnd, "");
            if (sameExe.Count > 1)
            {
                var exact = sameExe.FirstOrDefault(w => string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return (exact.Hwnd, "");
                var prefix = sameExe.FirstOrDefault(w =>
                    hasTitle && w.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase));
                if (prefix != null) return (prefix.Hwnd, "");
                return (sameExe[0].Hwnd, ""); // several with the same exe, no title match → first
            }
        }
        if (hasTitle)
        {
            var byTitle = wins.FirstOrDefault(w => string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase));
            if (byTitle != null) return (byTitle.Hwnd, "");
        }

        string what = hasTitle ? title : exe;
        return (IntPtr.Zero, $"Fenster '{what}' nicht gefunden (läuft die Anwendung?)");
    }

    // ── Filter: is this a "real", capturable app window? ───────────────────
    private static bool IsCapturable(IntPtr hWnd, uint ownPid, out string title, out int pid, out string exe)
    {
        title = ""; pid = 0; exe = "";

        if (!IsWindowVisible(hWnd)) return false;

        // Root windows only (no child/popup-owner mess).
        if (GetAncestor(hWnd, GA_ROOT) != hWnd) return false;

        // Drop tool windows (tray helpers etc.).
        long exStyle = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;

        // Drop DWM-"cloaked" windows (UWP ghosts, other desktops).
        if (IsCloaked(hWnd)) return false;

        // A title must be present.
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return false;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        title = sb.ToString().Trim();
        if (title.Length == 0) return false;

        // Determine the process; exclude our own process (RitschyMirror).
        GetWindowThreadProcessId(hWnd, out uint wpid);
        if (wpid == 0 || wpid == ownPid) return false;
        pid = (int)wpid;
        exe = ProcessExeName(wpid);

        return true;
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0)
                return cloaked != 0;
        }
        catch { /* DWM attribute unavailable → treat as not cloaked */ }
        return false;
    }

    /// <summary>Exe file name of a process (robust even with restricted access:
    /// PROCESS_QUERY_LIMITED_INFORMATION + QueryFullProcessImageName, no MainModule).</summary>
    private static string ProcessExeName(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size) && size > 0)
            {
                string full = sb.ToString(0, size);
                int slash = full.LastIndexOfAny(new[] { '\\', '/' });
                return slash >= 0 ? full[(slash + 1)..] : full;
            }
        }
        catch { /* ignore → empty exe name */ }
        finally { CloseHandle(h); }
        return "";
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────────
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint GA_ROOT = 2;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // GetWindowLongPtr is the right entry point on x64 (the 32-bit variant is dropped, we build x64-only).
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
}
