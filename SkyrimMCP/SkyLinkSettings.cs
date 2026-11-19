using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkyrimMCP;

/// <summary>
/// Player-editable settings (INFRA-006) and the folders SkyLink reads mod config files from
/// (INFRA-020). Kept in Documents\My Games\Skyrim Special Edition\SkyLinkAI\settings.json so
/// they survive mod updates.
///
/// MO2 and Wabbajack setups don't have a real Data\SKSE\Plugins: each mod's files sit in its own
/// folder, and new or changed files land in "overwrite". The server runs outside MO2's virtual
/// file system, so it has to search those folders itself.
/// </summary>
public static class SkyLinkSettings
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // BUG-088: the running game's own documents folder (the GOG game uses "Skyrim Special Edition GOG"), set by PipeClient
    // when it connects; the Steam folder until then. Face state, studio trips and markers were shared between games.
    public static string? GameDocumentsFolder { get; set; }

    // BUG-088: the running game's install folder (from the plugin), so a server installed in the Steam game still reads the
    // GOG game's files when the GOG game is the one connected.
    public static string? GameInstallFolder { get; set; }

    public static string Folder => GameDocumentsFolder is { Length: > 0 } game
        ? Path.Combine(game, "SkyLinkAI")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition", "SkyLinkAI");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Keys a player may set, with what each does.</summary>
    public static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>
    {
        ["skyrimPath"] = "Game folder (the one with SkyrimSE.exe). Data is assumed to be <skyrimPath>\\Data unless dataPath is set.",
        ["dataPath"] = "Data folder, when it isn't <skyrimPath>\\Data (for example a Wabbajack list's Stock Game folder).",
        ["mo2Path"] = "MO2 instance folder, the one containing 'mods', 'overwrite' and 'profiles'. Config files are then searched in overwrite and every enabled mod, in MO2's priority order.",
        ["mo2Profile"] = "MO2 profile to take the mod order from. Defaults to the only profile, or 'Default'.",
        ["resaverPath"] = "ReSaver (FallrimTools) folder, the one with ReSaver.exe and target\\ReSaver.jar; CleanSave and CleanAndReload use its library.",
        ["extraConfigFolders"] = "More folders to search for SKSE plugin config files, as a list, searched first.",
    };

    public static JsonObject Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject();
        }
        catch { /* a broken file is treated as empty; GetSkyLinkSettings shows the raw error */ }
        return new JsonObject();
    }

    public static void Save(JsonObject settings)
    {
        Directory.CreateDirectory(Folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, settings.ToJsonString(Indented));
        File.Move(temp, FilePath, overwrite: true);
    }

    private static string? Str(JsonObject s, string key) =>
        s[key] is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    /// <summary>
    /// The game folder: settings, then the SKYRIM_PATH variable, then where this server was
    /// deployed if that is inside a real Data\SKSE\Plugins folder.
    /// </summary>
    public static (string? Path, string Source) SkyrimPath()
    {
        var s = Load();
        if (Str(s, "skyrimPath") is string configured) return (configured, "settings.json skyrimPath");
        if (Environment.GetEnvironmentVariable("SKYRIM_PATH") is string env && env.Length > 0) return (env, "SKYRIM_PATH variable");
        if (GameInstallFolder is { Length: > 0 } running && Directory.Exists(running)) return (running, "the running game");

        foreach (var candidate in new[] { Environment.GetEnvironmentVariable("SKYLINK_SERVER_SOURCE"), typeof(SkyLinkSettings).Assembly.Location })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var marker = $"{System.IO.Path.DirectorySeparatorChar}Data{System.IO.Path.DirectorySeparatorChar}SKSE{System.IO.Path.DirectorySeparatorChar}";
            var at = candidate.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0) return (candidate[..at], "server install location");
        }
        return (null, "not found");
    }

    public static string? DataPath()
    {
        var s = Load();
        if (Str(s, "dataPath") is string data) return data;
        var (game, _) = SkyrimPath();
        return game == null ? null : Path.Combine(game, "Data");
    }

    /// <summary>
    /// Folders that can hold SKSE plugin config files, in search order: extra folders, MO2's
    /// overwrite, each MO2 mod, then the real Data\SKSE\Plugins.
    /// </summary>
    public static List<string> ConfigFolders()
    {
        var s = Load();
        var folders = new List<string>();
        if (s["extraConfigFolders"] is JsonArray extra)
        {
            foreach (var e in extra)
                if (e is JsonValue v && v.TryGetValue<string>(out var f) && !string.IsNullOrWhiteSpace(f)) folders.Add(f.Trim());
        }

        if (Str(s, "mo2Path") is string mo2)
        {
            folders.Add(Path.Combine(mo2, "overwrite", "SKSE", "Plugins"));
            var mods = Path.Combine(mo2, "mods");
            try
            {
                foreach (var mod in Mo2ModsByPriority(mo2, Str(s, "mo2Profile")))
                {
                    var plugins = Path.Combine(mods, mod, "SKSE", "Plugins");
                    if (Directory.Exists(plugins)) folders.Add(plugins);
                }
            }
            catch { /* unreadable mods folder: fall through to Data */ }
        }

        if (DataPath() is string data) folders.Add(Path.Combine(data, "SKSE", "Plugins"));
        return folders;
    }

    /// <summary>
    /// Enabled mods, highest priority first. MO2's modlist.txt lists mods from highest to lowest
    /// priority, "+" for enabled. Without a modlist, every mod folder in name order.
    /// </summary>
    internal static IEnumerable<string> Mo2ModsByPriority(string mo2Path, string? profile)
    {
        var profiles = Path.Combine(mo2Path, "profiles");
        if (profile == null && Directory.Exists(profiles))
        {
            var all = Directory.GetDirectories(profiles);
            profile = all.Length == 1 ? Path.GetFileName(all[0]) : "Default";
        }
        var modlist = Path.Combine(profiles, profile ?? "Default", "modlist.txt");
        if (File.Exists(modlist))
        {
            return File.ReadAllLines(modlist)
                .Where(l => l.StartsWith('+'))
                .Select(l => l[1..].Trim())
                .ToList();
        }
        var mods = Path.Combine(mo2Path, "mods");
        return Directory.Exists(mods)
            ? Directory.EnumerateDirectories(mods).Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
    }

    /// <summary>Every copy of a config file across the search folders, first = the one used.</summary>
    public static List<string> FindConfigFile(string filename)
    {
        var found = new List<string>();
        foreach (var folder in ConfigFolders())
        {
            var path = Path.Combine(folder, filename);
            if (File.Exists(path)) found.Add(path);
        }
        return found;
    }

    public static string NotFoundHelp() =>
        "Set the folders with SetSkyLinkSetting: skyrimPath (the game folder), mo2Path (MO2 instance with 'mods' and 'overwrite'), " +
        "or extraConfigFolders. Searched: " + string.Join("; ", ConfigFolders().Take(8)) + (ConfigFolders().Count > 8 ? "; ..." : "");
}
