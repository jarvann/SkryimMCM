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
        "SkyLinkLauncher, launcherVersion is set, serverPath is the shadow copy and serverSource is the deployed DLL.")]
    public async Task<object> CheckConnection()
    {
        try
        {
            // Plugin 1.1.0+ reports its version and the game runtime; 1.2.0+ also its link time.
            var info = await _pipe.SendRequestAsync("ping");
            return new
            {
                connected = true,
                message = "Connected to Skyrim via SKSE plugin",
                pluginVersion = info?["pluginVersion"]?.GetValue<string>(),
                pluginBuilt = info?["pluginBuilt"]?.GetValue<string>(),
                gameVersion = info?["gameVersion"]?.GetValue<string>(),
                loadingScreen = info?["loadingScreen"]?.GetValue<bool>(),  // 1.2.1+
                logic = info?["logic"] is JsonNode logicNode ? DeserializeResponse(logicNode) : null,
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
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get the player's appearance data: race, sex, weight, height, all 19 face morph values, " +
        "face presets, and head parts (hair, eyes, etc.). Use this for character recreation or face morphing. " +
        "Each head part has typeName, menuIndex (the number RaceMenu's slider shows for it, 0-based, editor IDs in case-sensitive order) " +
        "and menuChoices (how many playable choices of that type for this race and sex; RaceMenu may show one or two more). " +
        "ListHeadPartChoices lists the parts around a slider number. " +
        "Hair colour and skin tone: GetHairAndSkinColor.")]
    public async Task<object> GetAppearance()
    {
        var data = await _pipe.SendRequestAsync("get_appearance");
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
        "Every form ID also carries its plugin name and local ID (formIdSource), so ApplyBlueprint still finds modded items after a load-order change.")]
    public async Task<object> GetCharacterBlueprint(string? outputPath = null)
    {
        var data = await _pipe.SendRequestAsync("get_character_blueprint");

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
        "sections (comma-separated, default 'inventory,equipment,gold'). Only missing items are added (blueprint count minus what " +
        "the player has); nothing is ever removed, and gold only goes up unless exactGold=true. Then the blueprint's equipped " +
        "armor, weapons and spells are equipped. Items are found by plugin name and local ID, so a different load order works; " +
        "items from plugins that aren't loaded, items created in game, and blueprints from before 1.3 whose form IDs now point at " +
        "a different item are listed as unresolved. Player-made enchantments are not restored (the plain item is added).")]
    public async Task<object> ApplyBlueprint(string path, bool dryRun = true, string sections = "inventory,equipment,gold", bool exactGold = false)
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

        var parms = new JsonObject
        {
            ["sections"] = new JsonArray(sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => (JsonNode)s.ToLowerInvariant()).ToArray()),
            ["dryRun"] = dryRun,
            ["exactGold"] = exactGold,
            ["inventory"] = bp["inventory"]?["items"]?.DeepClone() ?? new JsonArray(),
            ["equipped"] = bp["equipped"]?["equipped"]?.DeepClone() ?? new JsonArray(),
        };
        if (bp["gold"]?["gold"] is JsonNode gold) parms["gold"] = gold.DeepClone();

        var data = await _pipe.SendRequestAsync("apply_blueprint_items", parms);
        if (!dryRun && data?["error"] == null) await NotifyInGame("Blueprint applied");
        var result = DeserializeResponse(data);
        return new
        {
            blueprint = path,
            exportedBy = bp["exportSource"]?.GetValue<string>(),
            portableIds = bp["formatVersion"]?.GetValue<int>() >= 2,
            result,
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

            // Save the game
            await _pipe.SendRequestAsync("save_game", new JsonObject { ["saveName"] = baseName });

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
