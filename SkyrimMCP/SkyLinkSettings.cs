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

    /// <summary>
    /// The game's documents folder: the connected game's (it says, and SkyLink keeps it after a crash), else the running game's
    /// by its exe folder (GOG installs carry goggame-*.info), else Steam's. BUG-101: a freshly started server used Steam's until
    /// its first call reached the game, so a save made on GOG was reported as not made (it looked in Steam's Saves).
    /// </summary>
    public static string DocumentsFolder
    {
        get
        {
            var running = RunningGameFolder();
            // What the plugin said, unless another game is running now (Steam closed, GOG started, no call made yet).
            if (GameDocumentsFolder is { Length: > 0 } connected &&
                (running == null || GameInstallFolder == null || SameFolder(running, GameInstallFolder))) return connected;
            var games = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");
            return Path.Combine(games, running != null && IsGogInstall(running) ? "Skyrim Special Edition GOG" : "Skyrim Special Edition");
        }
    }

    public static string Folder => Path.Combine(DocumentsFolder, "SkyLinkAI");

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>A GOG install: goggame-*.info files or Galaxy64.dll beside the exe.</summary>
    public static bool IsGogInstall(string? game)
    {
        try { return game != null && Directory.Exists(game) && (Directory.EnumerateFiles(game, "goggame-*.info").Any() || File.Exists(Path.Combine(game, "Galaxy64.dll"))); }
        catch (Exception) { return false; }
    }

    public static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Keys a player may set, with what each does.</summary>
    public static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>
    {
        ["skyrimPath"] = "Game folder (the one with SkyrimSE.exe). Data is assumed to be <skyrimPath>\\Data unless dataPath is set.",
        ["dataPath"] = "Data folder, when it isn't <skyrimPath>\\Data (for example a Wabbajack list's Stock Game folder).",
        ["mo2Path"] = "MO2 instance folder, the one containing 'mods', 'overwrite' and 'profiles'. Config files are then searched in overwrite and every enabled mod, in MO2's priority order.",
        ["mo2Profile"] = "MO2 profile to take the mod order from. Defaults to the only profile, or 'Default'.",
        ["resaverPath"] = "ReSaver (FallrimTools) folder, the one with ReSaver.exe and target\\ReSaver.jar; CleanSave and CleanAndReload use its library.",
        ["xeditPath"] = "Folder of SSEEdit (xEdit), for CleanPlugins, when it isn't found by itself.",
        ["lootPath"] = "Folder of LOOT, for SortLoadOrder, when it isn't in Program Files.",
        ["extraConfigFolders"] = "More folders to search for SKSE plugin config files, as a list, searched first.",
        ["dismissMessageBoxes"] = "Message boxes to close by themselves when they get in the way, as a list of pieces of their text. Only boxes with a single button, and " +
            "only ones the player has agreed to (AnswerMessageBox always: true adds the one on screen).",
        ["creationKitPath"] = "Folder of the Creation Kit (the one with 'Papyrus Compiler'), when GetCreationKit doesn't find it.",
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
        // INFRA-027: the game that is running decides, before anything set by hand or kept from an earlier game.
        if (RunningGameFolder() is string process) return (process, "the running game");
        var s = Load();
        if (Str(s, "skyrimPath") is string configured) return (configured, "settings.json skyrimPath");
        // The game that just quit (RestartGame, or after a crash) before a variable that may name another install.
        if (GameInstallFolder is { Length: > 0 } last && Directory.Exists(last)) return (last, "the last connected game");
        if (Environment.GetEnvironmentVariable("SKYRIM_PATH") is string env && env.Length > 0) return (env, "SKYRIM_PATH variable");
        foreach (var candidate in new[] { Environment.GetEnvironmentVariable("SKYLINK_SERVER_SOURCE"), typeof(SkyLinkSettings).Assembly.Location })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var marker = $"{System.IO.Path.DirectorySeparatorChar}Data{System.IO.Path.DirectorySeparatorChar}SKSE{System.IO.Path.DirectorySeparatorChar}";
            var at = candidate.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0) return (candidate[..at], "server install location");
        }
        return (null, "not found");
    }

    /// <summary>
    /// The MO2 instance for the running game: settings mo2Path, else a global instance (LocalAppData\ModOrganizer\*) whose
    /// ModOrganizer.ini gamePath is this game's folder. Returns the instance folder and its selected profile.
    /// </summary>
    public static (string? Folder, string? Profile) Mo2Instance()
    {
        // A game started without MO2 (running while MO2 isn't) reads the real Data folder and its own Plugins.txt, whatever MO2
        // instance points at its folder (round B: InstallPlugin wrote to MO2's profile while the GOG game ran directly).
        if (RunningWithoutMo2()) return (null, null);
        var s = Load();
        if (Str(s, "mo2Path") is string configured) return (configured, Str(s, "mo2Profile"));
        var game = SkyrimPath().Path;
        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        if (game == null || !Directory.Exists(root)) return (null, null);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var ini = System.IO.Path.Combine(dir, "ModOrganizer.ini");
            if (!File.Exists(ini)) continue;
            string? gamePath = null, profile = null;
            foreach (var line in File.ReadLines(ini))
            {
                if (line.StartsWith("gamePath=", StringComparison.OrdinalIgnoreCase)) gamePath = Unwrap(line[9..]).Replace(@"\\", @"\");
                else if (line.StartsWith("selected_profile=", StringComparison.OrdinalIgnoreCase)) profile = Unwrap(line[17..]);
            }
            if (gamePath != null && string.Equals(System.IO.Path.GetFullPath(gamePath).TrimEnd('\\'), System.IO.Path.GetFullPath(game).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return (dir, profile);
        }
        return (null, null);

        static string Unwrap(string v) => v.StartsWith("@ByteArray(") && v.EndsWith(")") ? v[11..^1] : v;
    }

    /// <summary>The folder of the running SkyrimSE.exe (null when none is running, or more than one is).</summary>
    internal static string? RunningGameFolder()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("SkyrimSE");
            if (procs.Length != 1) return null;
            return System.IO.Path.GetDirectoryName(procs[0].MainModule?.FileName);
        }
        catch (Exception) { return null; }
    }

    internal static bool RunningWithoutMo2()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("SkyrimSE").Length > 0
                && System.Diagnostics.Process.GetProcessesByName("ModOrganizer").Length == 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Folders that hold game files, lowest priority first: Data, then MO2's enabled mods by priority, then overwrite.</summary>
    public static List<string> GameFileFolders()
    {
        var folders = new List<string>();
        if (DataPath() is string data) folders.Add(data);
        var (mo2, profile) = Mo2Instance();
        if (mo2 != null)
        {
            try
            {
                folders.AddRange(Mo2ModsByPriority(mo2, profile).Reverse().Select(m => System.IO.Path.Combine(mo2, "mods", m)).Where(Directory.Exists));
            }
            catch (IOException) { }
            var overwrite = System.IO.Path.Combine(mo2, "overwrite");
            if (Directory.Exists(overwrite)) folders.Add(overwrite);
        }
        return folders;
    }

    /// <summary>Where SkyLink puts game files of its own (slot faces): MO2's overwrite when MO2 runs the game, else Data.</summary>
    public static string? WritableDataFolder()
    {
        var (mo2, _) = Mo2Instance();
        return mo2 != null ? System.IO.Path.Combine(mo2, "overwrite") : DataPath();
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

        // The running game's MO2 instance (found by itself; settings mo2Path when set), not only one named in the settings.
        var (mo2, profile) = Mo2Instance();
        if (mo2 != null)
        {
            folders.Add(Path.Combine(mo2, "overwrite", "SKSE", "Plugins"));
            var mods = Path.Combine(mo2, "mods");
            try
            {
                foreach (var mod in Mo2ModsByPriority(mo2, profile))
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
