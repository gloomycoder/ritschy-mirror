using System.Reflection;

namespace RitschyMirror;

/// <summary>Central app metadata (version from the assembly, repo/copyright for "About").</summary>
internal static class AppInfo
{
    public const string Name = "RitschyMirror";
    public const string RepoUrl = "https://github.com/RitschyRigz/ritschy-mirror";
    public const string Copyright = "© 2026 RitschyRigz — MIT-Lizenz";

    /// <summary>Clean version "1.0.2" (Major.Minor.Build) from the assembly.</summary>
    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
