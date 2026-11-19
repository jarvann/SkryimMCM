using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using SkyrimMCP.Saves;

namespace SkyrimMCP.Tools;

// FEAT-266: list the running game's saves and check one (a fresh save of the current game by default) with the native scan
// (FEAT-265). Read-only apart from the check save itself.
[McpServerToolType]
public class SaveTools : ToolBase
{
    public SaveTools(IPipeClient pipe) : base(pipe) { }

    static readonly Regex CharacterIdInName = new(@"_([0-9A-F]{8})_", RegexOptions.Compiled);

    /// <summary>The running game's save folder: Documents\My Games\&lt;game&gt;\ plus sLocalSavePath (default Saves\).</summary>
    internal static string SaveFolder()
    {
        var docs = SkyLinkSettings.GameDocumentsFolder is { Length: > 0 } g
            ? g
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition");
        var local = "Saves\\";
        foreach (var ini in new[] { "SkyrimCustom.ini", "Skyrim.ini" })
        {
            try
            {
                var file = Path.Combine(docs, ini);
                if (!File.Exists(file)) continue;
                var line = File.ReadLines(file).Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("sLocalSavePath", StringComparison.OrdinalIgnoreCase));
                if (line?.Split('=', 2) is [_, var v] && v.Trim().Length > 0) { local = v.Trim(); break; }
            }
            catch (IOException) { }
        }
        return Path.Combine(docs, local.TrimEnd('\\', '/'));
    }

    static string? ResolveSave(string name)
    {
        var folder = SaveFolder();
        if (!Directory.Exists(folder)) return null;
        var bare = name.EndsWith(".ess", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        var exact = Path.Combine(folder, bare + ".ess");
        if (File.Exists(exact)) return exact;
        return new DirectoryInfo(folder).EnumerateFiles("*.ess")
            .Where(f => f.Name.Contains(bare, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    [McpServerTool]
    [Description("List the running game's saves (Steam or GOG, whichever is connected), newest first: file name, character, " +
        "level, location, in-game date, when saved, size, and the character ID from the file name (saves of one character " +
        "share it). filter matches the file name, character or location. Read-only.")]
    public async Task<object> ListSaves(string? filter = null, int limit = 20)
    {
        var folder = SaveFolder();
        if (!Directory.Exists(folder)) return new { error = $"Save folder not found: {folder}" };
        string? currentId = null;
        try { currentId = (await _pipe.SendRequestAsync("get_appearance"))?["characterId"]?.ToString(); } catch (Exception) { }

        var rows = new List<object>();
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*.ess").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            if (rows.Count >= Math.Clamp(limit, 1, 200)) break;
            SaveHeader? h = null;
            try { h = EssScanner.ReadHeader(f.FullName); } catch (Exception) { }
            if (filter is { Length: > 0 } && !(f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                  (h?.PlayerName.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                  (h?.Location.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))) continue;
            var id = CharacterIdInName.Match(f.Name) is { Success: true } m ? m.Groups[1].Value : null;
            rows.Add(new
            {
                save = Path.GetFileNameWithoutExtension(f.Name),
                character = h?.PlayerName,
                level = h?.Level,
                location = h?.Location,
                gameDate = h?.GameDate,
                saved = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                sizeMB = Math.Round(f.Length / 1048576.0, 1),
                characterId = id,
                currentCharacter = currentId != null && id != null ? string.Equals(id, currentId, StringComparison.OrdinalIgnoreCase) : (bool?)null,
                unreadable = h == null ? true : (bool?)null,
            });
        }
        return new { folder, currentCharacterId = currentId, count = rows.Count, saves = rows };
    }

    [McpServerTool]
    [Description("Check a save for problems with SkyLink's own save scanner (based on ReSaver's). With no saveName it first " +
        "saves the current game as SkyLink_Check_<time> and scans that, so the result is the game as it is now; with a name " +
        "it scans that save (ListSaves shows them). Reports: script stacks stored in the save (running threads and queued calls " +
        "by function and mod: a big queue is the usual FPS killer), script leftovers from removed or changed mods (undefined " +
        "scripts and instances), unattached instances, plugins the save uses that aren't loaded now, and sizes. Read-only: " +
        "nothing is cleaned. Use it after StackReport when the player reports slowdowns or FPS drops.")]
    public async Task<object> CheckSave(string? saveName = null)
    {
        string? path;
        string? made = null;
        if (string.IsNullOrWhiteSpace(saveName))
        {
            if (await SafetyBlock("save") is string blocked) return new { error = blocked };
            made = $"SkyLink_Check_{DateTime.Now:yyyyMMdd-HHmmss}";
            var started = await _pipe.SendRequestAsync("save_game", new JsonObject { ["saveName"] = made });
            if (started?["error"] != null) return DeserializeResponse(started);
            path = await WaitForCompleteSave(Path.Combine(SaveFolder(), made + ".ess"), TimeSpan.FromSeconds(120));
            if (path == null)
                return new { error = $"Saved as {made}, but the file didn't appear complete in {SaveFolder()} within 2 minutes. Try CheckSave(\"{made}\") in a moment." };
        }
        else
        {
            path = ResolveSave(saveName);
            if (path == null) return new { error = $"No save matching '{saveName}' in {SaveFolder()}. ListSaves shows them." };
        }

        SaveScan scan;
        try { scan = await Task.Run(() => EssScanner.Scan(path)); }
        catch (Exception ex) { return new { error = $"Couldn't read {Path.GetFileName(path)}: {ex.Message}" }; }

        // Plugins the save uses that the running game hasn't loaded (their scripts and forms become leftovers).
        List<string>? missing = null;
        try
        {
            var lo = await _pipe.SendRequestAsync("get_load_order");
            if (lo?["mods"] is JsonArray mods)
            {
                var loaded = new HashSet<string>(mods.Select(m => m?["name"]?.ToString() ?? ""), StringComparer.OrdinalIgnoreCase);
                missing = scan.Plugins.Concat(scan.LightPlugins).Where(p => !loaded.Contains(p)).ToList();
            }
        }
        catch (Exception) { }

        return new
        {
            summary = Summarise(scan, missing),
            checkSave = made,
            save = Path.GetFileNameWithoutExtension(path),
            character = scan.Header.PlayerName,
            level = scan.Header.Level,
            location = scan.Header.Location,
            sizes = new { fileMB = scan.FileSizeMB, uncompressedMB = scan.BodySizeMB, papyrusMB = scan.PapyrusSizeMB, scan.ChangeFormCount, scan.Strings },
            scriptSection = scan.PapyrusParsed ? "read completely" : $"read partly: {scan.PapyrusError}",
            stacks = new
            {
                runningThreads = scan.ActiveScripts,
                suspendedThreads = scan.SuspendedThreads,
                terminatedThreads = scan.TerminatedThreads,
                queuedCalls = scan.SuspendedStacks1 + scan.SuspendedStacks2,
                queuedByFunction = scan.QueuedCalls,
                threadsByEntryPoint = scan.ThreadsByEntryPoint,
                threadsByTopFrame = scan.ThreadsByTopFrame,
                functionMessages = scan.FunctionMessages,
            },
            leftovers = new
            {
                undefinedScripts = scan.UndefinedScripts.Take(50),
                undefinedScriptCount = scan.UndefinedScripts.Count,
                undefinedInstances = scan.UndefinedInstances,
                undefinedInstancesByScript = scan.UndefinedInstancesByScript,
                undefinedThreads = scan.UndefinedThreads,
                unattachedInstances = scan.UnattachedInstances,
                unattachedInstancesByScript = scan.UnattachedInstancesByScript,
            },
            pluginsNotLoaded = missing,
            plugins = new { full = scan.Plugins.Count, light = scan.LightPlugins.Count },
            biggestChangeForms = scan.ChangeFormsByType,
            note = "Read-only: nothing was changed. Undefined = the save holds data for scripts whose files the game couldn't find when it saved (usually a removed mod). " +
                   "Unattached instances are often harmless leftovers. Credit: the scan follows ReSaver (FallrimTools, Mark Fairchild).",
        };
    }

    static string Summarise(SaveScan s, List<string>? missing)
    {
        var parts = new List<string>();
        var queued = s.SuspendedStacks1 + s.SuspendedStacks2;
        if (s.QueuedCalls.FirstOrDefault() is { Count: >= 100 } q)
            parts.Add($"{queued} queued script calls are stored in this save; {q.Count} of them are {q.Name}{(q.Plugin != null ? $" ({q.Plugin})" : "")}. " +
                      "A queue this size eats script time every frame and is a likely cause of low FPS.");
        else if (queued >= 500) parts.Add($"{queued} queued script calls are stored in this save (a lot).");
        if (s.ThreadsByEntryPoint.FirstOrDefault() is { Count: >= 50 } t)
            parts.Add($"{s.ActiveScripts} script threads are running in the save; {t.Count} started in {t.Name}{(t.Plugin != null ? $" ({t.Plugin})" : "")}.");
        if (s.UndefinedScripts.Count > 0)
            parts.Add($"{s.UndefinedScripts.Count} scripts in the save have no script file (leftovers of removed or changed mods), with {s.UndefinedInstances} instances" +
                      (s.UndefinedThreads > 0 ? $" and {s.UndefinedThreads} running threads" : "") + ".");
        if (missing is { Count: > 0 })
            parts.Add($"{missing.Count} plugins the save uses aren't loaded now: {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", ..." : "")}.");
        if (s.UnattachedInstances > 0) parts.Add($"{s.UnattachedInstances} unattached script instances (often harmless).");
        if (s.TerminatedThreads > 0) parts.Add($"{s.TerminatedThreads} threads were already terminated (by a cleaner).");
        if (!s.PapyrusParsed) parts.Add($"The script section couldn't be read completely ({s.PapyrusError}); counts may be incomplete.");
        if (parts.Count == 0) parts.Add($"No problems found: {s.ActiveScripts} running threads, {queued} queued calls, no script leftovers.");
        return string.Join(" ", parts);
    }

    // The game writes the save on its own time (10+ seconds with a heavy load order): wait until the file exists, its size has
    // stopped changing, and nothing holds it open for writing.
    static async Task<string?> WaitForCompleteSave(string path, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        long lastSize = -1;
        var stable = 0;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(500);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0) continue;
                if (info.Length == lastSize) stable++; else { stable = 0; lastSize = info.Length; }
                if (stable < 3) continue;
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                return path;
            }
            catch (IOException) { stable = 0; }
        }
        return null;
    }
}
