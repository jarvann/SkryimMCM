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

        // Live owners (as StackReport names them: for a magic effect, the effect's plugin) for handlers running now, so CheckSave
        // and StackReport can be read together. The save itself only knows the object a script runs on.
        Dictionary<string, string>? liveOwners = null;
        if (made != null)
        {
            try
            {
                var live = await _pipe.SendRequestAsync("get_script_stats", new JsonObject { ["top"] = 200 });
                if (live?["entryPointOwners"] is JsonObject o)
                    liveOwners = o.Where(kv => kv.Value is JsonArray { Count: > 0 })
                        .ToDictionary(kv => kv.Key.Replace(" (queued)", ""), kv => ((JsonArray)kv.Value!)[0]?["plugin"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception) { }
        }
        object Handler(NameCount n) => new { name = n.Name, count = n.Count, runningOn = n.RunningOn,
                                             liveOwner = liveOwners != null && liveOwners.TryGetValue(n.Name, out var lo) ? lo : null };
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
                queuedByFunction = scan.QueuedCalls.Take(20).Select(Handler),
                queuedByMod = scan.QueuedByPlugin,
                threadsByEntryPoint = scan.ThreadsByEntryPoint.Take(20).Select(Handler),
                threadsByMod = scan.ThreadsByPlugin,
                threadsByTopFrame = scan.ThreadsByTopFrame,
                functionMessages = scan.FunctionMessages,
                functionMessagesByFunction = scan.FunctionMessageCalls,
                functionMessagesByMod = scan.FunctionMessagesByPlugin,
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
            note = "Read-only: nothing was changed. runningOn = the plugin of the object a script runs on (an effect's actor, the player = Skyrim.esm), not necessarily the mod that defines the script; liveOwner (when the handler is running now) is what StackReport shows. Undefined = the save holds data for scripts whose files the game couldn't find when it saved (usually a removed mod). " +
                   "Unattached instances are often harmless leftovers. Credit: the scan follows ReSaver (FallrimTools, Mark Fairchild).",
        };
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // FEAT-267: cleaning, through an installed ReSaver's library (SaveClean.class, shipped with the server). Always writes a new
    // file next to the original; the original is the way back.

    static readonly string[] BaseGamePlugins = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    /// <summary>An installed ReSaver (FallrimTools) folder: settings resaverPath, the RESAVER_DIR variable, or a few usual places.</summary>
    internal static string? ResaverFolder()
    {
        var settings = SkyLinkSettings.Load();
        var candidates = new List<string?>
        {
            settings["resaverPath"]?.ToString(),
            Environment.GetEnvironmentVariable("RESAVER_DIR"),
        };
        foreach (var root in new[] { @"C:\Games\Modding", @"C:\Modding", @"C:\Games", @"C:\Tools", @"C:\" })
            foreach (var name in new[] { "ReSaver", "Resaver", "FallrimTools" })
                candidates.Add(Path.Combine(root, name));
        if (SkyLinkSettings.SkyrimPath().Path is string game)
            foreach (var name in new[] { "ReSaver", "FallrimTools" }) candidates.Add(Path.Combine(game, "Tools", name));
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, "target", "ReSaver.jar")));
    }

    static string UniqueOutput(string original)
    {
        var dir = Path.GetDirectoryName(original)!;
        var bare = Path.GetFileNameWithoutExtension(original);
        for (var i = 1; ; i++)
        {
            var name = i == 1 ? $"{bare}_cleaned" : $"{bare}_cleaned{i}";
            if (!File.Exists(Path.Combine(dir, name + ".ess"))) return Path.Combine(dir, name + ".ess");
        }
    }

    sealed record CleanPlan(List<string> Args, List<object> Items, List<string> Warnings, string? Error)
    {
        public HashSet<string> Mods { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Handlers { get; } = [];
    }

    static CleanPlan Plan(SaveScan scan, string levels, string? stacks)
    {
        var args = new List<string>();
        var items = new List<object>();
        var warnings = new List<string>();
        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var handlers = new List<string>();
        var lv = levels.ToLowerInvariant();
        if (scan.UndefinedScripts.Count > 0 || scan.UndefinedThreads > 0)
        {
            items.Add(new { level = "safe", what = $"script data of {scan.UndefinedScripts.Count} scripts with no script file ({scan.UndefinedInstances} instances)" +
                                                     (scan.UndefinedThreads > 0 ? $", and {scan.UndefinedThreads} threads running them (terminated)" : ""),
                            examples = scan.UndefinedScripts.Take(10), couldBreak = "nothing that still works: the game can't run these scripts. If you reinstall the mod later it starts fresh." });
            args.Add("--undefined");
        }
        if (lv.Contains("caution") && scan.UnattachedInstances > 0)
        {
            items.Add(new { level = "caution", what = $"{scan.UnattachedInstances} unattached script instances", examples = scan.UnattachedInstancesByScript.Take(10),
                            couldBreak = "a mod that still expects them may stop working until it sets itself up again" });
            args.Add("--unattached");
        }
        // A plugin name (Mod.esp) stands for every handler of that mod with queued calls or threads in the save.
        var names = new List<string>();
        foreach (var n in (stacks ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Regex.IsMatch(n, @"\.(esp|esm|esl)$", RegexOptions.IgnoreCase))
            {
                var all = scan.QueuedCalls.Concat(scan.ThreadsByEntryPoint).Where(x => string.Equals(x.RunningOn, n, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (all.Count == 0) return new CleanPlan(args, items, warnings, $"No queued calls or threads from {n} in this save.");
                names.AddRange(all);
            }
            else names.Add(n);
        }
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var queued = scan.QueuedCalls.FirstOrDefault(q => q.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var running = scan.ThreadsByEntryPoint.FirstOrDefault(q => q.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (queued == null && running == null && scan.FunctionMessageCalls.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return new CleanPlan(args, items, warnings,
                    $"'{name}' is only in the pending function calls, which ReSaver can't remove. The fix is the mod (update, settings, regenerate its output); cleaning won't help.");
            if (queued == null && running == null)
                return new CleanPlan(args, items, warnings,
                    $"No stacks of '{name}' in this save. Its queued calls and threads are named like: " +
                    string.Join(", ", scan.QueuedCalls.Concat(scan.ThreadsByEntryPoint).Select(x => x.Name).Distinct().Take(10)));
            var plugin = queued?.RunningOn ?? running?.RunningOn;
            if (plugin != null && !plugin.StartsWith("(")) mods.Add(plugin);
            handlers.Add(name);
            items.Add(new { level = "caution", what = $"{name}{(plugin != null ? $" ({plugin})" : "")}: {queued?.Count ?? 0} queued calls and {running?.Count ?? 0} running threads",
                            couldBreak = "whatever those calls were doing is dropped; the mod runs its handler again on its next event. If the mod keeps piling up, the stacks come back (then the mod is the fix: update, configure or remove it)." });
            if (plugin != null && BaseGamePlugins.Contains(plugin, StringComparer.OrdinalIgnoreCase))
                warnings.Add($"{name} runs on the player or a base-game object ({plugin}). If it's a base-game script (defaultsetstagealiasscript and other default*/quest scripts are), " +
                             "deleting its queued calls can skip the quest stage one of them would have set. With thousands of copies nearly all are duplicates of one event, " +
                             "but check the quests involved afterwards; usually another mod is feeding it (a magic effect applied over and over to the player).");
            if (name.Contains("quest", StringComparison.OrdinalIgnoreCase) || name.StartsWith("QF_", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"{name} looks like a quest script: cleaning it can leave that quest stuck at a stage.");
            args.Add("--stack");
            args.Add(name);
        }
        if (args.Count == 0) return new CleanPlan(args, items, warnings, "Nothing to clean at these levels (name pile-ups to clean with stacks, from CheckSave).");
        var plan = new CleanPlan(args, items, warnings, null);
        plan.Mods.UnionWith(mods);
        plan.Handlers.AddRange(handlers);
        return plan;
    }

    static async Task<(bool ok, List<string> lines)> RunCleaner(string resaver, string input, string output, List<string> args)
    {
        var serverDir = AppContext.BaseDirectory;
        var classDir = Path.Combine(serverDir, "SaveClean");
        var java = Path.Combine(resaver, "jre", "bin", "java.exe");
        if (!File.Exists(java)) java = "java";
        var psi = new System.Diagnostics.ProcessStartInfo(java)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-Xmx4g", "-cp", $"{classDir};{Path.Combine(resaver, "target", "ReSaver.jar")};{Path.Combine(resaver, "target", "lib", "*")}", "SaveClean", input, output })
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch (Exception) { } return (false, ["error: the cleaner took longer than 5 minutes"]); }
        var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return (proc.ExitCode == 0, lines);
    }

    [McpServerTool]
    [Description("Clean a COPY of a save (written as <save>_cleaned; the original is never changed and stays the way back). " +
        "levels: 'safe' (default) = script data of scripts whose files are gone, from removed mods; 'caution' adds unattached " +
        "instances. stacks: comma-separated pile-ups to remove, named exactly as CheckSave lists them (Script.Function, e.g. " +
        "AniyaFollower_FollowerOnHit.OnMagicEffectApply), or a mod's plugin name (Mod.esp) for all of its handlers: their " +
        "queued calls and running threads are deleted. dryRun (default " +
        "true) only lists what would go and what could break. Editing a save is risky: tell the player what could break and get " +
        "their agreement first. Needs ReSaver (FallrimTools) installed; set its folder with SetSkyLinkSetting('resaverPath') if " +
        "it isn't found. CleanAndReload does save, clean and load in one go.")]
    public async Task<object> CleanSave(string saveName, string levels = "safe", string? stacks = null, bool dryRun = true)
    {
        var path = ResolveSave(saveName);
        if (path == null) return new { error = $"No save matching '{saveName}' in {SaveFolder()}. ListSaves shows them." };
        return await CleanFile(path, levels, stacks, dryRun, new CleanInfo());
    }

    sealed class CleanInfo { public List<string> Handlers = []; public List<string> StillActive = []; }

    async Task<object> CleanFile(string path, string levels, string? stacks, bool dryRun, CleanInfo info)
    {
        SaveScan scan;
        try { scan = await Task.Run(() => EssScanner.Scan(path)); }
        catch (Exception ex) { return new { error = $"Couldn't read {Path.GetFileName(path)}: {ex.Message}" }; }
        if (!scan.PapyrusParsed) return new { error = $"The script section of {Path.GetFileName(path)} couldn't be read ({scan.PapyrusError}); it won't be cleaned." };
        var plan = Plan(scan, levels, stacks);
        if (plan.Error != null) return new { error = plan.Error, save = Path.GetFileNameWithoutExtension(path) };
        info.Handlers.AddRange(plan.Handlers);
        // Cleaning a pile-up whose mod is still running buys about a minute (seen in game: Aniya back to ~1,700 copies in 40 s).
        if (plan.Mods.Count > 0)
        {
            try
            {
                var lo = await _pipe.SendRequestAsync("get_load_order");
                if (lo?["mods"] is JsonArray loaded)
                {
                    var names = new HashSet<string>(loaded.Select(m => m?["name"]?.ToString() ?? ""), StringComparer.OrdinalIgnoreCase);
                    info.StillActive.AddRange(plan.Mods.Where(m => names.Contains(m) && !BaseGamePlugins.Contains(m, StringComparer.OrdinalIgnoreCase)));
                }
            }
            catch (Exception) { }
            if (info.StillActive.Count > 0)
                plan.Warnings.Insert(0, $"{string.Join(", ", info.StillActive)} is still loaded: if it keeps causing the pile-up, the stacks come back within a minute or two of loading the cleaned save. " +
                                        "Stop the cause too: update or reconfigure the mod, stop the quest that drives it (StopQuest), or disable the mod before playing on.");
        }
        if (dryRun)
            return new { dryRun = true, save = Path.GetFileNameWithoutExtension(path), wouldClean = plan.Items, warnings = plan.Warnings,
                         note = "Nothing changed. Run again with dryRun=false after the player agrees; the cleaned copy is a new save and the original stays." };

        var resaver = ResaverFolder();
        if (resaver == null)
            return new { error = "ReSaver (FallrimTools) isn't installed where SkyLink looks. Install it from Nexus (Skyrim SE mod 5031) and set its folder with SetSkyLinkSetting('resaverPath', '<folder with ReSaver.exe>')." };
        var output = UniqueOutput(path);
        var (ok, lines) = await RunCleaner(resaver, path, output, plan.Args);
        if (!ok)
        {
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
            return new { error = "The cleaner failed; nothing was written over anything: " + string.Join(" | ", lines.TakeLast(4)) };
        }
        SaveScan? after = null;
        try { after = await Task.Run(() => EssScanner.Scan(output)); } catch (Exception) { }
        int Num(string key) => lines.FirstOrDefault(l => l.StartsWith(key + ":")) is string l && int.TryParse(l[(key.Length + 1)..].Trim(), out var v) ? v : 0;
        var queuedRemoved = Num("queuedRemoved");
        var ownStacks = Num("threadOwnStacksRemoved");
        var totals = plan.Handlers.Count == 0 ? null :
            $"Removed {queuedRemoved} suspended stacks: {queuedRemoved - ownStacks} queued calls of the named handlers, and {ownStacks} stacks belonging to " +
            $"their running threads (waiting inside a call, e.g. a native function) that had to go with them. {Num("threadsRemoved")} threads deleted" +
            (Num("threadsTerminatedOnly") > 0 ? $", {Num("threadsTerminatedOnly")} terminated but kept (a pending call points at them)." : ".");
        return new
        {
            cleaned = Path.GetFileNameWithoutExtension(output),
            original = Path.GetFileNameWithoutExtension(path),
            removed = plan.Items,
            totals,
            stillActive = info.StillActive.Count > 0 ? info.StillActive : null,
            cleanerReport = lines,
            after = after == null ? null : new { threads = after.ActiveScripts, queuedCalls = after.SuspendedStacks1 + after.SuspendedStacks2, undefinedScripts = after.UndefinedScripts.Count, scriptSectionRead = after.PapyrusParsed },
            warnings = plan.Warnings,
            note = $"The original save '{Path.GetFileNameWithoutExtension(path)}' is unchanged: it's the way back. Play a while on the cleaned save and make a new save before deleting anything.",
        };
    }

    [McpServerTool]
    [Description("The whole cleanup in one go, for a game slowed down by script pile-ups or mod leftovers: saves the current game, " +
        "cleans a copy of that save (same levels and stacks as CleanSave), and loads the cleaned copy. With confirm=false (default) " +
        "it only saves, checks and shows what would be cleaned and what could break. Editing a save is risky: explain the risks " +
        "and get the player's agreement before confirm=true. The fresh save is kept as the way back. Refused in menus, combat, " +
        "scenes and other states where a load isn't safe. Run StackReport after it to see whether the pile-up is gone or returns.")]
    public async Task<object> CleanAndReload(string? stacks = null, string levels = "safe", bool confirm = false)
    {
        if (await SafetyBlock("move") is string blocked) return new { error = blocked };
        var made = $"SkyLink_BeforeClean_{DateTime.Now:yyyyMMdd-HHmmss}";
        var started = await _pipe.SendRequestAsync("save_game", new JsonObject { ["saveName"] = made });
        if (started?["error"] != null) return DeserializeResponse(started);
        var path = await WaitForCompleteSave(Path.Combine(SaveFolder(), made + ".ess"), TimeSpan.FromSeconds(120));
        if (path == null) return new { error = $"Saved as {made}, but the file didn't appear complete within 2 minutes; nothing was cleaned." };

        var info = new CleanInfo();
        var result = await CleanFile(path, levels, stacks, dryRun: !confirm, info);
        if (!confirm) return new { step = "preview", savedAs = made, plan = result, next = "If the player agrees, call CleanAndReload again with confirm=true (it saves again, so it cleans the game as it is then)." };
        var cleanedName = System.Text.Json.JsonSerializer.SerializeToNode(result)?["cleaned"]?.ToString();
        if (cleanedName == null) return new { step = "clean failed", savedAs = made, result };

        int? loadsBefore = null;
        try { loadsBefore = (await _pipe.SendRequestAsync("ping"))?["gameLoads"]?.GetValue<int>(); } catch (Exception) { }
        var load = await _pipe.SendRequestAsync("load_save", new JsonObject { ["saveName"] = cleanedName });
        if (load?["error"] != null) return new { step = "load failed", cleaned = cleanedName, savedAs = made, load = DeserializeResponse(load) };
        var loaded = false;
        for (var waited = 0; waited < 180_000 && !loaded; waited += 1000)
        {
            await Task.Delay(1000);
            try
            {
                var ping = await _pipe.SendRequestAsync("ping");
                var loads = ping?["gameLoads"]?.GetValue<int>();
                var loading = ping?["loadingScreen"]?.GetValue<bool>() ?? true;
                loaded = !loading && (loadsBefore == null ? waited > 15_000 : loads > loadsBefore);
            }
            catch (Exception) { }
        }
        // Is the pile-up coming back? Two samples 20 seconds apart, counting the cleaned handlers (running and queued).
        object? regrowth = null;
        if (loaded && info.Handlers.Count > 0)
        {
            async Task<int?> Sample()
            {
                try
                {
                    var data = await _pipe.SendRequestAsync("get_script_stats", new JsonObject { ["top"] = 200 });
                    var set = new HashSet<string>(info.Handlers, StringComparer.OrdinalIgnoreCase);
                    return (data?["topEntryPoints"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                        .Where(e => set.Contains((e["name"]?.ToString() ?? "").Replace(" (queued)", "")))
                        .Sum(e => e["stacks"]?.GetValue<int>() ?? 0);
                }
                catch (Exception) { return null; }
            }
            await Task.Delay(5000);
            var first = await Sample();
            await Task.Delay(20000);
            var second = await Sample();
            regrowth = new
            {
                after5s = first, after25s = second,
                verdict = second is null ? "couldn't sample"
                    : second >= 20 && second > (first ?? 0) ? $"coming back: {second} copies 25 s after loading. The cause is still running; stop it (see stillActive) before saving."
                    : "not coming back so far; run StackReport again in a few minutes to be sure.",
            };
        }
        return new
        {
            step = loaded ? "done" : "loading (not confirmed after 3 minutes)",
            loaded = cleanedName,
            wayBack = made,
            regrowth,
            result,
            next = "Run StackReport in a minute to check the pile-up is gone and isn't coming back. If anything seems wrong, load the way-back save.",
        };
    }

    static string Summarise(SaveScan s, List<string>? missing)
    {
        var parts = new List<string>();
        var queued = s.SuspendedStacks1 + s.SuspendedStacks2;
        // The plugin is where the script's object comes from. For the player and base-game objects that's Skyrim.esm even when
        // the script belongs to a mod (Ordinator's ORD_* effects on the player), so say that rather than blame the base game.
        static string Mod(ModCount p) => p.Plugin.StartsWith("(") ? $"an unidentified mod ({p.Handlers})"
            : BaseGamePlugins.Contains(p.Plugin, StringComparer.OrdinalIgnoreCase)
                ? $"scripts on the player or base-game objects ({p.Handlers}; the scripts themselves may come from mods)"
                : $"objects from {p.Plugin} ({p.Handlers})";
        if (s.QueuedByPlugin.FirstOrDefault() is { Count: >= 100 } qp)
            parts.Add($"{queued} queued script calls are stored in this save; {qp.Count} of them come from {Mod(qp)}. " +
                      "A queue this size eats script time every frame and is a likely cause of low FPS.");
        else if (queued >= 500) parts.Add($"{queued} queued script calls are stored in this save (a lot).");
        if (s.FunctionMessagesByPlugin.FirstOrDefault() is { Count: >= 100 } fp)
            parts.Add($"{s.FunctionMessages} script calls are waiting to start; {fp.Count} from {Mod(fp)}. " +
                      "These can't be removed by cleaning (ReSaver can't delete pending calls): the fix is the mod itself (update, settings, or regenerate its output).");
        if (s.ThreadsByPlugin.FirstOrDefault() is { Count: >= 50 } tp)
            parts.Add($"{s.ActiveScripts} script threads are in the save; {tp.Count} started in {Mod(tp)}.");
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
