using System.Collections.Concurrent;
using SkyrimMCP.Saves;

namespace SkyrimMCP.Plugins;

/// <summary>The load order the game will read on its next start, and which file each plugin name resolves to.</summary>
public sealed class LoadOrderState
{
    /// <summary>Active plugins in Plugins.txt order after the base masters and Creation Club files, only those with a file.</summary>
    public required List<string> Active { get; init; }
    /// <summary>Enabled in Plugins.txt but no file anywhere: the game skips them.</summary>
    public required List<string> MissingFiles { get; init; }
    /// <summary>Listed in Plugins.txt without the '*': installed but switched off.</summary>
    public required HashSet<string> Disabled { get; init; }
    public required string Source { get; init; }
    /// <summary>Plugin file name -> the copy the game uses (MO2: overwrite, then mods by priority, then Data).</summary>
    public required Dictionary<string, string> Files { get; init; }
    public required List<string> Folders { get; init; }

    // Built on first use, safely: the limits report asks from several threads at once.
    Dictionary<string, int>? _index;

    public int? IndexOf(string name)
    {
        var index = LazyInitializer.EnsureInitialized(ref _index, () =>
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < Active.Count; i++) d.TryAdd(Active[i], i);
            return d;
        });
        return index.TryGetValue(name, out var at) ? at : null;
    }

    public string? FileOf(string name) => Files.TryGetValue(name, out var f) ? f : null;
}

public static class LoadOrder
{
    public static readonly string[] BaseMasters = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    /// <summary>
    /// The Plugins.txt the game (or MO2) reads: the MO2 profile's when an MO2 instance manages this game, else LocalAppData's
    /// "Skyrim Special Edition" folder, or "Skyrim Special Edition GOG" for the GOG game.
    /// </summary>
    public static (string Path, bool Exists, string Source) PluginsTxtPath()
    {
        var (mo2, profile) = SkyLinkSettings.Mo2Instance();
        var mo2Txt = mo2 == null ? null : Path.Combine(mo2, "profiles", profile ?? "Default", "plugins.txt");
        if (mo2Txt != null && File.Exists(mo2Txt)) return (mo2Txt, true, "MO2 profile " + (profile ?? "Default"));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var steam = Path.Combine(local, "Skyrim Special Edition", "Plugins.txt");
        var gog = Path.Combine(local, "Skyrim Special Edition GOG", "Plugins.txt");
        var isGog = IsGog();
        var first = isGog == true ? gog : steam;
        if (File.Exists(first)) return (first, true, isGog == true ? "GOG game's Plugins.txt" : "Plugins.txt");
        // Edition unknown (game closed, no game folder found): take whichever list exists.
        if (isGog == null && File.Exists(gog)) return (gog, true, "GOG game's Plugins.txt");
        return (first, false, "no Plugins.txt (only the base game and Creation Club files are active)");
    }

