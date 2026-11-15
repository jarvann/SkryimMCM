using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// MCP Tools for utility operations — console commands, notifications, search, load order, save, events, shouts
/// </summary>
[McpServerToolType]
public class UtilityTools : ToolBase
{
    public UtilityTools(IPipeClient pipe) : base(pipe) { }

    [McpServerTool]
    [Description("Check if the game is in a safe state for executing commands. " +
        "Returns safety flags: isLoading, isPaused, isInCombat, isInKillMove, isInDialogue, isSaving. " +
        "If safe=false, the warning field explains why commands should be avoided. " +
        "Call this before risky operations like spawning NPCs, teleporting, or quest manipulation.")]
    public async Task<object> GetGameSafety()
    {
        var data = await _pipe.SendRequestAsync("get_game_safety");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Check which menus are currently open and whether the game is paused. " +
        "Shows open menus like InventoryMenu, MapMenu, DialogueMenu, Console, etc.")]
    public async Task<object> GetMenuState()
    {
        var data = await _pipe.SendRequestAsync("get_menu_state");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Reload the SKSE plugin's logic DLL (Data\\SKSE\\Plugins\\SkyLinkAI\\SkyLinkLogic.dll) from disk without restarting Skyrim. " +
        "The plugin also reloads automatically on the next call after a newer DLL is deployed, so this is mainly for forcing a reload " +
        "or confirming one. Use after build_plugin.ps1 -Deploy. Returns reloaded, previousBuilt, built, reloads and elapsedMs; " +
        "fails (and keeps the old version) if a game-thread task or Papyrus call is still pending.")]
    public async Task<object> ReloadPlugin()
    {
        var data = await _pipe.SendRequestAsync("reload_plugin");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("FALLBACK ONLY — Execute a raw Skyrim console command when NO dedicated tool exists. " +
        "ALWAYS check dedicated tools first: " +
        "Player: GetPlayerInfo, GetSkillLevels, GetPerks, GetKnownSpells, GetKnownShouts, GetKnownEnchantments, GetAppearance, GetFavorites, GetFactions. " +
        "Items: GetInventory, GetEquippedItems, AddItem, RemoveItem, BulkAddItems. " +
        "Quests: GetQuestInfo, GetQuestStages, GetQuestAliases, GetQuestItems, SetQuestStage. " +
        "NPCs: GetNearbyNPCs, GetNPCDetailedInfo, GetNPCInventory, GetFollowers, GetDetectionLevel, GetCrosshairRef, KillActor. " +
        "World: Teleport, GetWeather, SetWeather, GetCellInfo, GetNearbyObjects, UnlockDoor, LockDoor, GetContainerInventory. " +
        "Actions: ToggleGodMode, ToggleCollision, SetHealth, SetMagicka, SetStamina, SaveGame, SearchForms, ShowNotification. " +
        "Only use this tool if none of the above cover your need. " +
        "TARGETING: Use dot-notation with refId (e.g., 'FE124B0E.kill'), NOT prid — prid does not persist between calls. " +
        "Player commands: use 'player.' prefix (e.g., 'player.additem 0000000F 100'). " +
        "CAUTION: Invalid FormIDs crash the game. Avoid during loading screens or scripted events. " +
        "Console output may be captured if the command produces text output.")]
    public async Task<object> ExecuteConsoleCommand(string command)
    {
        var data = await _pipe.SendRequestAsync("execute_command", new JsonObject
        {
            ["command"] = command
        });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Show a notification message on the player's HUD (the same style as quest updates and skill increases). " +
        "Use this to inform the player about actions you're taking or important information. " +
        "Keep messages short and natural — they appear briefly in the top-left corner of the screen.")]
    public async Task<object> ShowNotification(string message)
    {
        await _pipe.SendRequestAsync("show_notification", new JsonObject
        {
            ["message"] = message
        });
        return new { success = true, message };
    }

    [McpServerTool]
    [Description("Search for forms (items, NPCs, quests, spells, etc.) by name or editor ID. " +
        "This is the equivalent of the 'help' console command but safe and returns structured data. " +
        "Use this to find FormIDs for items, NPCs, quests, or any game record from any mod in the load order. " +
        "Optionally filter by type: weapon, armor, potion, book, misc, spell, perk, quest, npc, enchantment, shout, key, ammo, ingredient, soulgem, scroll, location. " +
        "Returns up to maxResults matches (default 25).")]
    public async Task<object> SearchForms(string query, string type = "all", int maxResults = 25)
    {
        var data = await _pipe.SendRequestAsync("search_forms", new JsonObject
        {
            ["query"] = query,
            ["type"] = type,
            ["maxResults"] = maxResults
        });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get the full load order of all active plugins (ESP, ESM, ESL) with their FormID prefixes. " +
        "Use this to determine the correct FormID prefix for items from a specific mod.")]
    public async Task<object> GetLoadOrder()
    {
        var data = await _pipe.SendRequestAsync("get_load_order");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Look up the FormID prefix for a specific mod/plugin by filename (e.g., 'HLIORemi.esp'). " +
        "Returns the hex prefix needed to construct valid FormIDs for that mod's records. " +
        "For regular plugins, the prefix is 2 hex digits (e.g., '3A'). " +
        "For light plugins (ESL/ESPFE), the prefix is 'FE' followed by 3 hex digits (e.g., 'FE01A').")]
    public async Task<object> GetModFormIdPrefix(string modName)
    {
        var data = await _pipe.SendRequestAsync("get_mod_formid_prefix", new JsonObject
        {
            ["modName"] = modName
        });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("List all installed SKSE plugins with integration potential. " +
        "Identifies plugins like ConsoleUtilSSE, HDT-SMP, PapyrusUtil, RaceMenu, TrueHUD, etc. " +
        "and notes which ones have APIs that could extend MCP capabilities.")]
    public async Task<object> GetLoadedSKSEPlugins()
    {
        var data = await _pipe.SendRequestAsync("get_loaded_skse_plugins");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Save the game. Automatically generates a descriptive save name from player name, level, and location. " +
        "Creates both .ess and .skse co-save files. You can provide a custom save name or leave it blank for auto-naming. " +
        "CAUTION: Always save before performing risky operations like quest stage manipulation or teleporting.")]
    public async Task<object> SaveGame(string? saveName = null)
    {
        // If no name provided, build one from player info
        if (string.IsNullOrWhiteSpace(saveName))
        {
            try
            {
                var playerData = await _pipe.SendRequestAsync("get_player_info");
                var name = playerData?["name"]?.GetValue<string>() ?? "Player";
                var level = playerData?["level"]?.GetValue<int>() ?? 0;
                var cell = playerData?["cellName"]?.GetValue<string>() ?? "Unknown";
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

                // Clean invalid filename characters
                name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                cell = string.Join("_", cell.Split(Path.GetInvalidFileNameChars()));

                saveName = $"MCP_{name}_Lv{level}_{cell}_{timestamp}";
            }
            catch
            {
                saveName = $"MCP_Save_{DateTime.Now:yyyyMMdd-HHmmss}";
            }
        }

        await _pipe.SendRequestAsync("save_game", new JsonObject
        {
            ["saveName"] = saveName
        });

        // Notification is handled by the plugin after save completes
        return new { success = true, message = $"Game saved as: {saveName}" };
    }

    [McpServerTool]
    [Description("Poll for recent game events. Returns all events since last poll. " +
        "Events: combat, death, quest_stage, quest_start_stop, location_change, cell_loaded, " +
        "inventory_change, equip, hit, spell_cast, activate, open_close, sleep_ended, wait_ended, " +
        "fast_travel, lock_changed, stat_change. " +
        "Framework quest noise is auto-muted (not dropped). Response includes 'muted' summary showing " +
        "suppressed event counts — check mutedTotal to know if framework quests are cycling. " +
        "For focused polling, pass eventTypes (comma-separated types to include) " +
        "or excludeTypes (comma-separated types to exclude).")]
    public async Task<object> PollEvents(string? eventTypes = null, string? excludeTypes = null)
    {
        var parms = new JsonObject();
        if (!string.IsNullOrEmpty(eventTypes))
        {
            var types = new JsonArray();
            foreach (var t in eventTypes.Split(',', StringSplitOptions.TrimEntries))
                types.Add(t);
            parms["eventTypes"] = types;
        }
        if (!string.IsNullOrEmpty(excludeTypes))
        {
            var types = new JsonArray();
            foreach (var t in excludeTypes.Split(',', StringSplitOptions.TrimEntries))
                types.Add(t);
            parms["excludeTypes"] = types;
        }
        var data = await _pipe.SendRequestAsync("poll_events", parms);
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Unlock a shout or word of power by FormID. If given a shout FormID, unlocks all three words. " +
        "If given an individual word FormID, unlocks just that word. Uses teachword + unlockword console commands.")]
    public async Task<object> UnlockShout(string formId)
    {
        var data = await _pipe.SendRequestAsync("unlock_shout", new JsonObject { ["formId"] = formId });
        await NotifyInGame("Shout unlocked");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Play a music track by FormID via native API. Use SearchForms with type='music' to find tracks. " +
        "Example: search for 'combat' or 'explore' to find music type FormIDs.")]
    public async Task<object> PlayMusic(string formId)
    {
        var data = await _pipe.SendRequestAsync("play_music", new JsonObject
        {
            ["formId"] = formId
        });
        await NotifyInGame("Music playing");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Stop the currently playing music via native API. Returns what was playing.")]
    public async Task<object> StopMusic()
    {
        var data = await _pipe.SendRequestAsync("stop_music");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Set the master volume. Value from 0.0 (mute) to 1.0 (full). " +
        "Affects all game audio including music, effects, and voice.")]
    public async Task<object> SetVolume(float volume)
    {
        // Use console command to set master volume via game setting
        var data = await _pipe.SendRequestAsync("execute_command", new JsonObject
        {
            ["command"] = $"setgs fAudioMasterVolume {volume:F2}"
        });
        await NotifyInGame($"Volume: {volume:P0}");
        return new { success = true, volume, message = $"Volume set to {volume:P0}" };
    }

    [McpServerTool]
    [Description("Take a screenshot. Saves to the Skyrim root directory as a .bmp file.")]
    public async Task<object> TakeScreenshot()
    {
        var data = await _pipe.SendRequestAsync("execute_command", new JsonObject
        {
            ["command"] = "screenshot"
        });
        await NotifyInGame("Screenshot taken");
        return new { success = true, message = "Screenshot saved to Skyrim directory" };
    }

    [McpServerTool]
    [Description("Load a save game by name. Immediately begins loading — current game state is lost. " +
        "CAUTION: Any unsaved progress will be lost. Use SaveGame first if needed.")]
    public async Task<object> LoadSave(string saveName)
    {
        var data = await _pipe.SendRequestAsync("load_save", new JsonObject { ["saveName"] = saveName });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Load the most recent save game. Useful for quick recovery after a CTD or mistake.")]
    public async Task<object> LoadMostRecentSave()
    {
        var data = await _pipe.SendRequestAsync("load_most_recent_save");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Read a plugin config file (JSON or INI) from Data/SKSE/Plugins/. " +
        "Pass the filename relative to the plugins directory (e.g., 'SmoothCam.json', 'po3_Tweaks.ini'). " +
        "Returns the file contents parsed as JSON. Use this to inspect mod settings. " +
        "For SmoothCam: 'SmoothCam.json' (active config), 'SmoothCamPreset0.json' through 'SmoothCamPreset5.json' (presets).")]
    public async Task<object> ReadPluginConfig(string filename)
    {
        try
        {
            // Get Skyrim path from pipe — ask plugin for current working dir
            // For now, use a known path pattern
            var skyrimPath = Environment.GetEnvironmentVariable("SKYRIM_PATH")
                ?? @"C:\Steam\steamapps\common\Skyrim Special Edition";
            var filePath = Path.Combine(skyrimPath, "Data", "SKSE", "Plugins", filename);

            if (!File.Exists(filePath))
                return new { error = $"File not found: {filename}" };

            var content = await File.ReadAllTextAsync(filePath);

            // Try parsing as JSON
            if (filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var json = JsonNode.Parse(content);
                    return new { filename, format = "json", data = JsonSerializer.Deserialize<JsonElement>(content) };
                }
                catch { }
            }

            // Return as raw text for INI and other formats
            return new { filename, format = "text", content };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to read config: {ex.Message}" };
        }
    }

    [McpServerTool]
    [Description("Write a plugin config file (JSON) to Data/SKSE/Plugins/. " +
        "Pass the filename and the JSON content to write. " +
        "CAUTION: This overwrites the file. Back up first if needed. " +
        "Changes take effect on next MCM reload or game restart depending on the plugin. " +
        "For SmoothCam: write to 'SmoothCam.json' to change active config, or preset files to modify presets.")]
    public async Task<object> WritePluginConfig(string filename, string content)
    {
        try
        {
            // Security: only allow writing to SKSE/Plugins directory, no path traversal
            if (filename.Contains("..") || filename.Contains("/") || filename.Contains("\\"))
                return new { error = "Invalid filename — no path separators or .. allowed" };

            if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                !filename.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
                !filename.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                return new { error = "Only .json, .ini, and .txt files can be written" };

            var skyrimPath = Environment.GetEnvironmentVariable("SKYRIM_PATH")
                ?? @"C:\Steam\steamapps\common\Skyrim Special Edition";
            var filePath = Path.Combine(skyrimPath, "Data", "SKSE", "Plugins", filename);

            await File.WriteAllTextAsync(filePath, content);
            await NotifyInGame($"Config saved: {filename}");

            return new { success = true, filename, message = $"Written to {filePath}" };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to write config: {ex.Message}" };
        }
    }

    [McpServerTool]
    [Description("List all config files for a plugin in Data/SKSE/Plugins/. " +
        "Pass a search pattern like 'SmoothCam' to find all SmoothCam files, or '*' for everything. " +
        "Returns filenames, sizes, and last modified dates.")]
    public async Task<object> ListPluginConfigs(string pattern = "*")
    {
        try
        {
            var skyrimPath = Environment.GetEnvironmentVariable("SKYRIM_PATH")
                ?? @"C:\Steam\steamapps\common\Skyrim Special Edition";
            var pluginsDir = Path.Combine(skyrimPath, "Data", "SKSE", "Plugins");

            if (!Directory.Exists(pluginsDir))
                return new { error = "Plugins directory not found" };

            var files = Directory.GetFiles(pluginsDir, $"*{pattern}*")
                .Where(f => f.EndsWith(".json") || f.EndsWith(".ini") || f.EndsWith(".txt") || f.EndsWith(".toml"))
                .Select(f => new FileInfo(f))
                .Select(fi => new
                {
                    filename = fi.Name,
                    sizeBytes = fi.Length,
                    lastModified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                })
                .OrderBy(f => f.filename)
                .ToArray();

            return new { files, count = files.Length, pattern };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to list configs: {ex.Message}" };
        }
    }
}
