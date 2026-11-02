using System.Globalization;

namespace RitschyMirror;

/// <summary>
/// Folder layout next to the executable:
/// <code>
///   RitschyMirror.exe (+ RitschyMirror.dll, runtimeconfig)
///   bin\     dependency assemblies (normal builds; a single-file publish bundles them into the exe)
///   config\  mirror_config.json, app_settings.json
///   lang\    optional language-pack overrides / extra languages
///   log\     mirror_yyyyMMdd_HHmmss.log — one file per app start, kept for <see cref="LogRetentionDays"/> days
/// </code>
/// Older versions kept config and mirror.log directly next to the exe; <see cref="Prepare"/> moves
/// them into the new folders on first start.
/// </summary>
public static class AppPaths
{
    public const int LogRetentionDays = 30;
    private const string LogPrefix = "mirror_";
    private const string LogTimestamp = "yyyyMMdd_HHmmss";

    public static string Base { get; } = AppContext.BaseDirectory;
    public static string BinDir => Path.Combine(Base, "bin");
    public static string ConfigDir => Path.Combine(Base, "config");
    public static string LangDir => Path.Combine(Base, "lang");
    public static string LogDir => Path.Combine(Base, "log");

    public static string MirrorConfig => Path.Combine(ConfigDir, "mirror_config.json");
    public static string AppSettings => Path.Combine(ConfigDir, "app_settings.json");

    /// <summary>Log file of this app run (created by <see cref="Prepare"/>).</summary>
    public static string LogFile { get; private set; } = "";

    /// <summary>
    /// Create the folders, migrate files from the old flat layout, pick this run's log file and delete
    /// logs older than <see cref="LogRetentionDays"/>. Returns notes for the log (it is not open yet).
    /// </summary>
    public static List<string> Prepare()
    {
        var notes = new List<string>();
        foreach (var dir in new[] { ConfigDir, LogDir })
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { notes.Add($"Cannot create {dir}: {ex.Message}"); }
        }

        MigrateLegacy(Path.Combine(Base, "mirror_config.json"), MirrorConfig, notes);
        MigrateLegacy(Path.Combine(Base, "app_settings.json"), AppSettings, notes);
        // The old single log keeps its content; the retention below removes it once it is old enough.
        MigrateLegacy(Path.Combine(Base, "mirror.log"), Path.Combine(LogDir, LogPrefix + "legacy.log"), notes);

        LogFile = Path.Combine(LogDir, LogPrefix + DateTime.Now.ToString(LogTimestamp, CultureInfo.InvariantCulture) + ".log");
        DeleteOldLogs(notes);
        return notes;
    }

    /// <summary>
    /// Move a file of the old layout into its new place. A legacy file always wins: a fresh build copies
    /// the DEFAULT mirror_config.json into config\, which must not shadow the user's existing settings.
    /// </summary>
    private static void MigrateLegacy(string legacy, string target, List<string> notes)
    {
        try
        {
            if (!File.Exists(legacy)) return;
            File.Move(legacy, target, overwrite: true);
            notes.Add($"Moved {Path.GetFileName(legacy)} → {Path.GetRelativePath(Base, target)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Cannot move {legacy}: {ex.Message}");
        }
    }

    private static void DeleteOldLogs(List<string> notes)
    {
        var cutoff = DateTime.Now.AddDays(-LogRetentionDays);
        try
        {
            foreach (var f in Directory.EnumerateFiles(LogDir, LogPrefix + "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) >= cutoff) continue;
                    File.Delete(f);
                    notes.Add($"Deleted old log {Path.GetFileName(f)}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    notes.Add($"Cannot delete old log {Path.GetFileName(f)}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Cannot scan {LogDir}: {ex.Message}");
        }
    }
}
