using System.Reflection;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// MCP Tools for player state queries — info, skills, perks, spells, shouts, appearance, factions
/// </summary>
[McpServerToolType]
public class PlayerTools : ToolBase
{
    public PlayerTools(IPipeClient pipe) : base(pipe) { }

    // Which server build is answering: its version, when its DLL was written, and where it runs from.
    private static readonly string ServerPath = typeof(PlayerTools).Assembly.Location;
    private static readonly string? ServerVersion = typeof(PlayerTools).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
    private static readonly string? ServerBuilt = File.Exists(ServerPath)
        ? File.GetLastWriteTimeUtc(ServerPath).ToString("yyyy-MM-dd HH:mm:ss") + " UTC"
        : null;

    // Set by SkyLinkLauncher when it runs this server from a shadow copy (see SkyLinkLauncher/Program.cs).
    private static readonly string? LauncherVersion = Environment.GetEnvironmentVariable("SKYLINK_LAUNCHER_VERSION");
    private static readonly string? ServerSource = Environment.GetEnvironmentVariable("SKYLINK_SERVER_SOURCE");

    [McpServerTool]
    [Description("Wait until Skyrim is running with SkyLink's plugin answering, then report the connection like CheckConnection. " +
        "Use this when a call fails because the game isn't running or is still starting, instead of retrying by hand. " +
        "Checks every 2 seconds for up to timeoutSeconds (default 120, max 600). The plugin answers from the main menu onward.")]
    public async Task<object> WaitForSkyrim(int timeoutSeconds = 120)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 600);
        var started = DateTime.UtcNow;
        var deadline = started.AddSeconds(timeoutSeconds);
        var attempts = 0;
        while (true)
        {
            attempts++;
            if (await _pipe.PingAsync())
            {
                var waited = (int)(DateTime.UtcNow - started).TotalSeconds;
                if (attempts > 1) await NotifyInGame("SkyLink connected");
                return new { waitedSeconds = waited, attempts, connection = await CheckConnection() };
            }
            if (DateTime.UtcNow >= deadline) break;
            var delay = TimeSpan.FromSeconds(2);
            var left = deadline - DateTime.UtcNow;
            await Task.Delay(left < delay ? left : delay);
        }

        return new
        {
            connected = false,
            waitedSeconds = timeoutSeconds,
            attempts,
            message = PipeClient.DescribeConnectFailure()
        };
    }

    [McpServerTool]
    [Description("Check if Skyrim is running and the SKSE MCP plugin is connected. Also reports which plugin and server builds " +
        "are loaded (version, build time, server path), so a stale build is easy to spot. 'logic' describes the plugin's " +
        "reloadable logic DLL (version, build time, when it was loaded, reload count). When the server runs under " +
        "SkyLinkLauncher, launcherVersion is set, serverPath is the shadow copy and serverSource is the deployed DLL. " +
        "diagnostics (1.3+) shows the game's store (Steam, GOG, Epic), whether the Address Library file for this game build is " +
        "present, and gameThread counters: if tasksQueued is above 0 and tasksRun is 0, SKSE never runs SkyLink's game-thread " +
        "calls, which is why every other tool fails; diagnostics.problem then says what to check.")]
    public async Task<object> CheckConnection()
    {
        try
        {
            // Plugin 1.1.0+ reports its version and the game runtime; 1.2.0+ also its link time.
            var info = await _pipe.SendRequestAsync("ping");
            // BUG-087: a stalled game thread (tasks ran, then stopped) usually means the window is unfocused. The plugin says so
            // itself from this build; older host DLLs don't, so work it out here too.
            string? stalled = null;
            var gt = info?["diagnostics"]?["gameThread"];
            if (info?["diagnostics"]?["problem"] == null && gt != null &&
                (gt["pending"]?.GetValue<int>() ?? 0) > 0 && (gt["tasksRun"]?.GetValue<long>() ?? 0) > 0 &&
                (gt["lastTaskRanSecondsAgo"] as JsonValue)?.GetValue<double>() > 5)
                stalled = "The game has stopped running SkyLink's tasks. Usually its window is in the background and the game " +
                          "pauses when unfocused: click into the game, or set bAlwaysActive=1 under [General] in Skyrim.ini.";
            return new
            {
                connected = true,
                message = stalled ?? "Connected to Skyrim via SKSE plugin",
                stalled = stalled != null ? true : (bool?)null,
                pluginVersion = info?["pluginVersion"]?.GetValue<string>(),
                pluginBuilt = info?["pluginBuilt"]?.GetValue<string>(),
                gameVersion = info?["gameVersion"]?.GetValue<string>(),
                loadingScreen = info?["loadingScreen"]?.GetValue<bool>(),  // 1.2.1+
                logic = info?["logic"] is JsonNode logicNode ? DeserializeResponse(logicNode) : null,
                // 1.3+: store, Address Library file, and whether game-thread calls run at all (BUG-012)
                diagnostics = info?["diagnostics"] is JsonNode diag ? DeserializeResponse(diag) : null,
                serverVersion = ServerVersion,
                serverBuilt = ServerBuilt,
                serverPath = ServerPath,
                serverSource = ServerSource,
                launcherVersion = LauncherVersion
            };
        }
        catch (Exception ex)
        {
            return new
            {
                connected = false,
                message = ex.Message,
                serverVersion = ServerVersion,
                serverBuilt = ServerBuilt,
                serverPath = ServerPath,
                serverSource = ServerSource,
                launcherVersion = LauncherVersion
            };
        }
    }

    [McpServerTool]
    [Description("Get player information including name, race, level, health, magicka, stamina, and current location")]
    public async Task<object> GetPlayerInfo()
    {
        var data = await _pipe.SendRequestAsync("get_player_info");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get the player's current gold count")]
    public async Task<object> GetGoldCount()
    {
        var data = await _pipe.SendRequestAsync("get_gold_count");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get all 18 skill levels (current and base values) plus player level.")]
    public async Task<object> GetSkillLevels()
    {
        var data = await _pipe.SendRequestAsync("get_skill_levels");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get all perks the player has acquired, with rank information.")]
    public async Task<object> GetPerks(int page = 1, int pageSize = 50)
    {
        var data = await _pipe.SendRequestAsync("get_perks");
        return PageResponse(data, "perks", page, pageSize);
    }

    [McpServerTool]
    [Description("Get spells the player has learned. Supports paging: page (default 1), pageSize (default 50). " +
        "Set page=0 for summary only (just totalItems count). Returns school, type, magnitude per spell.")]
    public async Task<object> GetKnownSpells(int page = 1, int pageSize = 50)
    {
        var data = await _pipe.SendRequestAsync("get_known_spells");
        return PageResponse(data, "spells", page, pageSize);
    }

    [McpServerTool]
    [Description("Get all shouts the player knows, including per-word unlock status and recovery times.")]
    public async Task<object> GetKnownShouts()
    {
        var data = await _pipe.SendRequestAsync("get_known_shouts");
        // Per-word status, as the description promises (it was missing: 2026-11-20), and the word in plain letters.
        if (data?["shouts"] is JsonArray shouts)
            foreach (var w in shouts.OfType<JsonObject>().SelectMany(sh => (sh["words"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()))
            {
                if (w["name"]?.ToString() is string raw && raw.Any(char.IsDigit)) w["word"] = DragonWord(raw);
                if (w["formId"]?.ToString() is not string wid) continue;
                try
                {
                    var r = await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject { ["className"] = "Game", ["functionName"] = "IsWordUnlocked", ["args"] = new JsonArray(wid) });
                    if (r?["result"] is JsonValue v && v.TryGetValue<bool>(out var unlocked)) w["unlocked"] = unlocked;
                }
                catch (Exception) { }
            }
        return DeserializeResponse(data);
    }

    // Word of power names are stored in the dragon font's spelling, digits for double letters (D4 = Dah, N4 = Nah).
    internal static string DragonWord(string raw)
    {
        var map = new Dictionary<char, string> { ['2'] = "aa", ['3'] = "ei", ['4'] = "ah", ['6'] = "ii", ['7'] = "ir", ['8'] = "oo", ['9'] = "uu" };
        var text = string.Concat(raw.Select(c => map.TryGetValue(c, out var d) ? d : c.ToString())).ToLowerInvariant();
        return text.Length == 0 ? raw : char.ToUpperInvariant(text[0]) + text[1..];
    }

    [McpServerTool]
    [Description("Get the player's appearance data: race, sex, weight, height, all 19 face morph values, " +
        "face presets, and head parts (hair, eyes, etc.). Use this for character recreation or face morphing. " +
        "Each head part has typeName, menuIndex (the number RaceMenu's slider shows for it, 0-based, editor IDs in case-sensitive order) " +
        "and menuChoices (how many playable choices of that type for this race and sex; RaceMenu may show one or two more). " +
        "ListHeadPartChoices lists the parts around a slider number. " +
        "Hair colour and skin tone: GetHairAndSkinColor. refId reads an NPC instead (head parts, morphs, and faceGenMesh: whether " +
        "the NPC has a pre-built face, which a runtime head part change would no longer match).")]
    public async Task<object> GetAppearance(string? refId = null)
    {
        var parms = new JsonObject();
        if (!string.IsNullOrWhiteSpace(refId)) parms["refId"] = refId.Trim();
        var data = await _pipe.SendRequestAsync("get_appearance", parms);
        if (data is JsonObject o && o["faceGenMesh"]?.GetValue<string>() is string mesh)
            o["faceGenMeshSource"] = SkyrimMCP.Face.MeshFiles.Source(mesh) ?? "none (the face is built at runtime)";
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get the player's favorited spells and items (the ones assigned to the favorites menu/hotkeys).")]
    public async Task<object> GetFavorites()
    {
        var data = await _pipe.SendRequestAsync("get_favorites");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Export a complete character blueprint as JSON. Includes player info, all 18 skills, perks, " +
        "known spells, known shouts, equipped items, full inventory, gold, and active effects. " +
        "Use this to save a character template that can be re-applied to a new character. " +
        "IMPORTANT: Pass an outputPath to write the blueprint directly to a file on disk — the data is too large " +
        "for the MCP response. Example outputPath: 'C:\\Users\\<you>\\Documents\\blueprint.json'. " +
        "Every form ID also carries its plugin name and local ID (formIdSource), so ApplyBlueprint still finds modded items after a load-order change. " +
        "Also records quest progress (quests completed or started, with current stage, the stages done (read from a save made " +
        "at export: stagesFromSave, default true), objectives seen with their state, and type; ReplayQuests uses it) and " +
        "notCaptured: what a blueprint " +
        "can't hold (quest rewards and world changes, map markers, crime, other mods' script state).")]
    public async Task<object> GetCharacterBlueprint(string? outputPath = null, bool stagesFromSave = true)
    {
        var data = await _pipe.SendRequestAsync("get_character_blueprint");
        // FEAT-121: which shout words were unlocked (no native read; Papyrus Game.IsWordUnlocked).
        if (data?["shouts"]?["shouts"] is JsonArray exportShouts)
            foreach (var w in exportShouts.OfType<JsonObject>().SelectMany(sh => (sh["words"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()))
            {
                if (w["formId"]?.ToString() is not string wid) continue;
                try
                {
                    var r = await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject { ["className"] = "Game", ["functionName"] = "IsWordUnlocked", ["args"] = new JsonArray(wid) });
                    if (r?["result"] is JsonValue v && v.TryGetValue<bool>(out var unlocked)) w["unlocked"] = unlocked;
                }
                catch (Exception) { }
            }
        // FEAT-263: stages done per quest come from a save (the engine's in-memory list read only zeros): a save made now.
        if (stagesFromSave && data?["quests"] is JsonObject quests)
        {
            var (save, stages, note) = await new SaveTools(_pipe).QuestStagesFromSave("SkyLink_Blueprint");
            if (stages != null)
                foreach (var list in new[] { quests["completed"], quests["running"] }.OfType<JsonArray>())
                    foreach (var q in list.OfType<JsonObject>())
                        if (q["formId"]?.ToString() is string id && stages.TryGetValue(id.ToUpperInvariant().PadLeft(8, '0'), out var st))
                            q["stagesDone"] = new JsonArray(st.Where(x => x[1] == 1).Select(x => (JsonNode)x[0]).ToArray());
            quests["stagesSource"] = save;
            if (note != null) quests["stagesNote"] = note;
        }

        if (!string.IsNullOrEmpty(outputPath) && data != null)
        {
            try
            {
                var jsonString = data.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(outputPath, jsonString);
                await NotifyInGame("Blueprint exported");

                // Return summary instead of full data
                var spellCount = data["spells"]?["count"]?.GetValue<int>() ?? 0;
                var perkCount = data["perks"]?["count"]?.GetValue<int>() ?? 0;
                var shoutCount = data["shouts"]?["count"]?.GetValue<int>() ?? 0;
                var itemCount = data["inventory"]?["items"]?.AsArray()?.Count ?? 0;
                var effectCount = data["activeEffects"]?["effects"]?.AsArray()?.Count ?? 0;

                return new
                {
                    success = true,
                    outputPath,
                    message = $"Blueprint saved to {outputPath}",
                    summary = new
                    {
                        spells = spellCount,
                        perks = perkCount,
                        shouts = shoutCount,
                        inventoryItems = itemCount,
                        activeEffects = effectCount
                    }
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to write blueprint: {ex.Message}" };
            }
        }

        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Apply the inventory, equipment and gold of a character blueprint file (from GetCharacterBlueprint) to the player. " +
        "dryRun defaults to TRUE: first show the player what would change, then call again with dryRun=false once they agree. " +
        "sections (comma-separated, default 'inventory,equipment,gold'; also 'skills' (raises skills below the blueprint's base " +
        "levels; lowerSkills=true also lowers), 'level' (raises the level; perk points and level-up choices aren't given), " +
        "'perks', 'spells' (spellKinds, default 'spell,power,lesserPower'; add 'ability' for abilities, which include racial " +
        "ones, vampirism and mods' blessings; 'disease' and 'addiction' exist too) and 'shouts' (adds the ones she lacks; shout words the other character had unlocked are taught and " +
        "unlocked), all add-only; add 'quests' to compare the blueprint's quest progress " +
        "with this game's: listed only, never applied). Only missing items are added (blueprint count minus what " +
        "the player has); nothing is ever removed, and gold only goes up unless exactGold=true. Then the blueprint's equipped " +
        "armor and weapons are equipped (an item to equip that she lacks is added, even with only the equipment section; spells she " +
        "doesn't know are skipped, since spells aren't restored; worn items not in the blueprint stay on). Items are found by plugin name and local ID, so a different load order works; " +
        "items from plugins that aren't loaded, items created in game, and blueprints from before 1.3 whose form IDs now point at " +
        "a different item are listed as unresolved. Player-made enchantments are not restored (the plain item is added).")]
    public async Task<object> ApplyBlueprint(string path, bool dryRun = true, string sections = "inventory,equipment,gold", bool exactGold = false, bool lowerSkills = false, string spellKinds = "spell,power,lesserPower")
    {
        JsonNode? blueprint;
        try
        {
            blueprint = JsonNode.Parse(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex)
        {
            return new { error = $"Could not read the blueprint at {path}: {ex.Message}" };
        }
        if (blueprint is not JsonObject bp) return new { error = "The file isn't a blueprint (expected a JSON object)" };

        // FEAT-262: quests are compared, never applied: forcing a stage skips rewards, world changes and follow-on triggers.
        object? questReport = null;
        if (sections.Contains("quests", StringComparison.OrdinalIgnoreCase))
            questReport = await CompareQuests(bp);
        var parms = new JsonObject
        {
            ["sections"] = new JsonArray(sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => (JsonNode)s.ToLowerInvariant()).ToArray()),
            ["dryRun"] = dryRun,
            ["exactGold"] = exactGold,
            ["inventory"] = bp["inventory"]?["items"]?.DeepClone() ?? new JsonArray(),
            ["equipped"] = bp["equipped"]?["equipped"]?.DeepClone() ?? new JsonArray(),
            // FEAT-121
            ["skills"] = bp["skills"]?["skills"]?.DeepClone() ?? new JsonArray(),
            ["perks"] = bp["perks"]?["perks"]?.DeepClone() ?? new JsonArray(),
            // Castable spells and powers only by default: Arya's export held 1,421 "spells", including abilities (racial,
            // vampirism, mods' blessings), diseases and addictions, which shouldn't be handed to another character unasked.
            ["spells"] = new JsonArray((bp["spells"]?["spells"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Where(sp => spellKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains(sp["type"]?.ToString() ?? "spell", StringComparer.OrdinalIgnoreCase))
                .Select(sp => (JsonNode)sp.DeepClone()).ToArray()),
            ["shouts"] = bp["shouts"]?["shouts"]?.DeepClone() ?? new JsonArray(),
            ["lowerSkills"] = lowerSkills,
        };
        if ((bp["skills"]?["playerLevel"] ?? bp["character"]?["level"]) is JsonNode lvl) parms["level"] = lvl.DeepClone();
        if (bp["gold"]?["gold"] is JsonNode gold) parms["gold"] = gold.DeepClone();

        // A script reacting to the new perks, spells or level can switch off player controls and not give them back (mouse look
        // dead in game after every apply, 2026-11-20). Note what was on before, restore what the apply's aftermath turned off.
        async Task<HashSet<string>> DisabledControls()
        {
            try { return ((await _pipe.SendRequestAsync("get_controls"))?["disabled"] as JsonArray ?? new JsonArray()).Select(n => n?.ToString() ?? "").ToHashSet(); }
            catch (Exception) { return []; }
        }
        var controlsBefore = dryRun ? [] : await DisabledControls();
        // Quests running before, to name the ones the apply set off (Magic Student starts once she knows a ward: 2026-11-20).
        async Task<Dictionary<string, string>> RunningQuests()
        {
            try
            {
                var q = await _pipe.SendRequestAsync("export_quests");
                return (q?["running"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .GroupBy(x => x["formId"]?.ToString() ?? "").ToDictionary(g => g.Key, g => g.First()["name"]?.ToString() ?? g.Key);
            }
            catch (Exception) { return []; }
        }
        var questsBefore = dryRun ? [] : await RunningQuests();
        // Spells and shouts she has before, to list ones a mod granted on top of the apply (Burst Voice on learning FUS).
        async Task<Dictionary<string, string>> KnownMagic()
        {
            var map = new Dictionary<string, string>();
            try
            {
                foreach (var key in new[] { ("get_known_spells", "spells"), ("get_known_shouts", "shouts") })
                    foreach (var x in ((await _pipe.SendRequestAsync(key.Item1))?[key.Item2] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        if (x["formId"]?.ToString() is string id) map[id] = x["name"]?.ToString() ?? id;
            }
            catch (Exception) { }
            return map;
        }
        var magicBefore = dryRun ? [] : await KnownMagic();
        var data = await _pipe.SendRequestAsync("apply_blueprint_items", parms);
        if (!dryRun && data?["error"] == null) await NotifyInGame("Blueprint applied");
        object? controls = dryRun ? null : "unchanged";
        // On a real apply these are always present (an empty result read as "missing" in the round of 2026-11-20).
        object? questsStarted = dryRun ? null : "none";
        object? alsoGained = dryRun ? null : "none";
        if (!dryRun)
        {
            await Task.Delay(3000);
            var fromBlueprint = new HashSet<string>(new[] { bp["spells"]?["spells"], bp["shouts"]?["shouts"] }.OfType<JsonArray>()
                .SelectMany(a => a.OfType<JsonObject>()).Select(x => x["formId"]?.ToString() ?? ""));
            var extra = (await KnownMagic()).Where(kv => !magicBefore.ContainsKey(kv.Key) && !fromBlueprint.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            if (extra.Count > 0)
                alsoGained = new { spellsAndShouts = extra.Take(20), note = "Granted by another mod reacting to the apply (not in the blueprint)." };
            var startedNow = (await RunningQuests()).Where(kv => !questsBefore.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
            if (startedNow.Count > 0)
                questsStarted = new { quests = startedNow.Take(20),
                                      note = "Started during or just after the apply: quests that trigger on a known spell, skill level or level (a mage's ward practice, trainers, radiant jobs). One that holds the controls shows under controls." };
            var turnedOff = (await DisabledControls()).Except(controlsBefore).ToList();
            if (turnedOff.Count > 0)
            {
                try
                {
                    await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject
                    {
                        ["className"] = "Game", ["functionName"] = "EnablePlayerControls",
                        ["args"] = new JsonArray(true, true, true, true, true, true, true, true, 0),
                    });
                }
                catch (Exception) { }
                var still = (await DisabledControls()).Except(controlsBefore).ToList();
                controls = new
                {
                    turnedOffDuringApply = turnedOff,
                    restored = still.Count == 0,
                    stillOff = still.Count > 0 ? still : null,
                    note = "A script reacting to the new perks, spells or level switched these controls off (a mod's or a radiant quest's scene); SkyLink switched them back on. If a quest scene was meant to hold them, it may need finishing.",
                };
            }
        }
        // FEAT-121: shout words the other character had unlocked are taught and unlocked through Papyrus (no native call).
        object? words = null;
        if (sections.Contains("shouts", StringComparison.OrdinalIgnoreCase) && bp["shouts"]?["shouts"] is JsonArray shoutList)
        {
            var resolve = await PortableResolver();
            var taught = new List<string>();
            var alreadyUnlocked = new List<string>();
            var skipped = 0;
            foreach (var w in shoutList.OfType<JsonObject>().SelectMany(sh => (sh["words"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()))
            {
                if (w["unlocked"]?.GetValue<bool>() != true) { skipped++; continue; }
                if (resolve(w) is not string id) continue;
                var label = w["name"]?.ToString() is string n && n.Any(char.IsDigit) ? DragonWord(n) : w["name"]?.ToString() ?? id;
                // Already unlocked here: list apart, don't "teach" again.
                try
                {
                    var have = await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject { ["className"] = "Game", ["functionName"] = "IsWordUnlocked", ["args"] = new JsonArray(id) });
                    if (have?["result"] is JsonValue hv && hv.TryGetValue<bool>(out var already) && already) { alreadyUnlocked.Add(label); continue; }
                }
                catch (Exception) { }
                if (!dryRun)
                {
                    try
                    {
                        await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject { ["className"] = "Game", ["functionName"] = "TeachWord", ["args"] = new JsonArray(id) });
                        await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject { ["className"] = "Game", ["functionName"] = "UnlockWord", ["args"] = new JsonArray(id) });
                    }
                    catch (Exception) { continue; }
                }
                taught.Add(label);
            }
            words = new
            {
                taughtAndUnlocked = taught,
                alreadyUnlocked,
                notUnlockedThere = skipped,
                note = skipped > 0 && taught.Count == 0 && shoutList.Count > 0 && !shoutList.OfType<JsonObject>().Any(sh => (sh["words"] as JsonArray)?.OfType<JsonObject>().Any(x => x["unlocked"] != null) == true)
                    ? "This blueprint doesn't record which words were unlocked (exported before 1.4): export it again to bring words across."
                    : null,
            };
        }
        var result = DeserializeResponse(data);
        return new
        {
            blueprint = path,
            exportedBy = bp["exportSource"]?.GetValue<string>(),
            portableIds = bp["formatVersion"]?.GetValue<int>() >= 2,
            result,
            shoutWords = words,
            controls,
            questsStarted,
            alsoGained,
            quests = questReport,
        };
    }

    // Blueprint quests against this game's: what the blueprint's character had completed or running that this one hasn't.
    private async Task<object> CompareQuests(JsonObject bp)
    {
        if (bp["quests"] is not JsonObject saved) return new { note = "This blueprint has no quests section (exported before 1.3)." };
        var now = await _pipe.SendRequestAsync("export_quests");
        static string Key(JsonNode? q) =>
            q?["formIdSource"] is JsonObject src && src["plugin"] != null
                ? $"{src["plugin"]}:{src["localId"]}".ToLowerInvariant()
                : (q?["editorId"]?.ToString() ?? q?["name"]?.ToString() ?? "").ToLowerInvariant();
        var doneHere = new HashSet<string>((now?["completed"] as JsonArray ?? new JsonArray()).Select(Key));
        var runningHere = new HashSet<string>((now?["running"] as JsonArray ?? new JsonArray()).Select(Key));
        var nowByKey = new Dictionary<string, JsonNode?>();
        foreach (var q in (now?["completed"] as JsonArray ?? new JsonArray()).Concat(now?["running"] as JsonArray ?? new JsonArray()))
            nowByKey[Key(q)] = q;
        object Row(JsonNode? q, string here)
        {
            nowByKey.TryGetValue(Key(q), out var mine);
            var objectivesThere = (q?["objectives"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Where(o => o["state"]?.ToString() == "completed").Select(o => o["text"]?.ToString()).ToList();
            var objectivesHere = new HashSet<string?>((mine?["objectives"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Where(o => o["state"]?.ToString() == "completed").Select(o => o["text"]?.ToString()));
            return new
            {
                name = q?["name"]?.ToString(), type = q?["type"]?.ToString(), stage = q?["stage"]?.GetValue<int>(),
                stageHere = mine?["stage"]?.GetValue<int>(), formId = q?["formId"]?.ToString(), here,
                objectivesDoneThereNotHere = objectivesThere.Where(t => !objectivesHere.Contains(t)).ToList(),
            };
        }
        var completedThere = (saved["completed"] as JsonArray ?? new JsonArray())
            .Where(q => !doneHere.Contains(Key(q)))
            .Select(q => Row(q, runningHere.Contains(Key(q)) ? "running" : "not started")).ToList();
        // BUG-084: a quest running in both games but further on there (Thane of Falkreath at 200 there, 0 here) is a gap too.
        // Exports from build 117/118 listed every not-completed quest as running: keep only ones actually started there.
        static bool Started(JsonNode? q) => q?["inJournal"]?.GetValue<bool>() == true || (q?["stage"]?.GetValue<int>() ?? 0) > 0 ||
                                            (q?["objectives"] as JsonArray)?.Count > 0;
        // A player-facing quest has a journal entry or objectives; the rest are world encounters, scenes and mod managers.
        static bool PlayerFacing(JsonNode? q) => q?["inJournal"]?.GetValue<bool>() == true || (q?["objectives"] as JsonArray)?.Count > 0;
        var background = (saved["running"] as JsonArray ?? new JsonArray())
            .Where(q => Started(q) && !PlayerFacing(q) && !doneHere.Contains(Key(q)) && !runningHere.Contains(Key(q)))
            .Select(q => q?["name"]?.ToString()).ToList();
        var runningThere = (saved["running"] as JsonArray ?? new JsonArray())
            .Where(Started)
            .Where(PlayerFacing)
            .Where(q => (q?["stage"]?.GetValue<int>() ?? 0) < 255)  // finished encounters that never set the completed flag
            .Where(q => !doneHere.Contains(Key(q)))
            .Where(q => !runningHere.Contains(Key(q)) ||
                        (q?["stage"]?.GetValue<int>() ?? 0) > ((nowByKey.TryGetValue(Key(q), out var m) ? m?["stage"]?.GetValue<int>() : 0) ?? 0))
            .Select(q => Row(q, runningHere.Contains(Key(q)) ? "running, earlier stage" : "not started")).ToList();
        return new
        {
            completedThereNotHere = completedThere,
            runningThereNotHere = runningThere,
            backgroundThereNotHere = background.Count > 0 ? background : null,
            backgroundNote = background.Count > 0 ? "Started there with no journal entry or objectives: world encounters, scenes and mod managers, not player progress." : null,
            note = "Compared only; nothing is applied. Forcing a quest stage (SetQuestStage, CompleteQuest) skips its rewards, world " +
                   "changes and the scripts that start follow-on quests, and main or faction quests can break. Decide per quest " +
                   "with the player, simple miscellaneous ones first, after a save.",
        };
    }

    [McpServerTool]
    [Description("Get factions an actor belongs to, with rank for each. " +
        "Defaults to the player if no refId is given. Pass a refId to query any NPC's factions.")]
    public async Task<object> GetFactions(string? refId = null)
    {
        var parms = new JsonObject();
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        var data = await _pipe.SendRequestAsync("get_factions", parms);
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get all powers, lesser powers, and abilities — separated by type. " +
        "Powers are daily-use abilities (e.g., racial powers). Lesser powers can be used freely. " +
        "Abilities are passive permanent effects. Includes school and primary effect name.")]
    public async Task<object> GetPowers()
    {
        var data = await _pipe.SendRequestAsync("get_powers");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Check for diseases, vampirism, and lycanthropy. Returns vampire/werewolf status, " +
        "active diseases with names and effects, and the player's current race.")]
    public async Task<object> GetDiseaseStatus()
    {
        var data = await _pipe.SendRequestAsync("get_disease_status");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get detailed info about a specific spell by FormID — school, casting type (fire-and-forget, concentration, constant), " +
        "delivery (self, aimed, target), magicka cost, casting perk, and full effect list with magnitudes/durations. " +
        "Use SearchForms to find spell FormIDs first.")]
    public async Task<object> GetSpellDetails(string formId)
    {
        var data = await _pipe.SendRequestAsync("get_spell_details", new JsonObject { ["formId"] = formId });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get enchantment details for an item in the player's inventory by FormID. " +
        "Returns enchantment name, charge level, and full effect list with magnitudes. " +
        "Also returns the item's display name if it was custom-renamed.")]
    public async Task<object> GetEnchantmentInfo(string formId)
    {
        var data = await _pipe.SendRequestAsync("get_enchantment_info", new JsonObject { ["formId"] = formId });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get every enchantment the player has learned (by disenchanting), as listed at the enchanting table. " +
        "Grouped into weapon and armor enchantments, one entry per base enchantment, with effects, the plugin it comes from, " +
        "and any armor-type restriction (restrictedTo keywords). Hidden script-marker effects that some mods add are left out " +
        "(hiddenEffectsOmitted counts them). The game does not record which item taught an enchantment. " +
        "Use this instead of calling Papyrus functions to read learned enchantments.")]
    public async Task<object> GetKnownEnchantments()
    {
        var data = await _pipe.SendRequestAsync("get_known_enchantments");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get the player's magic resistance values — magic, fire, frost, shock, poison, and disease resistance percentages.")]
    public async Task<object> GetMagicResistances()
    {
        var data = await _pipe.SendRequestAsync("get_magic_resistances");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Take a full game state snapshot — exports character blueprint to a file AND saves the game. " +
        "Pass an outputDir (e.g., 'C:\\Users\\cory\\Downloads') and the snapshot is timestamped automatically. " +
        "Creates both a .json blueprint file and an in-game save with matching names.")]
    public async Task<object> TakeSnapshot(string outputDir)
    {
        try
        {
            // Get player info for naming
            var playerData = await _pipe.SendRequestAsync("get_player_info");
            var name = playerData?["name"]?.GetValue<string>() ?? "Player";
            var level = playerData?["level"]?.GetValue<int>() ?? 0;
            var cell = playerData?["cellName"]?.GetValue<string>() ?? "Unknown";
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            var cleanName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            var cleanCell = string.Join("_", cell.Split(Path.GetInvalidFileNameChars()));
            var baseName = $"Snapshot_{cleanName}_Lv{level}_{cleanCell}_{timestamp}";

            // Export blueprint to file
            var blueprintPath = Path.Combine(outputDir, baseName + ".json");
            var blueprintData = await _pipe.SendRequestAsync("get_character_blueprint");
            if (blueprintData != null)
            {
                var jsonString = blueprintData.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(blueprintPath, jsonString);
            }

            // Save the game (sheathes first; reports a save a mod refused)
            if (await SaveTools.RequestSave(_pipe, baseName) is string refused) return new { error = "Blueprint written, but the game didn't save: " + refused, blueprintPath };

            await NotifyInGame("Snapshot saved");

            return new
            {
                success = true,
                snapshotName = baseName,
                blueprintPath,
                saveName = baseName,
                message = $"Snapshot saved: {baseName}"
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Snapshot failed: {ex.Message}" };
        }
    }

    [McpServerTool]
    [Description("Set the player's level. CAUTION: Does not add perks or skill points — just changes the level number.")]
    public async Task<object> SetLevel(int level)
    {
        var data = await _pipe.SendRequestAsync("set_level", new JsonObject { ["level"] = level });
        await NotifyInGame($"Level set to {level}");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Toggle god mode (invincibility) for the player. Uses direct engine API — no console command.")]
    public async Task<object> ToggleGodMode()
    {
        var data = await _pipe.SendRequestAsync("toggle_god_mode");
        var godMode = data?["godMode"]?.GetValue<bool>() ?? false;
        var msg = godMode ? "God Mode On" : "God Mode Off";
        await NotifyInGame(msg);
        return new { success = true, godMode, message = msg };
    }

    [McpServerTool]
    [Description("Toggle immortal mode (TIM) — player takes damage but cannot die. " +
        "Different from god mode: god mode prevents all damage, immortal mode allows damage but prevents death.")]
    public async Task<object> ToggleImmortalMode()
    {
        var data = await _pipe.SendRequestAsync("toggle_immortal_mode");
        var immortal = data?["immortalMode"]?.GetValue<bool>() ?? false;
        var msg = immortal ? "Immortal Mode On" : "Immortal Mode Off";
        await NotifyInGame(msg);
        return new { success = true, immortalMode = immortal, message = msg };
    }
}
