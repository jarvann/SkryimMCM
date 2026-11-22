using System.Diagnostics;

namespace SkyrimMCP;

/// <summary>
/// INFRA-027: where everything belonging to the running game is, decided by that game alone (BUG-088, 095, 097 and 101 were all
/// a folder taken from the wrong game). The connected game says its documents and install folders and SkyLink keeps them after
/// a crash; before that, the running SkyrimSE process decides (GOG installs carry goggame-*.info). MO2's profile moves the saves
/// and INIs when its LocalSaves and LocalSettings are on. Tools ask here instead of building paths.
/// </summary>
public sealed record GameEnvironment
{
    public required string Store { get; init; }           // Steam, GOG or unknown
    public string? GameFolder { get; init; }
    public string? GameFolderFrom { get; init; }
    public string? Exe { get; init; }
    public string? ExeVersion { get; init; }
    public string? SkseVersion { get; init; }
    public required string Documents { get; init; }
    public required string DocumentsFrom { get; init; }
    public required string Saves { get; init; }
    public required string SavesFrom { get; init; }
    public required Dictionary<string, IniFile> Inis { get; init; }
    public required string SkseLogs { get; init; }
    public required string SkyLinkFolder { get; init; }
    public string? PluginsTxt { get; init; }
    public string? PluginsTxtFrom { get; init; }
    public string? Data { get; init; }
    public string? ModManager { get; init; }
    public string? Mo2Instance { get; init; }
    public string? Mo2Profile { get; init; }
    public required List<string> CrashDumpFolders { get; init; }

    public sealed record IniFile(string Path, bool Exists, string From);

    private static readonly string[] IniNames = ["Skyrim.ini", "SkyrimPrefs.ini", "SkyrimCustom.ini"];

    public static GameEnvironment Current()
    {
        var documents = SkyLinkSettings.DocumentsFolder;
        var (gameFolder, gameFrom) = SkyLinkSettings.SkyrimPath();
        var gog = documents.TrimEnd('\\', '/').EndsWith(" GOG", StringComparison.OrdinalIgnoreCase) || IsGogFolder(gameFolder);
        var (mo2, profile) = SkyLinkSettings.Mo2Instance();
        var profileFolder = mo2 == null ? null : Path.Combine(mo2, "profiles", profile ?? "Default");
        var inis = IniNames.ToDictionary(n => n, n => Ini(n, documents, profileFolder), StringComparer.OrdinalIgnoreCase);

        // Documents\...\<sLocalSavePath> (default Saves), or the MO2 profile's own saves.
        string saves, savesFrom;
        if (profileFolder != null && ProfileFlag(profileFolder, "LocalSaves"))
            (saves, savesFrom) = (Path.Combine(profileFolder, "saves"), "the MO2 profile (local saves)");
        else
        {
            var local = "Saves";
            foreach (var name in new[] { "SkyrimCustom.ini", "Skyrim.ini" })
                if (inis[name] is { Exists: true } ini && ReadValue(ini.Path, "General", "sLocalSavePath") is { Length: > 0 } v) { local = v.TrimEnd('\\', '/'); break; }
            (saves, savesFrom) = (Path.Combine(documents, local), $"the game's documents folder ({local})");
        }

        var exe = gameFolder == null ? null : Path.Combine(gameFolder, "SkyrimSE.exe");
        var skse = gameFolder == null ? null : Directory.EnumerateFiles(gameFolder, "skse64_1_*.dll").Concat(Directory.EnumerateFiles(gameFolder, "skse64_loader.exe")).FirstOrDefault();
        var (pluginsTxt, _, pluginsFrom) = Plugins.LoadOrder.PluginsTxtPath();
        var dump = new List<string>();
        try { dump.AddRange(Minidump.DumpFolders()); } catch (Exception) { }

        return new GameEnvironment
        {
            Store = gog ? "GOG" : gameFolder == null ? "unknown" : "Steam",
            GameFolder = gameFolder, GameFolderFrom = gameFrom,
            Exe = File.Exists(exe) ? exe : null,
            ExeVersion = VersionOf(exe), SkseVersion = VersionOf(skse),
            Documents = documents,
            DocumentsFrom = SkyLinkSettings.GameDocumentsFolder is { Length: > 0 } ? "the connected game" : SkyLinkSettings.RunningGameFolder() != null ? "the running game" : "default (no game running)",
            Saves = saves, SavesFrom = savesFrom, Inis = inis,
            SkseLogs = Path.Combine(documents, "SKSE"),
            SkyLinkFolder = SkyLinkSettings.Folder,
            PluginsTxt = pluginsTxt, PluginsTxtFrom = pluginsFrom,
            Data = SkyLinkSettings.DataPath(),
            ModManager = Tools.InstallTools.DetectManager().ToString() is "None" ? null : Tools.InstallTools.DetectManager().ToString(),
            Mo2Instance = mo2, Mo2Profile = mo2 == null ? null : profile ?? "Default",
            CrashDumpFolders = dump,
        };
    }

    /// <summary>The INI the game uses: the MO2 profile's own copy when local settings are on, else the documents folder's.</summary>
    public static IniFile Ini(string name) => Current().Inis[name];

    private static IniFile Ini(string name, string documents, string? profileFolder)
    {
        if (profileFolder != null && ProfileFlag(profileFolder, "LocalSettings"))
        {
            var own = Path.Combine(profileFolder, name.ToLowerInvariant());
            if (File.Exists(own)) return new IniFile(own, true, "the MO2 profile (local settings)");
        }
        var file = Path.Combine(documents, name);
        return new IniFile(file, File.Exists(file), "the game's documents folder");
    }

    // MO2 writes LocalSaves and LocalSettings in the profile's settings.ini.
    private static bool ProfileFlag(string profileFolder, string key) =>
        string.Equals(ReadValue(Path.Combine(profileFolder, "settings.ini"), "General", key), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>One value from an INI section, or null. Shared with the checks that read game settings.</summary>
    internal static string? ReadValue(string path, string section, string key)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var current = "";
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { current = line.Trim('[', ']'); continue; }
                if (!current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
                var eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line[(eq + 1)..].Trim();
            }
        }
        catch (IOException) { }
        return null;
    }

    private static bool IsGogFolder(string? game)
    {
        try { return game != null && Directory.Exists(game) && (Directory.EnumerateFiles(game, "goggame-*.info").Any() || File.Exists(Path.Combine(game, "Galaxy64.dll"))); }
        catch (Exception) { return false; }
    }

    private static string? VersionOf(string? file)
    {
        try { return file != null && File.Exists(file) ? FileVersionInfo.GetVersionInfo(file).FileVersion?.Replace(", ", ".") : null; }
        catch (Exception) { return null; }
    }
}
