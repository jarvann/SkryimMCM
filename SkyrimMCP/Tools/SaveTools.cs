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

    // Only names the game made carry a character ID that loading restores (BUG-091: a console-made save named like one doesn't).
    static readonly Regex CharacterIdInName = new(@"^(?:Save|Autosave|Quicksave)\d*_([0-9A-F]{8})_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The running game's save folder: Documents\My Games\&lt;game&gt;\ plus sLocalSavePath (default Saves\).</summary>
    internal static string SaveFolder() => GameEnvironment.Current().Saves;

    /// <summary>The character a save belongs to: the ID in a game-made name, else the header's player name.</summary>
    internal static (string? Id, string? Name) CharacterOf(string path)
    {
        // BUG-121 (GOG, 2026-11-25): SkyLink's saves have plain names, so two characters both called Adventurer looked alike and
        // the other one's save loaded. Their character ID is in SkyLink's co-save record.
        var id = CharacterIdInName.Match(Path.GetFileName(path)) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : CoSaveCharacterId(path);
        string? name = null;
        try { name = EssScanner.ReadHeader(path).PlayerName; } catch (Exception) { }
        return (id, name);
    }

    /// <summary>
    /// The character ID SkyLink keeps in a save's SKSE co-save (plugin 'SKLK', record 'CHID', BUG-091), or null. The co-save is the
    /// SKSE header (signature, format, SKSE and runtime versions, plugin count), then per plugin its ID, chunk count and length,
    /// then its chunks (type, version, length, data).
    /// </summary>
    internal static string? CoSaveCharacterId(string essPath)
    {
        var cosave = Path.ChangeExtension(essPath, ".skse");
        const uint Uid = 0x534B4C4B, Record = 0x43484944;   // 'SKLK', 'CHID' as MSVC writes them
        try
        {
            if (!File.Exists(cosave)) return null;
            using var r = new BinaryReader(File.OpenRead(cosave));
            if (r.ReadUInt32() != 0x45534B53) return null;   // "SKSE" as bytes
            r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
            var plugins = r.ReadUInt32();
            for (var p = 0; p < plugins && r.BaseStream.Position + 12 <= r.BaseStream.Length; p++)
            {
                var uid = r.ReadUInt32(); var chunks = r.ReadUInt32(); var length = r.ReadUInt32();
                var end = r.BaseStream.Position + length;
                if (uid == Uid)
                    for (var c = 0; c < chunks && r.BaseStream.Position + 12 <= end; c++)
                    {
                        var type = r.ReadUInt32(); r.ReadUInt32(); var size = r.ReadUInt32();
                        if (type == Record && size == 4) { var id = r.ReadUInt32(); return id == 0 ? null : id.ToString("X8"); }
                        r.BaseStream.Seek(size, SeekOrigin.Current);
                    }
                r.BaseStream.Seek(end, SeekOrigin.Begin);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException) { }
        return null;
    }

    static bool IsSkyLinkSave(string file)
    {
        var label = LabelOf(Path.GetFileNameWithoutExtension(file));
        return label.StartsWith("SkyLink", StringComparison.OrdinalIgnoreCase) || file.Contains("cleaned", StringComparison.OrdinalIgnoreCase);
    }

    // BUG-122: the Load menu lists a character's saves by the character ID in the file name, so SkyLink names its saves the
    // game's way with the label in the location field: Save<n>_<ID>_1_<name in hex>_<label>_000000_<UTC yyyyMMddHHmmss>_<level>_<sex>.
    // The menu shows the location and date from inside the save, not the label; SkyLink's tools find saves by label. The play
    // time field 000000 tells SkyLink's from the game's own (whose field there is the location).
    static readonly Regex GameStyleName = new(@"^Save\d*_[0-9A-F]{8}_\d+_[0-9A-F]*_(?<label>.+)_000000_\d{14}_\d+_\d+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A save's label: the label field of a name SkyLink made the game's way, else the name itself.</summary>
    internal static string LabelOf(string bareName) =>
        GameStyleName.Match(bareName) is { Success: true } m ? m.Groups["label"].Value : bareName;

    /// <summary>
    /// The save a name or label means: the file of that exact name, else the newest game-style save with that label, the current
    /// character's first when characterId is given. Null when none.
    /// </summary>
    internal static string? FileForLabel(string folder, string name, string? characterId = null)
    {
        if (!Directory.Exists(folder)) return null;
        var bare = name.EndsWith(".ess", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        var exact = Path.Combine(folder, bare + ".ess");
        if (File.Exists(exact)) return exact;
        var ids = SkyrimMCP.Characters.CharacterIds.All(characterId);
        return new DirectoryInfo(folder).EnumerateFiles("*.ess")
            .Where(f => LabelOf(Path.GetFileNameWithoutExtension(f.Name)).Equals(bare, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => ids.Any(i => f.Name.Contains("_" + i + "_", StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    /// <summary>The file a save just asked for under this label was written to (the newest with that label).</summary>
    internal static string SavedFile(string label) => FileForLabel(SaveFolder(), label) ?? Path.Combine(SaveFolder(), label + ".ess");

    /// <summary>The game-style file name for a save labelled label, or null when the game has no character ID to put in it.</summary>
    internal static async Task<string?> GameStyleFileName(IPipeClient pipe, string label)
    {
        try
        {
            var me = await pipe.SendRequestAsync("get_appearance");
            var id = me?["characterId"]?.ToString()?.ToUpperInvariant();
            if (string.IsNullOrEmpty(id) || id == "00000000" || me?["characterIdLost"]?.GetValue<bool>() == true) return null;
            var name = me?["name"]?.ToString() ?? "";
            var female = me?["sex"]?.ToString() == "female";
            var level = 1;
            try { level = (await pipe.SendRequestAsync("get_player_info"))?["level"]?.GetValue<int>() ?? 1; } catch (Exception) { }
            // The number the menu shows: one more than this character's highest.
            var ids = SkyrimMCP.Characters.CharacterIds.All(id);
            var number = 0;
            if (Directory.Exists(SaveFolder()))
                foreach (var f in Directory.EnumerateFiles(SaveFolder(), "Save*.ess"))
                    if (Regex.Match(Path.GetFileName(f), @"^Save(\d+)_([0-9A-F]{8})_", RegexOptions.IgnoreCase) is { Success: true } m &&
                        ids.Contains(m.Groups[2].Value.ToUpperInvariant()) && int.TryParse(m.Groups[1].Value, out var n))
                        number = Math.Max(number, n + 1);
            var hexName = Convert.ToHexString(System.Text.Encoding.Latin1.GetBytes(name));
            return $"Save{number}_{id}_1_{hexName}_{label}_000000_{DateTime.UtcNow:yyyyMMddHHmmss}_{level}_{(female ? 1 : 0)}";
        }
        catch (Exception) { return null; }
    }

    static readonly Regex GameMadeName = new(@"^(Save|Autosave|Quicksave)\d*_([0-9A-F]{8})_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Asks the game to save and waits until the file has started to appear; asks once more if it hasn't after 6 seconds. Mods
    /// can refuse saves (survival and save-limiting mods; seen twice on 2026-11-19), so a missing file is reported as not saved.
    /// Returns null when it started, or the reason it didn't.
    /// </summary>
    internal static async Task<string?> RequestSave(IPipeClient pipe, string name) => (await RequestSaveEx(pipe, name)).error;

    /// <summary>As RequestSave, also saying whether a drawn weapon or spell was sheathed first.</summary>
    internal static async Task<(string? error, bool sheathed)> RequestSaveEx(IPipeClient pipe, string name)
    {
        // BUG-122: named the game's way so the Load menu lists it under the character; plain label when there's no ID.
        var fileName = await GameStyleFileName(pipe, name) ?? name;
        var file = Path.Combine(SaveFolder(), fileName + ".ess");
        // BUG-103: an older save of that name isn't this one (2026-11-22: a refused save over an existing name was reported as done).
        var before = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        // A drawn weapon or spell is enough for some load orders to refuse the save (seen in game): sheathe first and wait for it.
        var sheathed = false;
        try
        {
            // Wait until the weapon state reads sheathed (0), not just "not drawn": a save asked for mid-animation was refused
            // although the reply said it had sheathed (2026-11-20). Then a short settle.
            for (var i = 0; i < 12; i++)
            {
                var s = await pipe.SendRequestAsync("sheathe");
                var drawn = s?["wasDrawn"]?.GetValue<bool>() == true;
                var state = s?["weaponState"]?.GetValue<int>() ?? 0;
                if (drawn) sheathed = true;
                if (!drawn && state == 0) break;
                await Task.Delay(500);
            }
            if (sheathed) await Task.Delay(700);
        }
        catch (Exception) { }
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var started = await pipe.SendRequestAsync("save_game", new JsonObject { ["saveName"] = fileName });
            if (started?["error"] != null) return (started["error"]!.ToString(), sheathed);
            for (var waited = 0; waited < 6_000; waited += 500)
            {
                await Task.Delay(500);
                if (File.Exists(file) && File.GetLastWriteTimeUtc(file) > before) return (null, sheathed);
            }
        }
        // Round B: a refused save near a runaway pile-up. Say what the game was doing, not only what it could be.
        var seen = new List<string>();
        try
        {
            var safety = await pipe.SendRequestAsync("get_game_safety");
            foreach (var b in (safety?["moveBlockers"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? ""))
                if (b.Contains("combat", StringComparison.OrdinalIgnoreCase) || b.Contains("loading", StringComparison.OrdinalIgnoreCase) || b.Contains("saving", StringComparison.OrdinalIgnoreCase)) seen.Add(b);
            var stats = await pipe.SendRequestAsync("get_script_stats", new JsonObject { ["top"] = 1 });
            if (stats?["overstressed"]?.GetValue<bool>() == true)
                seen.Add($"the script engine is overstressed ({stats["runningStacks"]} stacks running): saves wait for scripts to pause, and a pile-up can stop that");
            var s = await pipe.SendRequestAsync("sheathe");
            if (s?["weaponState"]?.GetValue<int>() is int ws && ws != 0) seen.Add($"a weapon or spell is still out (weapon state {ws})");
        }
        catch (Exception) { }
        var observed = seen.Count > 0 ? " Right now: " + string.Join("; ", seen) + "." : "";
        return ($"the game didn't save '{name}' (no file after two requests, 12 seconds{(sheathed ? "; a drawn weapon was sheathed first" : "")}).{observed} Otherwise most likely a mod blocked it: survival or " +
               "save-limiting mods (Survival Mode, SaveReminder and the like) refuse saves under their own rules, console saves included; " +
               "the game also won't save in combat, during a load or while another save is being written. In some load orders a drawn " +
               "weapon or readied spell blocks it too: SkyLink sheathes before saving, so check it stayed sheathed. Nothing else was done.", sheathed);
    }

    /// <summary>
    /// FEAT-263: every quest's stages with done flags (status 1 = done), read from a save: fresh (saved now, under the given
    /// label) or, if that fails or isn't wanted, the newest save of this character. The engine's in-memory list read only zeros.
    /// Keys are form IDs in that save's load order (the running game's, for a save made now).
    /// </summary>
    internal async Task<(string? save, Dictionary<string, List<int[]>>? stages, string? note)> QuestStagesFromSave(string label, bool fresh = true, string? reuseSave = null)
    {
        string? path = null;
        string? note = null;
        // A save made moments ago (a preview's way-back save) instead of saving again.
        if (reuseSave is { Length: > 0 } && ResolveSave(reuseSave) is string recent && File.GetLastWriteTime(recent) > DateTime.Now.AddMinutes(-10))
            path = recent;
        else if (fresh)
        {
            var name = $"{label}_{DateTime.Now:yyyyMMdd-HHmmss}";
            if (await RequestSave(_pipe, name) is string refused) note = "Couldn't save for the stages: " + refused;
            else
            {
                path = await WaitForCompleteSave(SavedFile(name), TimeSpan.FromSeconds(45));
                if (path == null) note = $"The save {name} wasn't complete in time.";
            }
        }
        if (path == null)
        {
            string? currentId = null;
            try { currentId = (await _pipe.SendRequestAsync("get_appearance"))?["characterId"]?.ToString(); } catch (Exception) { }
            var newest = Directory.Exists(SaveFolder())
                ? new DirectoryInfo(SaveFolder()).EnumerateFiles("*.ess").OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault(f => currentId == null || SkyrimMCP.Characters.CharacterIds.All(currentId).Any(i => f.Name.Contains(i, StringComparison.OrdinalIgnoreCase)))
                : null;
            if (newest == null) return (null, null, (note ?? "") + " No save of this character to read stages from.");
            path = newest.FullName;
            note = (note == null ? "" : note + " ") + $"Stages are as of the newest save, {Path.GetFileNameWithoutExtension(path)} ({newest.LastWriteTime:HH:mm}): anything done since isn't included.";
        }
        try
        {
            var scan = await Task.Run(() => EssScanner.Scan(path, questStages: true));
            return (Path.GetFileNameWithoutExtension(path), scan.QuestStages, note);
        }
        catch (Exception ex) { return (null, null, $"Couldn't read {Path.GetFileName(path)}: {ex.Message}"); }
    }

    static SaveHeader? SafeHeader(string path) { try { return EssScanner.ReadHeader(path); } catch (Exception) { return null; } }

    // Queued calls and the mod with the most, for marking way-back saves that still hold a flood (about 0.5 s a save).
    static object? QuickStacks(string path)
    {
        try
        {
            var s = EssScanner.Scan(path);
            // Queued calls and threads together per mod: the wendigo's 280 threads wait inside a native call, not in the queue.
            var perMod = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in s.QueuedByPlugin.Concat(s.ThreadsByPlugin)) perMod[m.Plugin] = perMod.GetValueOrDefault(m.Plugin) + m.Count;
            var top = perMod.OrderByDescending(kv => kv.Value).FirstOrDefault();
            return new { queued = s.SuspendedStacks1 + s.SuspendedStacks2, threads = s.ActiveScripts, topMod = top.Key == null ? null : $"{top.Key} {top.Value}",
                         flood = s.SuspendedStacks1 + s.SuspendedStacks2 >= 1000 ? true : (bool?)null };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    [McpServerTool]
    [Description("Move saves out of the save folder into Saves\\SkyLink_Archive (with their .skse co-saves), so they stop cluttering " +
        "the load menu without being deleted: move them back by hand to restore. names: comma-separated save names, or 'skylink' " +
        "for every save SkyLink made (SkyLink_*, SkyLinkCheck*, SkyLinkBeforeClean*, *_cleaned, *Cleaned*) older than olderThanMinutes. dryRun " +
        "(default true) lists what would move. Never moves the newest save. Use ListSaves(scan: true) first to see which way-back " +
        "saves still hold a stack flood.")]
    public Task<object> ArchiveSaves(string names, int olderThanMinutes = 30, bool dryRun = true)
    {
        var folder = SaveFolder();
        if (!Directory.Exists(folder)) return Task.FromResult<object>(new { error = $"Save folder not found: {folder}" });
        var all = new DirectoryInfo(folder).EnumerateFiles("*.ess").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var newest = all.FirstOrDefault()?.FullName;
        List<FileInfo> pick;
        if (names.Trim().Equals("skylink", StringComparison.OrdinalIgnoreCase))
            pick = all.Where(f => IsSkyLinkSave(f.Name) && f.LastWriteTime < DateTime.Now.AddMinutes(-olderThanMinutes)).ToList();
        else
        {
            pick = [];
            foreach (var n in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var bare = n.EndsWith(".ess", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
                var f = all.FirstOrDefault(x => Path.GetFileNameWithoutExtension(x.Name).Equals(bare, StringComparison.OrdinalIgnoreCase));
                if (f == null) return Task.FromResult<object>(new { error = $"No save named '{n}' (exact name, as ListSaves shows it)." });
                pick.Add(f);
            }
        }
        pick = pick.Where(f => f.FullName != newest).ToList();
        var archive = Path.Combine(folder, "SkyLink_Archive");
        if (dryRun || pick.Count == 0)
            return Task.FromResult<object>(new
            {
                dryRun,   // as asked: a real run with nothing to move isn't a dry run (2026-11-20)
                wouldMove = pick.Select(f => Path.GetFileNameWithoutExtension(f.Name)), to = archive,
                note = pick.Count == 0 ? "Nothing to move: no save qualifies (the newest save is never moved, since Continue loads it)."
                                       : "Nothing moved (dry run). The newest save is never moved (Continue loads it).",
            });
        Directory.CreateDirectory(archive);
        var moved = new List<string>();
        foreach (var f in pick)
        {
            try
            {
                f.MoveTo(Path.Combine(archive, f.Name), overwrite: false);
                var cosave = Path.ChangeExtension(Path.Combine(folder, f.Name), ".skse");
                if (File.Exists(cosave)) File.Move(cosave, Path.Combine(archive, Path.GetFileName(cosave)), overwrite: false);
                moved.Add(Path.GetFileNameWithoutExtension(f.Name));
            }
            catch (IOException ex) { moved.Add($"{f.Name}: not moved ({ex.Message})"); }
        }
        return Task.FromResult<object>(new { moved, to = archive, note = "Move a save back into the save folder to restore it." });
    }

    static string? ResolveSave(string name)
    {
        var folder = SaveFolder();
        if (!Directory.Exists(folder)) return null;
        var bare = name.EndsWith(".ess", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (FileForLabel(folder, bare) is string byLabel) return byLabel;
        return new DirectoryInfo(folder).EnumerateFiles("*.ess")
            .Where(f => f.Name.Contains(bare, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    [McpServerTool]
    [Description("List the running game's saves (Steam or GOG, whichever is connected), newest first: file name, character, " +
        "level, location, in-game date, when saved, size, and the character ID from the file name (saves of one character " +
        "share it). filter matches the file name, character or location. scan: true also reads each save's stacks (queued calls, " +
        "the mod with most, flood when 1,000+), about half a second a save. madeBySkyLink marks check, way-back and cleaned saves. Read-only.")]
    public async Task<object> ListSaves(string? filter = null, int limit = 20, bool scan = false)
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
                label = LabelOf(Path.GetFileNameWithoutExtension(f.Name)) is var lb && lb != Path.GetFileNameWithoutExtension(f.Name) ? lb : null,
                character = h?.PlayerName,
                level = h?.Level,
                location = h?.Location,
                gameDate = h?.GameDate,
                saved = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                sizeMB = Math.Round(f.Length / 1048576.0, 1),
                characterId = id,
                currentCharacter = currentId != null && id != null ? SkyrimMCP.Characters.CharacterIds.Same(id, currentId) : (bool?)null,
                unreadable = h == null ? true : (bool?)null,
                madeBySkyLink = IsSkyLinkSave(f.Name) ? true : (bool?)null,
                stacks = scan ? QuickStacks(f.FullName) : null,
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
            if (await RequestSave(_pipe, made) is string notStarted) return new { error = $"Couldn't save for the check: {notStarted}" };
            // Under the bridge's 80-second limit: a long save is reported, not waited out.
            path = await WaitForCompleteSave(SavedFile(made), TimeSpan.FromSeconds(50));
            if (path == null)
                return new { error = $"Saving as {made}: the file wasn't complete after a minute (a big save, or the save didn't start). Try CheckSave(\"{made}\") in a moment; if the file never appears, save again." };
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
        {
            try
            {
                var live = await _pipe.SendRequestAsync("get_script_stats", new JsonObject { ["top"] = 200 });
                if (live?["entryPointOwners"] is JsonObject o)
                {
                    // "X" and "X (queued)" both appear when a handler is running and queued: first one wins (ToDictionary threw).
                    liveOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (key, value) in o)
                        if (value is JsonArray { Count: > 0 } list && list[0]?["plugin"]?.ToString() is { Length: > 0 } owner)
                            liveOwners.TryAdd(key.Replace(" (queued)", ""), owner);
                }
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
            return new { stillActive = info.StillActive.Count > 0 ? $"{string.Join(", ", info.StillActive)} is still loaded: cleaning alone buys a minute or two. Stop the cause as well (see warnings)." : null,
                         dryRun = true, save = Path.GetFileNameWithoutExtension(path), wouldClean = plan.Items, warnings = plan.Warnings,
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
    public async Task<object> CleanAndReload(string? stacks = null, string levels = "safe", bool confirm = false, string? previewSave = null)
    {
        if (await SafetyBlock("move") is string blocked) return new { error = blocked };
        string made;
        string? path = null;
        // The preview's save, if it's recent: one way-back save per cleanup instead of two.
        if (confirm && previewSave is { Length: > 0 } && ResolveSave(previewSave) is string prev && File.GetLastWriteTime(prev) > DateTime.Now.AddMinutes(-10))
        {
            made = Path.GetFileNameWithoutExtension(prev);
            path = prev;
        }
        else
        {
            made = $"SkyLink_BeforeClean_{DateTime.Now:yyyyMMdd-HHmmss}";
            if (await RequestSave(_pipe, made) is string notStarted) return new { error = $"Couldn't make the way-back save: {notStarted} Nothing was cleaned." };
            path = await WaitForCompleteSave(SavedFile(made), TimeSpan.FromSeconds(50));
            if (path == null) return new { error = $"Saving as {made}: the file wasn't complete after 55 seconds; nothing was cleaned. Run CleanAndReload again with previewSave=\"{made}\" once it exists." };
        }

        var info = new CleanInfo();
        var result = await CleanFile(path, levels, stacks, dryRun: !confirm, info);
        if (!confirm) return new { step = "preview", savedAs = made, plan = result, next = $"If the player agrees, call CleanAndReload again with confirm=true and previewSave=\"{made}\" (within 10 minutes it cleans that save instead of saving again; later it saves afresh)." };
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

    [McpServerTool]
    [Description("Tell SkyLink that a save under another character ID is the character in the game (BUG-149): the game gives the " +
        "player a new character ID each time the race menu closes, so one character's older saves carry an older ID, are listed " +
        "as another character's, and LoadSave refuses them at first. SkyLink links the IDs by itself when it sees the change " +
        "happen; this is for a change it didn't see. Ask the player first: two characters with the same name are not the same " +
        "character (both called Adventurer, BUG-121). saveName: a save of the other ID. Links only saves of the same player " +
        "name; per-character data (family story, family homes, face settings) kept under the other ID is merged.")]
    public async Task<object> LinkCharacterIds(string saveName)
    {
        var folder = SaveFolder();
        var bare = saveName.EndsWith(".ess", StringComparison.OrdinalIgnoreCase) ? saveName[..^4] : saveName;
        var file = FileForLabel(folder, bare);
        if (file == null) return new { error = $"No save named \"{bare}\" in {folder}." };
        var me = await _pipe.SendRequestAsync("get_appearance");
        var myId = me?["characterId"]?.ToString()?.ToUpperInvariant();
        var myName = me?["name"]?.ToString();
        if (string.IsNullOrEmpty(myId) || myId == "00000000") return new { error = SkyrimMCP.Face.FaceState.NoCharacterId };
        var (id, name) = CharacterOf(file);
        if (id == null) return new { error = $"{bare} has no character ID (not in its name nor in SkyLink's co-save record)." };
        if (!string.Equals(name, myName, StringComparison.Ordinal))
            return new { error = $"{bare} is {name ?? "an unnamed character"}'s save and {myName} is in the game: only saves of the same player name are linked." };
        if (SkyrimMCP.Characters.CharacterIds.Same(id, myId))
            return new { linked = false, note = $"{id} and {myId} are already the same character." };
        // The older ID stays the character's key, so data first kept under it isn't moved.
        var older = File.GetLastWriteTimeUtc(file) < DateTime.UtcNow ? id : myId;
        var newer = older == id ? myId : id;
        SkyrimMCP.Characters.CharacterIds.Link(newer, older, myName ?? "", $"LinkCharacterIds({bare}), confirmed by the player");
        return new
        {
            linked = true,
            character = myName,
            ids = SkyrimMCP.Characters.CharacterIds.All(myId),
            note = "Saves under any of these IDs are now this character's: ListSaves marks them currentCharacter and LoadSave loads them " +
                   "without the other-character refusal. Per-character data was merged under the first ID.",
        };
    }

    [McpServerTool]
    [Description("Put back a lost character ID (BUG-091). Loading a save made with the console save command (SkyLink's own saves " +
        "before 1.4, or 'save <name>' typed in the console) leaves the game's character ID at 00000000; every save after that " +
        "carries it, the load menu stops grouping them with the character, and per-character settings (face colours) can't be " +
        "found. This sets it from the newest save the game itself made for this character (Save/Autosave/Quicksave files), or " +
        "from characterId if given. Only acts when the ID is lost. Save from the game menu afterwards.")]
    public async Task<object> RepairCharacterId(string? characterId = null)
    {
        var appearance = await _pipe.SendRequestAsync("get_appearance");
        if (appearance?["characterIdLost"]?.GetValue<bool>() != true)
            return new { ok = true, characterId = appearance?["characterId"]?.ToString(), note = "The character ID isn't lost: nothing to repair." };
        var player = appearance?["name"]?.ToString();
        var id = characterId?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(id))
        {
            var folder = SaveFolder();
            var found = new List<(string id, string save, DateTime when)>();
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*.ess").OrderByDescending(f => f.LastWriteTimeUtc).Take(500))
            {
                var m = GameMadeName.Match(f.Name);
                if (!m.Success || m.Groups[2].Value == "00000000") continue;
                if (!string.Equals(SafeHeader(f.FullName)?.PlayerName, player, StringComparison.Ordinal)) continue;
                found.Add((m.Groups[2].Value.ToUpperInvariant(), Path.GetFileNameWithoutExtension(f.Name), f.LastWriteTime));
            }
            var ids = found.GroupBy(x => x.id).Select(g => new { characterId = g.Key, newestSave = g.First().save, saved = g.First().when.ToString("yyyy-MM-dd HH:mm"), saves = g.Count() }).ToList();
            if (ids.Count == 0) return new { error = $"No save made by the game for '{player}' with a character ID. Give characterId (8 hex digits) if you know it." };
            if (ids.Count > 1) return new { error = $"More than one character named '{player}' has saves: pick one and call again with characterId.", characters = ids };
            id = ids[0].characterId;
        }
        var set = await _pipe.SendRequestAsync("set_character_id", new JsonObject { ["characterId"] = id });
        if (set?["error"] != null) return DeserializeResponse(set);
        return new { repaired = true, characterId = id, next = "Make a save from the game menu (Esc > Save): it carries the ID again, and SkyLink's co-save keeps it from now on." };
    }

    [McpServerTool]
    [Description("Check whether a save will survive the current load order, for collection or mod-list updates (FEAT-269). " +
        "Compares the save's plugin list with the plugins loaded now (or, with the game closed, Plugins.txt): removed mods with " +
        "how much of the save each one owns (changed forms, script instances, running threads, queued calls) and a risk for " +
        "each, mods that changed between full and light (an ESP compacted to ESL renumbers its forms), added mods, and moved " +
        "ones (the game re-maps by name, so moving alone is safe). scripts: true also compares each script's variables in the " +
        "save with the installed script files (loose and in BSAs; slower): removed or retyped variables are what breaks a " +
        "save after a mod update (MO2 mod folders are searched too, found from MO2's own settings); differences a recent save (the newest one, assumed made with the current mods) shares are " +
        "treated as noise. Read-only. saveName: as ListSaves shows it.")]
    public async Task<object> CompareSaveToLoadOrder(string saveName, bool scripts = false)
    {
        var path = ResolveSave(saveName);
        if (path == null) return new { error = $"No save matching '{saveName}' in {SaveFolder()}. ListSaves shows them." };
        List<MigrationReport.CurrentPlugin>? current = null;
        string source = "the running game";
        try
        {
            var lo = await _pipe.SendRequestAsync("get_load_order");
            if (lo?["mods"] is JsonArray mods && mods.Count > 0)
                current = mods.OfType<JsonObject>().Select(m => new MigrationReport.CurrentPlugin(m["name"]?.ToString() ?? "", m["type"]?.ToString() == "esl")).ToList();
        }
        catch (Exception) { }
        if (current == null)
        {
            // MO2 keeps the load order in its profile; Vortex and the game in LocalAppData.
            // MO2's profile, or the Steam or GOG game's Plugins.txt (the GOG one was missed before).
            var (txt, exists, _) = SkyrimMCP.Plugins.LoadOrder.PluginsTxtPath();
            if (!exists) return new { error = "The game isn't running and Plugins.txt wasn't found: start the game, or run this from the main menu." };
            current = MigrationReport.FromPluginsTxt(txt, SkyLinkSettings.SkyrimPath().Path);
            source = txt + " (game closed: full or light not known, so type changes aren't checked)";
        }
        try
        {
            return await Task.Run(() =>
            {
                var scan = EssScanner.Scan(path, scriptMembers: scripts);
                object? scriptReport = null;
                if (scripts && SkyLinkSettings.GameFileFolders() is { Count: > 0 } folders)
                {
                    // Baseline: the newest save other than this one, assumed made with the current mods (say so in the reply).
                    var newest = new DirectoryInfo(SaveFolder()).EnumerateFiles("*.ess").OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault(f => !f.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
                    SaveScan? baseline = null;
                    try { if (newest != null) baseline = EssScanner.Scan(newest.FullName, scriptMembers: true); } catch (Exception) { }
                    scriptReport = MigrationReport.CompareScripts(scan, ScriptFiles.Build(folders, MigrationReport.ArchiveOrder(current)), baseline);
                }
                return MigrationReport.Build(scan, current, source, scriptReport);
            });
        }
        catch (Exception ex) { return new { error = $"Couldn't read {Path.GetFileName(path)}: {ex.Message}" }; }
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