    static bool? IsGog()
    {
        if (SkyLinkSettings.GameDocumentsFolder is { Length: > 0 } docs)
            return Path.GetFileName(docs.TrimEnd('\\', '/')).EndsWith(" GOG", StringComparison.OrdinalIgnoreCase);
        if (SkyLinkSettings.SkyrimPath().Path is string game && Directory.Exists(game))
        {
            try
            {
                if (Directory.EnumerateFiles(game, "goggame-*.info").Any() || File.Exists(Path.Combine(game, "Galaxy64.dll"))) return true;
                return false;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>
    /// Reads the load order. The overrides are for tests and odd setups: a Plugins.txt, a Data folder (searched alone), and the
    /// folder holding Skyrim.ccc.
    /// </summary>
    public static LoadOrderState Read(string? pluginsTxt = null, string? dataFolder = null, string? cccFolder = null)
    {
        string source;
        bool exists;
        if (pluginsTxt != null) { exists = File.Exists(pluginsTxt); source = pluginsTxt + (exists ? "" : " (not found)"); }
        else
        {
            var found = PluginsTxtPath();
            (pluginsTxt, exists) = (found.Path, found.Exists);
            source = exists ? $"{found.Path} ({found.Source})" : found.Source;
        }

        List<string> folders;
        if (dataFolder != null) folders = [dataFolder];
        else
        {
            folders = SkyLinkSettings.GameFileFolders();
            folders.Reverse();   // highest priority first
        }
        var files = IndexFiles(folders);

        // Skyrim.ccc sits next to the Data folder (a Wabbajack Stock Game has its own); else the game folder.
        var data = dataFolder ?? SkyLinkSettings.DataPath();
        cccFolder ??= data != null && Path.GetDirectoryName(data.TrimEnd('\\', '/')) is string parent && File.Exists(Path.Combine(parent, "Skyrim.ccc"))
            ? parent : SkyLinkSettings.SkyrimPath().Path;

        var listed = MigrationReport.FromPluginsTxt(exists ? pluginsTxt : null, cccFolder).Select(p => p.Name).ToList();
        var fromTxt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (exists)
            foreach (var line in File.ReadLines(pluginsTxt!))
            {
                var l = line.Trim();
                if (l.Length == 0 || l.StartsWith('#')) continue;
                if (l.StartsWith('*')) fromTxt.Add(l[1..].Trim());
                else disabled.Add(l);
            }
        // Skyrim.ccc names every Creation Club file the game knows, installed or not: only the installed ones load. A missing
        // Plugins.txt entry is worth reporting.
        var active = listed.Where(files.ContainsKey).ToList();
        var missing = listed.Where(n => !files.ContainsKey(n) && fromTxt.Contains(n)).ToList();
        disabled.ExceptWith(active);
        return new LoadOrderState { Active = active, MissingFiles = missing, Disabled = disabled, Source = source, Files = files, Folders = folders };
    }

    /// <summary>Plugin files in the given folders (highest priority first): the first folder holding a name wins.</summary>
    public static Dictionary<string, string> IndexFiles(IEnumerable<string> foldersHighestFirst)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in foldersHighestFirst)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(folder))
                    if (Path.GetExtension(f).ToLowerInvariant() is ".esp" or ".esm" or ".esl") files.TryAdd(Path.GetFileName(f), f);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files;
    }

    // Header reads and scans of unchanged files are reused: a load-order check reads thousands of headers.
    static readonly ConcurrentDictionary<string, (long Size, DateTime Time, PluginHeader Header)> Headers = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, (long Size, DateTime Time, int Stop, PluginScan Scan)> Scans = new(StringComparer.OrdinalIgnoreCase);

    public static PluginHeader Header(string path)
    {
        var fi = new FileInfo(path);
        if (Headers.TryGetValue(path, out var c) && c.Size == fi.Length && c.Time == fi.LastWriteTimeUtc) return c.Header;
        var h = PluginFile.ReadHeader(path);
        Headers[path] = (fi.Length, fi.LastWriteTimeUtc, h);
        return h;
    }

    public static PluginScan Scan(string path, int stopAfterNew = int.MaxValue)
    {
        var fi = new FileInfo(path);
        // A full scan answers a partial one too; a partial one that stopped early can't answer a full one.
        if (Scans.TryGetValue(path, out var c) && c.Size == fi.Length && c.Time == fi.LastWriteTimeUtc && (c.Stop >= stopAfterNew || !c.Scan.StoppedEarly))
            return c.Scan;
        var s = PluginFile.Scan(path, stopAfterNew);
        Scans[path] = (fi.Length, fi.LastWriteTimeUtc, stopAfterNew, s);
        return s;
    }

    /// <summary>
    /// Whether master loads before plugin. The game loads master-flagged files (.esm, .esl, or the master flag) ahead of all
    /// others whatever Plugins.txt says; within each kind, list order.
    /// </summary>
    public static bool LoadsBefore(LoadOrderState lo, string master, bool masterIsMaster, string plugin, bool pluginIsMaster)
    {
        if (masterIsMaster && !pluginIsMaster) return true;
        if (!masterIsMaster && pluginIsMaster) return false;
        var a = lo.IndexOf(master);
        var b = lo.IndexOf(plugin);
        return a != null && (b == null || a < b);
    }
}
