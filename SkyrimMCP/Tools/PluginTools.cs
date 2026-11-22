using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using SkyrimMCP.Plugins;

namespace SkyrimMCP.Tools;

// FEAT-273/274/276: read plugin files (header, masters, record counts, light eligibility), check a plugin's masters against the
// load order, the load order's plugin limits, and write small new plugins into SkyLink's Build folder. Works with the game
// closed: everything comes from the files and Plugins.txt (the list the game reads on its next start).
[McpServerToolType]
public class PluginTools : ToolBase
{
    public PluginTools(IPipeClient pipe) : base(pipe) { }

    [McpServerTool]
    [Description("Read a plugin file (.esp, .esm or .esl): header (version, author, description, master and light flags), its " +
        "masters (each: found, enabled, loading before it), record counts by type (new records it adds vs overrides of other " +
        "plugins' records), the object-ID range of its new records, and whether it could be light (ESL-flagged) and why. plugin: " +
        "a file name in the load order (Data folder, or MO2's mods and overwrite), part of one, or a full path to any plugin file. " +
        "Read-only; works with the game closed.")]
    public async Task<object> GetPluginInfo(string plugin)
    {
        return await Task.Run<object>(() =>
        {
            var sw = Stopwatch.StartNew();
            var lo = LoadOrder.Read();
            var (path, error) = Resolve(plugin, lo);
            if (path == null) return new { error };
            PluginScan scan;
            try { scan = PluginFile.Scan(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return new { error = $"Couldn't read {path}: {ex.Message}" }; }
            var h = scan.Header;
            var name = Path.GetFileName(path);
            var light = PluginFile.CheckLight(scan);
            var masters = MasterStatuses(h, lo, name);
            var index = lo.IndexOf(name);
            var usesThisFile = lo.FileOf(name) is string used && Path.GetFullPath(used).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            return new
            {
                plugin = name,
                file = path,
                size = scan.FileSize >= 1048576 ? $"{scan.FileSize / 1048576.0:0.0} MB" : $"{scan.FileSize / 1024.0:0.#} KB",
                loadOrder = index is int i ? $"active, position {i + 1} of {lo.Active.Count}" + (usesThisFile ? "" : " (the game uses another copy: " + lo.FileOf(name) + ")")
                    : lo.Disabled.Contains(name) ? "installed but disabled" : "not in the load order",
                kind = (h.IsLight ? "light" : "full") + (h.IsMaster ? " master" : " plugin") + (h.LightFlag && h.Extension != ".esl" ? " (ESL-flagged)" : ""),
                header = new
                {
                    version = h.VersionText,
                    formVersion = h.FormVersion,
                    author = h.Author,
                    description = h.Description,
                    masterFlag = h.MasterFlag,
                    lightFlag = h.LightFlag,
                    localized = h.Localized ? true : (bool?)null,
                    numRecords = h.NumRecords,
                    nextObjectId = $"0x{h.NextObjectId:X6}",
                },
                masters,
                missingMasters = masters.Count(m => !m.found) is var mm and > 0 ? mm : (int?)null,
                records = new
                {
                    total = scan.Records,
                    added = scan.NewRecords,
                    overrides = scan.Overrides,
                    groups = scan.Groups,
                    compressed = scan.Compressed > 0 ? scan.Compressed : (int?)null,
                    newObjectIds = scan.NewRecords > 0 ? $"0x{scan.MinNewId:X6}-0x{scan.MaxNewId:X6}" : null,
                    byType = scan.Types.OrderByDescending(t => t.Value.New + t.Value.Overrides).Take(30)
                        .Select(t => new { type = t.Key, added = t.Value.New, overrides = t.Value.Overrides }),
                    moreTypes = scan.Types.Count > 30 ? scan.Types.Count - 30 : (int?)null,
                    oddMasterIndex = scan.OddMasterIndex > 0 ? scan.OddMasterIndex : (int?)null,
                },
                lightEligibility = new { verdict = light.Verdict, eligible = light.Eligible, withHeader171 = light.EligibleWithHeader171, needsCompacting = light.NeedsCompacting, reasons = light.Reasons },
                warnings = Warnings(scan),
                seconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
            };
        });
    }

    [McpServerTool]
    [Description("What LOOT's masterlist says (FEAT-286), read from the player's own LOOT install: for one plugin, its requirements " +
        "(and whether they're installed), incompatibilities (and whether they're active), LOOT's messages that apply, dirty edits " +
        "matched by the file's checksum (with the cleaning tool to use) and links. With no plugin, scans the whole load order and " +
        "lists the problems: active incompatible pairs, missing requirements, warnings and errors, and dirty plugins " +
        "(checkDirty: true checksums each plugin LOOT has dirty records for; slower on big lists). Messages whose condition " +
        "SkyLink can't evaluate (versions, checksums) come with the condition and 'applies: null'. Says how old the masterlist is. " +
        "Needs LOOT installed (loot.github.io). Read-only.")]
    public async Task<object> GetLootNotes(string? plugin = null, bool checkDirty = true, int page = 1, int pageSize = 40)
    {
        return await Task.Run<object>(() =>
        {
            LootMasterlist? ml;
            try { ml = LootMasterlist.Load(); }
            catch (Exception ex) { return new { error = "Couldn't read LOOT's masterlist: " + ex.Message }; }
            if (ml == null) return new { lootFound = false, note = "LOOT's masterlist wasn't found (%LOCALAPPDATA%\\LOOT\\games\\Skyrim Special Edition). Install LOOT and run it once; SkyLink then reads its notes." };
            var lo = LoadOrder.Read();
            if (plugin != null)
            {
                var name = Path.GetFileName(plugin.Trim());
                return new { masterlist = ml.Age(), notes = ml.NotesFor(name, lo, checkDirty) };
            }

            var problems = new List<object>();
            int known = 0, dirtyCount = 0, incCount = 0, reqCount = 0, warnCount = 0;
            foreach (var p in lo.Active)
            {
                if (!ml.EntriesFor(p).Any()) continue;
                known++;
                var n = System.Text.Json.JsonSerializer.SerializeToNode(ml.NotesFor(p, lo, checkDirty))!;
                var inc = n["activeIncompatibilities"]?.GetValue<int>() ?? 0;
                var req = n["missingRequirements"]?.GetValue<int>() ?? 0;
                var dirty = n["dirty"] is System.Text.Json.Nodes.JsonArray d && d.Count > 0;
                var serious = (n["messages"] as System.Text.Json.Nodes.JsonArray)?.Where(m => m?["type"]?.ToString() is "warn" or "error" && m?["applies"]?.GetValue<bool?>() == true).ToList() ?? [];
                if (inc == 0 && req == 0 && !dirty && serious.Count == 0) continue;
                incCount += inc > 0 ? 1 : 0; reqCount += req > 0 ? 1 : 0; dirtyCount += dirty ? 1 : 0; warnCount += serious.Count > 0 ? 1 : 0;
                problems.Add(new
                {
                    rank = inc > 0 ? 0 : req > 0 ? 1 : serious.Count > 0 ? 2 : 3,
                    plugin = p,
                    incompatibleWith = (n["incompatibilities"] as System.Text.Json.Nodes.JsonArray)?.Where(x => x?["active"]?.GetValue<bool>() == true).Select(x => x?["name"]?.ToString()),
                    missing = (n["requirements"] as System.Text.Json.Nodes.JsonArray)?.Where(x => x?["present"]?.GetValue<bool>() == false).Select(x => x?["name"]?.ToString()),
                    dirty = dirty ? n["dirty"] : null,
                    warnings = serious.Select(m => m?["text"]?.ToString()),
                });
            }
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 5, 200);
            return new
            {
                masterlist = ml.Age(),
                activePlugins = lo.Active.Count,
                knownToLoot = known,
                summary = $"{incCount} plugins with an active incompatibility, {reqCount} missing a requirement, {dirtyCount} dirty, {warnCount} with a LOOT warning or error.",
                page, pages = (problems.Count + pageSize - 1) / pageSize,
                // Round A: page 1 was all Creation Club dirty notes. Incompatibilities, missing requirements and warnings first.
                problems = problems.OrderBy(x => (int)x.GetType().GetProperty("rank")!.GetValue(x)!).Skip((page - 1) * pageSize).Take(pageSize),
                note = "LOOT's notes are community knowledge, not checks of the files' contents (except dirty edits, matched by checksum).",
            };
        });
    }

