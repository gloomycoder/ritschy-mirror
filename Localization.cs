using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace RitschyMirror;

/// <summary>
/// Language packs for every user-visible string (settings GUI, tray, OBS dock, user-facing errors).
///
/// Packs are flat JSON objects (<c>"key": "text"</c>, <c>{0}</c>-style placeholders). The built-in
/// packs (en/ko/de) are embedded in the exe; a <c>lang\&lt;code&gt;.json</c> file next to the exe
/// overrides single keys of a built-in pack or adds a whole new language. Lookup order for a key:
/// external current → embedded current → external en → embedded en → the key itself, so a partial
/// translation never shows blanks.
///
/// The language is chosen by <c>app_settings.json → language</c>: <c>"auto"</c> (default) follows the
/// Windows UI language and falls back to English when no pack exists for it.
/// </summary>
public static class L
{
    public const string Auto = "auto";
    private const string Fallback = "en";
    private static readonly string[] Embedded = { "en", "ko", "de" };

    private static readonly object Gate = new();
    private static Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _english = new(StringComparer.Ordinal);
    private static string _langDir = "";

    /// <summary>Resolved language code actually in use (e.g. "ko").</summary>
    public static string Current { get; private set; } = Fallback;

    /// <summary>Load the packs for <paramref name="setting"/> ("auto" or a language code).</summary>
    public static void Init(string baseDir, string? setting)
    {
        _langDir = Path.Combine(baseDir, "lang");
        var available = Available();
        string code = (setting ?? Auto).Trim().ToLowerInvariant();
        if (code.Length == 0 || code == Auto)
            code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        if (!available.Contains(code)) code = Fallback;

        // Fallback first, current language on top → missing keys fall through to English.
        var english = new Dictionary<string, string>(StringComparer.Ordinal);
        MergeInto(english, Fallback);
        var merged = new Dictionary<string, string>(english, StringComparer.Ordinal);
        if (code != Fallback) MergeInto(merged, code);

        lock (Gate)
        {
            _english = english;
            _current = merged;
            Current = code;
        }
    }

    /// <summary>Translated text for <paramref name="key"/>; formats <paramref name="args"/> into it.</summary>
    public static string T(string key, params object?[] args)
    {
        string text;
        lock (Gate) text = _current.TryGetValue(key, out var v) ? v : key;
        return Format(text, args);
    }

    /// <summary>English text for <paramref name="key"/> — for mirror.log, which stays English.</summary>
    public static string En(string key, params object?[] args)
    {
        string text;
        lock (Gate) text = _english.TryGetValue(key, out var v) ? v : key;
        return Format(text, args);
    }

    private static string Format(string text, object?[] args)
    {
        if (args.Length == 0) return text;
        try { return string.Format(CultureInfo.CurrentCulture, text, args); }
        catch (FormatException) { return text; } // broken placeholder in a user pack — show it raw
    }

    /// <summary>Snapshot of the whole active pack (served to the OBS dock via <c>GET /lang</c>).</summary>
    public static IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (Gate) return new Dictionary<string, string>(_current, StringComparer.Ordinal);
    }

    /// <summary>All selectable language codes: embedded packs plus external <c>lang\*.json</c>.</summary>
    public static SortedSet<string> Available()
    {
        var set = new SortedSet<string>(Embedded, StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(_langDir))
                foreach (var f in Directory.EnumerateFiles(_langDir, "*.json"))
                    set.Add(Path.GetFileNameWithoutExtension(f).ToLowerInvariant());
        }
        catch (IOException) { /* unreadable folder → embedded packs only */ }
        catch (UnauthorizedAccessException) { }
        return set;
    }

    /// <summary>Native display name of a pack (its own "lang.name" key), e.g. "한국어".</summary>
    public static string DisplayName(string code)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        MergeInto(d, code);
        return d.TryGetValue("lang.name", out var n) && n.Length > 0 ? n : code;
    }

    private static void MergeInto(Dictionary<string, string> target, string code)
    {
        Merge(target, ReadEmbedded(code));
        Merge(target, ReadExternal(code));
    }

    private static void Merge(Dictionary<string, string> target, Dictionary<string, string>? src)
    {
        if (src is null) return;
        foreach (var kv in src) target[kv.Key] = kv.Value;
    }

    private static Dictionary<string, string>? ReadEmbedded(string code)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"lang.{code}.json");
        if (s is null) return null;
        return Parse(s, $"embedded lang.{code}.json");
    }

    private static Dictionary<string, string>? ReadExternal(string code)
    {
        string path = Path.Combine(_langDir, code + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            using var s = File.OpenRead(path);
            return Parse(s, path);
        }
        catch (IOException ex) { Console.Error.WriteLine($"[lang] Cannot read {path}: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { Console.Error.WriteLine($"[lang] Cannot read {path}: {ex.Message}"); }
        return null;
    }

    private static readonly JsonDocumentOptions ParseOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static Dictionary<string, string>? Parse(Stream s, string origin)
    {
        try
        {
            using var doc = JsonDocument.Parse(s, ParseOpts);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                Console.Error.WriteLine($"[lang] Ignoring {origin}: the root must be a JSON object.");
                return null;
            }
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) d[p.Name] = p.Value.GetString() ?? "";
            return d;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // A broken user pack (bad JSON, bad encoding) must never take the app down —
            // ignore it and keep the others.
            Console.Error.WriteLine($"[lang] Invalid pack {origin}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// A user-facing message kept as key + arguments, so it can be shown in the current UI language
/// (<see cref="Text"/>) and logged in English (<see cref="English"/>) — and re-renders correctly
/// after a language switch.
/// </summary>
public sealed class LocalizedText
{
    public string Key { get; }
    public object?[] Args { get; }

    public LocalizedText(string key, params object?[] args) { Key = key; Args = args; }

    public string Text => L.T(Key, Args);
    public string English => L.En(Key, Args);
    public override string ToString() => Text;
}