    [McpServerTool]
    [Description("Check a plugin's masters before installing or enabling it (FEAT-274): each master missing (no file), disabled, " +
        "or loading after the plugin, in plain terms, with what to do. Works on a plugin already in the load order (by name) or " +
        "on a downloaded file not installed yet (full path); for one not installed it checks the masters as if the plugin were " +
        "added at the end. Masters only: scripts and SKSE plugins a mod needs aren't checked. Read-only.")]
    public async Task<object> CheckPluginDependencies(string plugin)
    {
        return await Task.Run<object>(() =>
        {
            var lo = LoadOrder.Read();
            var (path, error) = Resolve(plugin, lo);
            if (path == null) return new { error };
            PluginHeader h;
            try { h = PluginFile.ReadHeader(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return new { error = $"Couldn't read {path}: {ex.Message}" }; }
            var name = Path.GetFileName(path);
            var installed = lo.IndexOf(name) != null;
            var masters = MasterStatuses(h, lo, name);
            var problems = masters.Where(m => m.status != "ok").Select(m => $"{m.name}: {m.detail}").ToList();
            var existing = lo.FileOf(name);
            var replaces = existing != null && !Path.GetFullPath(existing).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            var verdict = problems.Count == 0
                ? (h.Masters.Count == 0 ? $"{name} has no masters: nothing to check." : $"All {h.Masters.Count} masters of {name} are installed, enabled and load before it.")
                : $"{name} can't load as things are: {problems.Count} of its {h.Masters.Count} masters " +
                  $"{(problems.Count == 1 ? "has a problem" : "have problems")}. Missing or late masters usually crash the game at start.";
            return new
            {
                plugin = name,
                file = path,
                ok = problems.Count == 0,
                verdict,
                inLoadOrder = installed ? "active" : lo.Disabled.Contains(name) ? "installed but disabled" : "not installed (checked as if added at the end)",
                problems,
                masters,
                alreadyInstalled = replaces ? $"A plugin with this name is already installed ({existing}); installing this file replaces it." : null,
                loadOrder = lo.Source,
                notChecked = "Scripts and SKSE plugins the mod needs, and the versions of its masters, aren't checked.",
            };
        });
    }

    [McpServerTool]
    [Description("How close the load order is to the game's plugin limits (FEAT-276): full plugins (.esm and .esp without the " +
        "light flag; at most 254, including Skyrim.esm and the DLCs) and light plugins (.esl or ESL-flagged; at most 4,096), " +
        "with the headroom. With lightCandidates (default) it also lists full plugins that could be light without renumbering " +
        "anything (frees a full slot each), then those that would need compacting (renumbering, which breaks saves that use " +
        "their forms), paged (page, pageSize). Reads Plugins.txt (or MO2's profile) and the plugin files: the load order " +
        "of the next game start. Read-only; a few seconds on a big list.")]
    public async Task<object> GetLoadOrderLimits(bool lightCandidates = true, int page = 1, int pageSize = 50)
    {
        return await Task.Run<object>(() => LimitsReport(LoadOrder.Read(), lightCandidates, page, pageSize));
    }

    /// <summary>The limits report for a given load order (tests pass their own).</summary>
    public static object LimitsReport(LoadOrderState lo, bool lightCandidates, int page, int pageSize)
    {
        var sw = Stopwatch.StartNew();
        if (lo.Active.Count == 0 || !lo.Active.Contains("Skyrim.esm", StringComparer.OrdinalIgnoreCase))
            return new
            {
                error = "Skyrim.esm wasn't found, so the Data folder isn't known. Searched: " + string.Join("; ", lo.Folders.Take(4)) +
                        ". Set it with SetSkyLinkSetting: skyrimPath (the game folder), dataPath (a Wabbajack Stock Game's Data folder) or mo2Path.",
            };
        var headers = new ConcurrentDictionary<string, PluginHeader>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new ConcurrentBag<string>();
        Parallel.ForEach(lo.Active, new ParallelOptions { MaxDegreeOfParallelism = 8 }, name =>
        {
            try { headers[name] = LoadOrder.Header(lo.Files[name]); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { unreadable.Add($"{name}: {ex.Message}"); }
        });
        var full = lo.Active.Where(n => headers.TryGetValue(n, out var h) && !h.IsLight).ToList();
        var light = lo.Active.Where(n => headers.TryGetValue(n, out var h) && h.IsLight).ToList();
        var fullFree = PluginFile.FullPluginLimit - full.Count;
        var lightFree = PluginFile.LightPluginLimit - light.Count;

        JsonObject? candidates = null;
        int asIs = 0, with171 = 0, compacting = 0, cannot = 0;
        if (lightCandidates)
        {
            var rows = new ConcurrentBag<(int order, JsonObject row)>();
            var errors = new ConcurrentBag<string>();
            // Bethesda's own files (base game, Creation Club) aren't the player's to change.
            var check = full.Where(n => !IsBaseOrCc(n)).ToList();
            Parallel.ForEach(check, new ParallelOptions { MaxDegreeOfParallelism = 4 }, name =>
            {
                try
                {
                    // Stop once a plugin has more new records than any light plugin can hold: big masters end in a moment.
                    var scan = LoadOrder.Scan(lo.Files[name], PluginFile.LightMax171);
                    var e = PluginFile.CheckLight(scan);
                    if (e.Eligible) Interlocked.Increment(ref asIs);
                    else if (e.EligibleWithHeader171) Interlocked.Increment(ref with171);
                    else if (e.NeedsCompacting) Interlocked.Increment(ref compacting);
                    else Interlocked.Increment(ref cannot);
                    if (!e.Eligible && !e.EligibleWithHeader171 && !e.NeedsCompacting) return;
                    rows.Add((e.Eligible ? 0 : e.EligibleWithHeader171 ? 1 : 2, new JsonObject
                    {
                        ["plugin"] = name,
                        ["position"] = lo.IndexOf(name) + 1,
                        ["how"] = e.Eligible ? "set the light flag (FormIDs unchanged)"
                            : e.EligibleWithHeader171 ? "set the light flag and a 1.71 header (game 1.6.1130+; FormIDs unchanged)"
                            : "compacting (renumbers its forms: breaks saves that use them; for a new playthrough, or ask the author for an ESL version)",
                        ["newRecords"] = scan.NewRecords,
                        ["overrides"] = scan.Overrides,
                        ["objectIds"] = scan.NewRecords > 0 ? $"0x{scan.MinNewId:X3}-0x{scan.MaxNewId:X3}" : null,
                        ["caution"] = scan.NewCells > 0 ? $"adds {scan.NewCells} cell/worldspace records (see the mod's page first)" : null,
                    }));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { errors.Add($"{name}: {ex.Message}"); }
            });
            var list = new JsonArray(rows.OrderBy(r => r.order).ThenBy(r => r.order == 2 ? (int)r.row["newRecords"]! : 0).ThenBy(r => (int?)r.row["position"])
                .Select(r => (JsonNode)r.row).ToArray());
            candidates = new JsonObject { ["items"] = list };
            foreach (var e in errors) unreadable.Add(e);
        }

        var verdict = fullFree < 0
            ? $"Over the limit: {full.Count} full plugins, {-fullFree} more than the game's 254. It can't load them all; make some light (see lightEligible) or disable some."
            : fullFree < 10
                ? $"Close to the limit: {full.Count} of 254 full plugin slots used, {fullFree} left."
                : $"Within limits: {full.Count} of 254 full plugin slots used ({fullFree} free), {light.Count} of 4,096 light.";
        if (lightFree < 0) verdict += $" Light plugins are over their limit too: {light.Count} of 4,096.";
        else if (lightFree < 50) verdict += $" Light plugins are close to their limit too: {light.Count} of 4,096, {lightFree} left.";
        return new
        {
            verdict,
            loadOrder = lo.Source,
            active = lo.Active.Count,
            full = new { count = full.Count, limit = PluginFile.FullPluginLimit, headroom = fullFree, baseGameAndCreationClub = full.Count(IsBaseOrCc) },
            light = new { count = light.Count, limit = PluginFile.LightPluginLimit, headroom = lightFree, creationClub = light.Count(IsBaseOrCc) },
            missingFiles = lo.MissingFiles.Count > 0 ? new { count = lo.MissingFiles.Count, examples = lo.MissingFiles.Take(15), note = "enabled in Plugins.txt but not installed: the game skips them" } : null,
            unreadable = unreadable.IsEmpty ? null : unreadable.Take(10).ToList(),
            lightEligible = candidates == null ? null : new
            {
                asIs,
                withHeader171 = with171,
                onlyByCompacting = compacting,
                tooManyRecords = cannot,
                checkedPlugins = asIs + with171 + compacting + cannot,
                list = PageResponse(candidates, "items", Math.Max(page, 1), Math.Clamp(pageSize, 1, 200)),
            },
            notes = new[]
            {
                "Light plugins share slot FE; each holds up to 4,096 forms (2,048 with a 1.70 header).",
                "Making a plugin light is only safe when its FormIDs don't change: 'as is' and 1.71 candidates keep them (listed first). Compacting renumbers forms and breaks saves that use them (listed last, smallest first).",
                "The list counts what Plugins.txt enables; the running game may differ if it was changed since the game started.",
            },
            seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
        };
    }

    // Creation Club files are named cc<author>sse<number> (ccBGSSSE001-Fish.esm, ccQDRSSE001-SurvivalMode.esl).
    static readonly System.Text.RegularExpressions.Regex CreationClubName = new(@"^cc[a-z]{3}sse\d{3}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static bool IsBaseOrCc(string name) => LoadOrder.BaseMasters.Contains(name, StringComparer.OrdinalIgnoreCase) || CreationClubName.IsMatch(name) ||
                                           name.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);   // Anniversary Edition's shared assets

    [McpServerTool]
    [Description("Write a small new plugin (FEAT-273) into SkyLink's Build folder (Documents\\My Games\\<game>\\SkyLinkAI\\Build\\<name>\\), " +
        "never into Data or a mod folder: installing it is a separate step. name: file name (.esp added if missing; .esm and .esl " +
        "allowed). records: JSON array; supported now: " +
        "{\"type\":\"GLOB\",\"editorId\":\"MyGlobal\",\"valueType\":\"short|long|float\",\"value\":1,\"constant\":false} and " +
        "{\"type\":\"QUST\",\"editorId\":\"MyQuest\",\"name\":\"My Quest\",\"startGameEnabled\":true,\"runOnce\":false,\"priority\":50," +
        "\"scripts\":[{\"name\":\"MyQuestScript\",\"properties\":[{\"name\":\"Count\",\"type\":\"int\",\"value\":3}," +
        "{\"name\":\"PlayerRef\",\"type\":\"object\",\"value\":\"Skyrim.esm|0x00000014\"},{\"name\":\"MyGlobal\",\"type\":\"object\",\"value\":\"MyGlobal\"}]}]}. " +
        "Property types: int, float, bool, string, object (Plugin.esm|FormID, or the editor ID of a record made in the same call). " +
        "masters: plugins it depends on (plugins named in object properties are added); put in load order. light (default true) " +
        "makes it ESL-flagged (up to 4,095 records, object IDs from 0x800). dryRun (default true) builds it in memory and reports " +
        "what would be written; dryRun false writes the file. Either way the result is read back with the plugin reader (round " +
        "trip) and its masters checked. Script .pex files are not written or checked: the scripts must exist in the game's Scripts.")]
    public async Task<object> CreatePlugin(string name,
        [Description("JSON array of record objects (or the array as a JSON string).")] JsonElement records, string[]? masters = null, string? author = null,
        string? description = null, bool light = true, bool dryRun = true)
    {
        return await Task.Run<object>(() =>
        {
            name = (name ?? "").Trim();
            if (Path.GetExtension(name).Length == 0) name += ".esp";
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is not (".esp" or ".esm" or ".esl")) return new { error = "name must end in .esp, .esm or .esl (or have no extension: .esp is added)." };
            if (Path.GetFileNameWithoutExtension(name).Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
                return new { error = $"'{name}' isn't a usable file name." };
            if (LoadOrder.BaseMasters.Contains(name, StringComparer.OrdinalIgnoreCase)) return new { error = $"{name} is a base game file name: pick another." };

            List<NewRecord> parsed;
            try
            {
                var node = records.ValueKind == JsonValueKind.String ? JsonNode.Parse(records.GetString() ?? "") : JsonNode.Parse(records.GetRawText());
                parsed = PluginWriter.ParseRecords(node);
            }
            catch (JsonException ex) { return new { error = "records isn't valid JSON: " + ex.Message }; }
            catch (ArgumentException ex) { return new { error = ex.Message }; }

            LoadOrderState? lo = null;
            try { lo = LoadOrder.Read(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            int? Position(string m)
            {
                var b = Array.FindIndex(LoadOrder.BaseMasters, x => x.Equals(m, StringComparison.OrdinalIgnoreCase));
                return b >= 0 ? b : lo?.IndexOf(m);
            }
            BuiltPlugin built;
            try
            {
                built = PluginWriter.Build(new NewPlugin
                {
                    Name = name, Author = author, Description = description, Light = light || ext == ".esl",
                    Masters = (masters ?? []).SelectMany(m => m.Split(',')).Select(m => m.Trim()).Where(m => m.Length > 0).ToList(),
                    Records = parsed,
                }, Position);
            }
            catch (ArgumentException ex) { return new { error = ex.Message }; }

            var folder = Path.Combine(SkyLinkSettings.Folder, "Build", Path.GetFileNameWithoutExtension(name));
            var file = Path.Combine(folder, name);
            var existed = File.Exists(file);
            if (!dryRun)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    var temp = file + ".tmp";
                    File.WriteAllBytes(temp, built.Bytes);
                    File.Move(temp, file, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new { error = $"Couldn't write {file}: {ex.Message}" }; }
            }

            // The round trip reads what was written (the file, or the bytes for a dry run) with the same reader as GetPluginInfo.
            object roundTrip;
            PluginHeader? readBack = null;
            try
            {
                using Stream s = dryRun ? new MemoryStream(built.Bytes, false) : PluginFile.OpenRead(file);
                var scan = PluginFile.Scan(s, name);
                readBack = scan.Header;
                s.Position = 0;
                // Every record: the reader's default stops at 200, which failed the check of a 500-record plugin (2026-11-22).
                var recs = PluginFile.ReadRecords(s, name, max: built.Assigned.Count + 1);
                var e = PluginFile.CheckLight(scan);
                var expected = built.Assigned.Select(a => a.FormId).OrderBy(x => x).ToList();
                var found = recs.Select(r => Convert.ToUInt32(r["formId"]!.ToString()[2..], 16)).OrderBy(x => x).ToList();
                var matches = scan.Records == built.Assigned.Count && scan.Records + scan.Groups == scan.Header.NumRecords && expected.SequenceEqual(found) && !scan.Truncated;
                roundTrip = new
                {
                    ok = matches,
                    header = new { version = scan.Header.VersionText, flags = $"0x{scan.Header.Flags:X}", numRecords = scan.Header.NumRecords, nextObjectId = $"0x{scan.Header.NextObjectId:X6}", masters = scan.Header.Masters, author = scan.Header.Author },
                    counted = new { records = scan.Records, groups = scan.Groups, added = scan.NewRecords, overrides = scan.Overrides },
                    light = new { verdict = e.Verdict, eligible = e.Eligible },
                    records = recs.Take(40).ToList(),
                    recordsNotShown = Math.Max(0, recs.Count - 40),
                };
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                roundTrip = new { ok = false, error = "Reading it back failed: " + ex.Message };
            }

            var deps = lo == null || readBack == null ? null : MasterStatuses(readBack, lo, name);
            var depProblems = deps?.Where(d => d.status != "ok").Select(d => $"{d.name}: {d.detail}").ToList();
            return new
            {
                dryRun,
                written = dryRun ? null : file,
                wouldWrite = dryRun ? file : null,
                replacedEarlierBuild = !dryRun && existed ? true : (bool?)null,
                sizeBytes = built.Bytes.Length,
                plugin = new
                {
                    name,
                    light = (built.Flags & 0x200) != 0,
                    master = (built.Flags & 0x1) != 0,
                    headerVersion = built.Version.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    numRecords = built.NumRecords,
                    nextObjectId = $"0x{built.NextObjectId:X6}",
                    masters = built.Masters,
                },
                records = built.Assigned.Select(a => new { type = a.Type, editorId = a.EditorId, formId = $"0x{a.FormId:X8}", objectId = $"0x{a.FormId & 0xFFFFFF:X3}" }),
                roundTrip,
                masterProblems = depProblems is { Count: > 0 } ? depProblems : null,
                alreadyInLoadOrder = lo?.FileOf(name) is string other ? $"A plugin named {name} is already installed ({other}): installing this build would replace it." : null,
                notes = built.Notes.Concat(new[]
                {
                    "Installing (copying into Data or a mod folder and enabling it) is a separate step; this only builds the file.",
                    parsed.OfType<NewQuest>().Any(q => q.Scripts.Count > 0)
                        ? "The quest's scripts must exist as compiled .pex files (loose in Data\\Scripts or in a BSA) with matching property names, or the game attaches nothing." : null,
                    (built.Flags & 0x200) != 0 && built.Version > 1.705f ? "This plugin's 1.71 header needs game version 1.6.1130 or later." : null,
                    (built.Flags & 0x200) == 0 ? "A full (not light) plugin takes one of the 254 full slots: GetLoadOrderLimits shows how many are free." : null,
                }.OfType<string>()),
            };
        });
    }

    // ---- helpers

    sealed record MasterInfo(string name, bool found, bool enabled, bool? loadsBefore, string status, string detail, string? file);

    static List<MasterInfo> MasterStatuses(PluginHeader h, LoadOrderState lo, string pluginName)
    {
        var list = new List<MasterInfo>();
        foreach (var m in h.Masters)
        {
            var file = lo.FileOf(m);
            if (file == null)
            {
                list.Add(new MasterInfo(m, false, false, null, "missing",
                    $"not installed (no {m} in {(lo.Folders.Count > 1 ? "the Data folder, MO2's mods or overwrite" : "the Data folder")}): install the mod that provides it.", null));
                continue;
            }
            if (lo.IndexOf(m) == null)
            {
                list.Add(new MasterInfo(m, true, false, null, "disabled",
                    lo.Disabled.Contains(m) ? "installed but disabled: enable it in the mod manager." : "installed but not in the load order: enable it in the mod manager.", file));
                continue;
            }
            bool masterIsMaster;
            try { masterIsMaster = LoadOrder.Header(file).IsMaster; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { masterIsMaster = Path.GetExtension(m).ToLowerInvariant() is ".esm" or ".esl"; }
            var before = LoadOrder.LoadsBefore(lo, m, masterIsMaster, pluginName, h.IsMaster);
            list.Add(before
                ? new MasterInfo(m, true, true, true, "ok", "installed, enabled, loads before it.", null)
                : new MasterInfo(m, true, true, false, "loads after",
                    h.IsMaster && !masterIsMaster
                        ? $"it's a plain plugin but {pluginName} is a master, and the game loads every master first: {pluginName} can't depend on it this way."
                        : $"loads after {pluginName}: move it above (LOOT or the mod manager's sort fixes this).", file));
        }
        return list;
    }

    static (string? path, string? error) Resolve(string plugin, LoadOrderState lo)
    {
        var p = (plugin ?? "").Trim().Trim('"');
        if (p.Length == 0) return (null, "Give a plugin name (as in the load order) or a full path.");
        if (p.Contains('\\') || p.Contains('/') || p.Contains(':'))
            return File.Exists(p) ? (Path.GetFullPath(p), null) : (null, $"No file at {p}.");
        if (lo.Folders.Count == 0)
            return (null, "The game's Data folder isn't known: give a full path, or set skyrimPath (or dataPath, mo2Path) with SetSkyLinkSetting.");
        if (lo.FileOf(p) is string f) return (f, null);
        if (Path.GetExtension(p).Length == 0)
            foreach (var ext in new[] { ".esp", ".esm", ".esl" })
                if (lo.FileOf(p + ext) is string g) return (g, null);
        var matches = lo.Files.Keys.Where(k => k.Contains(p, StringComparison.OrdinalIgnoreCase)).OrderBy(k => k.Length).Take(8).ToList();
        if (matches.Count == 1) return (lo.Files[matches[0]], null);
        return (null, matches.Count > 1
            ? $"'{plugin}' matches several plugins: {string.Join(", ", matches)}. Give the full name."
            : $"No plugin named '{plugin}' in {string.Join("; ", lo.Folders.Take(3))}{(lo.Folders.Count > 3 ? $" (and {lo.Folders.Count - 3} more folders)" : "")}. A full path reads any plugin file.");
    }

    static List<string>? Warnings(PluginScan s)
    {
        var w = new List<string>();
        if (s.Truncated) w.Add("The file ends early (cut off or damaged): the game may crash loading it.");
        if (s.OddMasterIndex > 0) w.Add($"{s.OddMasterIndex} records have a master index past its masters list (the game reads them as this plugin's own); usually a sign of a hand-edited or broken plugin.");
        if (s.Header.Localized) w.Add("Localized: its names live in .STRINGS files next to it (or in its BSA).");
        if (s.Header.Version is not (>= 1.69f and <= 1.72f) && s.Header.Version != 0.94f) w.Add($"Unusual header version {s.Header.VersionText}.");
        return w.Count > 0 ? w : null;
    }
}
