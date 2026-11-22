using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Drawing;
using ModelContextProtocol.Protocol;
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
        "Call this before risky operations like spawning NPCs, teleporting, or quest manipulation. " +
        "canMovePlayer and moveBlockers say whether Teleport and studio trips would be refused and why (menus, combat, dialogue, " +
        "scenes, script-held controls such as cutscenes or OStim, furniture, mount, ragdoll, falling, bleeding out, a held object); " +
        "notes flag swimming and beast form. Check it before moving the player. action (move, camera, face or actor, with refId " +
        "for actor) answers for that kind of action, as its tools decide (allowed, reason), including an OStim scene; reads need " +
        "no check. The tools apply these rules themselves and refuse with the reason.")]
    public async Task<object> GetGameSafety(string? action = null, string? refId = null)
    {
        var data = await _pipe.SendRequestAsync("get_game_safety");
        if (data is not JsonObject o) return DeserializeResponse(data);
        // BUG-076: the scene showed only when an action was named, and its posing read as "in the air".
        var scene = await SafetyBlock("actor", refId);
        o["isInOStimScene"] = scene != null;
        if (scene != null)
        {
            var blockers = (o["moveBlockers"] as JsonArray ?? new JsonArray()).Select(b => b?.ToString() ?? "")
                .Where(b => !b.Contains("in the air")).ToList();
            blockers.Insert(0, "she is in an OStim scene: wait until it ends");
            o["moveBlockers"] = new JsonArray(blockers.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray());
            o["canMovePlayer"] = false;
        }
        // FEAT-261: MCM Recorder playing or recording blocks moves too; the top-level answer said canMovePlayer true meanwhile.
        if (await McmRecorderPlaying() is string mcm)
        {
            var blockers = (o["moveBlockers"] as JsonArray ?? new JsonArray()).Select(b => b?.ToString() ?? "").ToList();
            blockers.Insert(0, mcm);
            o["moveBlockers"] = new JsonArray(blockers.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray());
            o["canMovePlayer"] = false;
            o["mcmRecorder"] = mcm;
        }
        o["safeMeans"] = "safe: reads and simple changes can run. Moving her, captures and face edits have stricter rules: see " +
                         "canMovePlayer, or pass action.";
        if (string.IsNullOrWhiteSpace(action)) return DeserializeResponse(o);
        // FEAT-260: answer for one kind of action, the way that action's tools decide.
        var kind = action.Trim().ToLowerInvariant() switch
        {
            "teleport" or "move" or "studio" => "move",
            "capture" or "camera" or "measure" => "camera",
            "face" or "body" or "edit" => "face",
            "actor" or "moveactor" or "follower" => "actor",
            _ => null,
        };
        if (kind == null) return new { error = "action must be move, camera, face or actor (or leave it out for the raw states)" };
        var blocked = await SafetyBlock(kind, refId);
        o["action"] = kind;
        o["allowed"] = blocked == null;
        o["reason"] = blocked;
        return DeserializeResponse(o);
    }

    [McpServerTool]
    [Description("Check which menus are open and whether the game is paused. " +
        "openMenus and menuCount cover only these vanilla menus: Console, InventoryMenu, MagicMenu, MapMenu, StatsMenu, " +
        "ContainerMenu, DialogueMenu, BarterMenu, FavoritesMenu, Journal Menu, Book Menu, LockpickingMenu, Loading Menu, " +
        "MessageBoxMenu, TweenMenu, Sleep/Wait Menu, Crafting Menu, Training Menu, RaceSex Menu. " +
        "So menuCount 0 means none of those is open, not that the screen is clear. " +
        "allOpenMenus lists every open menu, including the HUD and mod menus, with pausesGame, usesCursor, modal and " +
        "alwaysOpen (true for overlays such as the HUD that are always present).")]
    public async Task<object> GetMenuState()
    {
        var data = await _pipe.SendRequestAsync("get_menu_state");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("The message box on screen (FEAT-302): its text, its buttons and a boxId. Tell the player what it says and what the buttons are; " +
        "answer it with AnswerMessageBox only as they say. Boxes the player asked to have closed are closed here and listed in 'dismissed'.")]
    public async Task<object> ReadMessageBox()
    {
        var dismissed = await DismissKnownMessageBoxes();
        var box = DeserializeResponse(await _pipe.SendRequestAsync("get_message_box"));
        return new { box, dismissed = dismissed.Count == 0 ? null : dismissed };
    }

    [McpServerTool]
    [Description("Press a button of the message box on screen (FEAT-302). Only with the player's go-ahead for this box and this button: the buttons " +
        "can start quests, spend things or lock choices in. boxId: from ReadMessageBox (refused if another box is showing by then). button: " +
        "its number, from 0. always: true remembers a box with a single button (OK) so SkyLink closes it by itself from now on, when the player asks for that.")]
    public async Task<object> AnswerMessageBox(string boxId, int button, bool always = false)
    {
        var before = await _pipe.SendRequestAsync("get_message_box");
        var text = before?["text"]?.ToString();
        var single = (before?["buttons"] as JsonArray)?.Count == 1;
        var answer = await _pipe.SendRequestAsync("answer_message_box", new JsonObject { ["boxId"] = boxId, ["button"] = button });
        if (answer?["error"] != null) return DeserializeResponse(answer);
        string? remembered = null;
        if (always)
        {
            if (!single || text is not { Length: >= 8 } || before?["boxId"]?.ToString() != boxId) remembered = "Not remembered: only boxes with a single button and some text are closed by themselves.";
            else
            {
                var piece = text.Length > 120 ? text[..120] : text;
                var settings = SkyLinkSettings.Load();
                var list = settings["dismissMessageBoxes"] as JsonArray ?? [];
                if (!list.Any(x => x?.ToString() == piece)) list.Add(piece);
                settings["dismissMessageBoxes"] = list.DeepClone();
                SkyLinkSettings.Save(settings);
                remembered = "From now on SkyLink closes this box by itself when it gets in the way. Remove it from dismissMessageBoxes with SetSkyLinkSetting to stop.";
            }
        }
        return new { answer = DeserializeResponse(answer), remembered };
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
        "A refId that starts with a digit must be quoted ('\"61033226\".moveto player'), or the console reads it as a command. " +
        "Player commands: use 'player.' prefix (e.g., 'player.additem 0000000F 100'). " +
        "Commands run with the player selected, as if the player were clicked in the console, so 'tcl' here only " +
        "sets the player's reference flag and is NOT no-clip: use ToggleCollision for that. " +
        "CAUTION: Invalid FormIDs crash the game. Avoid during loading screens or scripted events. " +
        "What the command prints comes back in 'output', or for multi-line output (sqv, help) as 'lines' (maxLines, default 300).")]
    public async Task<object> ExecuteConsoleCommand(string command, int maxLines = 300)
    {
        // BUG-024: a console move (coc, cow, centeroncell, player.moveto) loads a cell inside the call and timed out though it
        // worked. Start it, then wait for the cell to change, as Teleport does.
        var c = command.Trim().ToLowerInvariant();
        var isMove = c.StartsWith("coc ") || c.StartsWith("cow ") || c.StartsWith("centeroncell ") || c.StartsWith("centeronworld ") ||
                     c.StartsWith("player.moveto ") || c.StartsWith("player.moveto(");
        if (isMove)
        {
            if (await SafetyBlock("move", null) is string gateReason) return new { error = gateReason };
            JsonNode? before = null;
            try { before = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "status" }); } catch (Exception) { }
            var fromCell = before?["cellFormId"]?.ToString();
            var started = await _pipe.SendRequestAsync("execute_command_async", new JsonObject { ["command"] = command });
            if (started?["error"] != null) return DeserializeResponse(started);
            for (var waited = 0; waited < 20000; waited += 750)
            {
                await Task.Delay(750);
                try
                {
                    var now = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "status" });
                    var cell = now?["cellFormId"]?.ToString();
                    if (cell != null && cell != fromCell)
                        return new { command, moved = true, cellName = now?["cellName"]?.ToString(), cellFormId = cell,
                                     note = "Teleport has a way back (Teleport('back')); console moves don't." };
                }
                catch (Exception) { /* loading */ }
            }
            return new { command, moved = (bool?)null, note = "Started, but the cell hadn't changed after 20 seconds (still loading, or the same cell). Check with GetCellInfo." };
        }
        var data = await _pipe.SendRequestAsync("execute_command", new JsonObject
        {
            ["command"] = command
        });
        // Round A: long output came back twice (output and lines), 72-85 KB a reply. Lines only, capped.
        if (data is JsonObject o && o["lines"] is JsonArray lines)
        {
            o.Remove("output");
            var cap = Math.Clamp(maxLines, 10, 5000);
            if (lines.Count > cap)
            {
                var total = lines.Count;
                var kept = new JsonArray(lines.Take(cap).Select(l => l?.DeepClone()).ToArray());
                o["lines"] = kept;
                o["truncated"] = $"{total - cap} more lines (raise maxLines, up to 5000)";
            }
            o["lineCount"] = lines.Count;
        }
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
        "Optionally filter by type: weapon, armor, potion, book, misc, spell, perk, quest, npc, race, enchantment, shout, key, ammo, " +
        "ingredient, soulgem, scroll, location, cell (interiors by name), worldspace, faction, idle, container, activator, furniture, " +
        "door, flora, music, keyword, magiceffect, headpart, outfit, class, recipe, light, global and more; an unknown type is refused " +
        "with the list. Editor IDs are often empty (the game drops them). " +
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
    [Description("Get the load order of active plugins (ESP, ESM, ESL) with their FormID prefixes, a page at a time. " +
        "filter: only plugins whose file name contains this text (case-insensitive). type: 'esp' (regular, 2-digit prefix) or 'esl' (light, FExxx). " +
        "page (default 1) and pageSize (default 100; 0 = summary only). Large lists have thousands of plugins, so filter when you can; " +
        "for a single plugin, GetModFormIdPrefix is cheaper. outputPath writes the whole (filtered) list to a JSON file instead and returns only the counts.")]
    public async Task<object> GetLoadOrder(string? filter = null, string? type = null, int page = 1, int pageSize = 100,
        string? outputPath = null)
    {
        var data = await _pipe.SendRequestAsync("get_load_order");
        var all = data?["mods"]?.AsArray();
        if (all == null) return DeserializeResponse(data);

        var matches = all.Where(m =>
        {
            var name = m?["name"]?.GetValue<string>() ?? "";
            var modType = m?["type"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(type) && !modType.Equals(type, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }).Select(m => m!.DeepClone()).ToList();

        var regularCount = data?["regularCount"]?.GetValue<int>() ?? 0;
        var lightCount = data?["lightCount"]?.GetValue<int>() ?? 0;

        if (!string.IsNullOrEmpty(outputPath))
        {
            try
            {
                var full = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, new JsonArray(matches.ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return new { written = full, matched = matches.Count, regularCount, lightCount };
            }
            catch (Exception ex)
            {
                return new { error = $"Could not write {outputPath}: {ex.Message}" };
            }
        }

        int totalPages = pageSize > 0 ? (int)Math.Ceiling(matches.Count / (double)pageSize) : 0;
        if (page < 1) page = 1;
        var items = pageSize > 0
            ? matches.Skip((page - 1) * pageSize).Take(pageSize).Select(m => JsonSerializer.Deserialize<JsonElement>(m.ToJsonString())).ToArray()
            : Array.Empty<JsonElement>();

        return new
        {
            mods = items,
            page,
            pageSize,
            matched = matches.Count,
            totalPages,
            hasMore = page < totalPages,
            regularCount,
            lightCount,
        };
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
    [Description("What SkyLink has cost the game this session (INFRA-007), for 'does this mod hurt my FPS?': game-thread time " +
        "(the part a player can feel: it comes out of frames) in total and per tool action, the longest single task (one long " +
        "task is a hitch), and how long the AI waited. shareOfGameTime is game-thread time divided by time since the game started " +
        "(0.001 = 0.1%). reapply_face_state is SkyLink's own face refresh after loads and cell changes, not an AI call. Read-only, " +
        "answered without touching the game thread.")]
    public async Task<object> GetSkyLinkCost()
    {
        var data = await _pipe.SendRequestAsync("get_skylink_cost");
        if (data is JsonObject o && o["shareOfGameTime"] is JsonValue v && v.TryGetValue<double>(out var share))
        {
            var longest = o["longestTaskMs"]?.GetValue<double>() ?? 0;
            o["summary"] = $"SkyLink used {o["gameThreadMsTotal"]} ms of game-thread time in {Math.Round(o["sinceGameStartSeconds"]?.GetValue<double>() ?? 0)} s " +
                $"({share * 100:0.###}% of the time). Longest single task: {longest:0.#} ms ({o["longestTaskAction"]})" +
                (longest > 16 ? ", long enough to drop a frame at 60 FPS: that tool causes a brief hitch when used." : ", shorter than a 60 FPS frame.");
        }
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
    [Description("Save the game (with no name: SkyLink_<date-time>), .ess plus the .skse co-save; waits until the file exists. " +
        "This is the console save: the game itself doesn't keep the character ID in such a save, so SkyLink stores it in its " +
        "co-save record and puts it back on load (BUG-091). An existing save is never replaced unless overwrite is true. For a save to keep with no mods involved, save from the game menu. " +
        "CAUTION: Always save before performing risky operations like quest stage manipulation or teleporting.")]
    public async Task<object> SaveGame(string? saveName = null, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(saveName)) saveName = $"SkyLink_{DateTime.Now:yyyyMMdd-HHmmss}";
        saveName = string.Join("_", saveName.Split(Path.GetInvalidFileNameChars()));
        if (!overwrite && File.Exists(Path.Combine(SaveTools.SaveFolder(), saveName + ".ess")))
            return new { success = false, error = $"A save called {saveName} already exists. Use another name, or overwrite: true to replace it (with the player's agreement)." };

        var (notStarted, sheathed) = await SaveTools.RequestSaveEx(_pipe, saveName);
        if (notStarted != null) return new { success = false, error = notStarted };
        return new
        {
            success = true,
            message = $"Game saved as: {saveName} (the game finishes writing it in the background).",
            sheathed = sheathed ? "A weapon or spell was out: it was sheathed first so the game would save (draw it again yourself)." : null,
        };
    }

    [McpServerTool]
    [Description("Poll for recent game events, oldest first. " +
        "Events: combat, death, quest_stage, quest_start_stop, location_change, cell_loaded, " +
        "inventory_change, equip, hit, spell_cast, activate, open_close, sleep_ended, wait_ended, " +
        "fast_travel, lock_changed, stat_change, and quest_churn. " +
        "Returns at most maxEvents (default 50; 0 = all). Events that don't fit are kept for the next poll: " +
        "'remaining' says how many are waiting. " +
        "A quest that starts and stops 3 or more times in one batch is collapsed into one quest_churn event " +
        "(starts, stops, runningNow); a quest whose stage changes 3 or more times becomes one quest_stage_churn " +
        "(changes, stagesSeen, lastStage); and 10 or more lock_changed events (doors as a cell loads) into one lock_changed_burst " +
        "(count and the first 5); set collapseQuestChurn=false to see each one. " +
        "Known framework quests are muted (counted, not listed): see 'muted' and mutedTotal. " +
        "For focused polling, pass eventTypes (comma-separated types to include) " +
        "or excludeTypes (comma-separated types to exclude); these filter events as the game hands them over.")]
    public async Task<object> PollEvents(string? eventTypes = null, string? excludeTypes = null,
        int maxEvents = 50, bool collapseQuestChurn = true)
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

        var (events, remaining, collapsed, dropped) =
            EventShaper.Shared.Take(data?["events"]?.AsArray(), maxEvents, collapseQuestChurn);

        var result = new JsonObject
        {
            ["events"] = events,
            ["count"] = events.Count,
            ["remaining"] = remaining,
            ["mutedTotal"] = data?["mutedTotal"]?.DeepClone() ?? 0,
        };
        if (data?["muted"] is JsonNode muted) result["muted"] = muted.DeepClone();
        if (collapsed.Count > 0) result["collapsedQuests"] = collapsed;
        if (dropped > 0) result["droppedOldest"] = dropped;
        return DeserializeResponse(result);
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
    [Description("Developer test of SkyLink's crash protection (FEAT-220): runs a game-thread task that faults on purpose " +
        "(mode null_read or divide_by_zero) and reports whether the guard caught it and the game kept running. The fault is in " +
        "SkyLink's own code, holding nothing in the engine, so the game is unaffected; still, only run it when the player has " +
        "asked for it and has saved. confirm must be true.")]
    public async Task<object> TestCrashProtection(string mode = "null_read", bool confirm = false)
    {
        if (!confirm) return new { error = "This faults on purpose inside the game. Ask the player, have them save, then pass confirm=true." };
        var data = await _pipe.SendRequestAsync("crash_test", new JsonObject { ["mode"] = mode, ["confirm"] = "crash-test" });
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
    [Description("Set a game volume, like the Audio menu sliders: volume 0.0 (mute) to 1.0 (full). " +
        "category: 'master' (default, the Master slider), an Audio menu category such as Music, Effects, Voice, Footsteps or Magic " +
        "(or its FormID, when two share a name), 'all' (every category, not Master), 'list' (each category's own and effective " +
        "volume and parent), 'snapshot' (save every category's own value and Master; volume ignored) or 'restore' (put the last " +
        "snapshot back, parents first; volume ignored). Take a snapshot before changing several volumes. " +
        "'own' is the category's value; 'effective' is what plays after its parent and static multiplier, so it can read lower. " +
        "Applies at once, except that music already playing takes up to a minute. Ask the player whether they hear the change.")]
    public async Task<object> SetVolume(float volume = 1.0f, string category = "master")
    {
        var key = category.Trim().ToLowerInvariant();
        var snapshotFile = Path.Combine(SkyLinkSettings.Folder, "volume_snapshot.json");
        if (key == "snapshot")
        {
            var list = await _pipe.SendRequestAsync("set_volume", new JsonObject { ["volume"] = 0, ["category"] = "list" });
            if (list?["error"] is not null) return DeserializeResponse(list);
            var snap = new JsonObject
            {
                ["saved"] = DateTime.Now.ToString("s"),
                ["master"] = list?["master"]?.DeepClone(),
                ["categories"] = new JsonArray((list?["categories"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Select(c => (JsonNode?)new JsonObject { ["formId"] = c["formId"]?.DeepClone(), ["name"] = c["name"]?.DeepClone(), ["own"] = c["own"]?.DeepClone() }).ToArray()),
            };
            Directory.CreateDirectory(SkyLinkSettings.Folder);
            await File.WriteAllTextAsync(snapshotFile, snap.ToJsonString());
            return new { saved = snapshotFile, categories = (snap["categories"] as JsonArray)!.Count, master = snap["master"]?.ToString() };
        }
        if (key == "restore")
        {
            if (!File.Exists(snapshotFile)) return new { error = "No volume snapshot saved: SetVolume(category: 'snapshot') first." };
            var snap = JsonNode.Parse(await File.ReadAllTextAsync(snapshotFile)) as JsonObject;
            var restored = new List<string>();
            var failed = new List<string>();
            foreach (var c in (snap?["categories"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())  // saved parents first
            {
                var r = await _pipe.SendRequestAsync("set_volume", new JsonObject { ["volume"] = c["own"]?.GetValue<float>() ?? 1f, ["category"] = c["formId"]?.GetValue<string>() });
                if (r?["error"] != null || r?["warning"] != null) failed.Add($"{c["name"]} ({c["formId"]}): {r?["error"] ?? r?["warning"]}");
                else restored.Add($"{c["name"]} {c["own"]}");
            }
            if (snap?["master"] is JsonValue m)
                await _pipe.SendRequestAsync("set_volume", new JsonObject { ["volume"] = m.GetValue<float>(), ["category"] = "master" });
            await NotifyInGame("Volumes restored");
            return new { restoredFrom = snap?["saved"]?.ToString(), restored = restored.Count, master = snap?["master"]?.ToString(), failed };
        }
        var data = await _pipe.SendRequestAsync("set_volume", new JsonObject { ["volume"] = volume, ["category"] = category });
        if (key != "list") await NotifyInGame($"{data?["category"]?.GetValue<string>() ?? "Volume"}: {volume:P0}");
        return DeserializeResponse(data);
    }

    // Waits until the file stops growing, then renames ScreenShotN.png to ScreenShot_yyyy-MM-dd_HH-mm-ss.png.
    // Leaves it where it is if it can't be renamed.
    private static async Task<FileInfo> KeepScreenshot(FileInfo file)
    {
        long last = -1;
        for (var i = 0; i < 40; i++)
        {
            file.Refresh();
            if (file.Length > 0 && file.Length == last) break;
            last = file.Length;
            await Task.Delay(250);
        }
        var stem = Path.GetFileNameWithoutExtension(file.Name);
        var digits = stem.Length - stem.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Length;
        var prefix = digits > 0 ? stem[..^digits] : stem;
        var target = Path.Combine(file.DirectoryName!, $"{prefix}_{file.LastWriteTime:yyyy-MM-dd_HH-mm-ss}{file.Extension}");
        try
        {
            if (File.Exists(target)) return file;
            file.MoveTo(target);
            return new FileInfo(target);
        }
        catch (IOException) { return file; }
        catch (UnauthorizedAccessException) { return file; }
    }

    [McpServerTool]
    [Description("Take a screenshot with the game's own Print Screen handler, then wait for the file and report its path. " +
        "The game writes it where Skyrim.ini's sScreenShotBaseName points (the game folder by default) and only while it is " +
        "rendering: if the player is tabbed out, ask them to click into the game first. waitSeconds (default 8, max 30). " +
        "The full-size file can be huge (63 MB at 7680x2160), so a small JPEG copy is also saved next to it as " +
        "<name>_preview.jpg: previewWidth (default 1568, 0 for none) caps its width, and crop 'x,y,width,height' in " +
        "fractions of the frame (e.g. '0.4,0.05,0.2,0.6' for the middle) cuts it first, so a face can fill the preview. " +
        "showImage (default true) also returns the preview as an image you can look at. " +
        "Returns an error if no file appeared. Close menus first if the player wants the plain view.")]
    public async Task<CallToolResult> TakeScreenshot(int waitSeconds = 8, int previewWidth = 1568, string? crop = null, bool showImage = true)
    {
        RectangleF? cropFraction;
        try { cropFraction = ScreenshotPreview.ParseCrop(crop); }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return ToolJson(new { error = ex.Message }, isError: true); }

        var (game, _) = SkyLinkSettings.SkyrimPath();
        var started = DateTime.Now.AddSeconds(-1);
        var data = await _pipe.SendRequestAsync("take_screenshot");

        // sScreenShotBaseName is a path prefix: absolute, or relative to the game folder.
        var baseName = data?["baseName"]?.GetValue<string>() ?? "ScreenShot";
        string? folder = null;
        var prefix = Path.GetFileName(baseName);
        if (Path.IsPathRooted(baseName)) folder = Path.GetDirectoryName(baseName);
        else if (game != null) folder = Path.GetDirectoryName(Path.Combine(game, baseName));
        if (string.IsNullOrEmpty(prefix)) prefix = "ScreenShot";

        if (folder == null || !Directory.Exists(folder))
            return ToolJson(new { queued = true, verified = false, baseName, note = $"Screenshot queued, but the folder it goes to ({folder ?? "unknown"}) can't be checked.", plugin = DeserializeResponse(data) });

        waitSeconds = Math.Clamp(waitSeconds, 1, 30);
        for (var waited = 0; waited < waitSeconds * 1000; waited += 250)
        {
            await Task.Delay(250);
            var file = new DirectoryInfo(folder).EnumerateFiles(prefix + "*.*")
                .Where(f => f.LastWriteTime >= started && !f.Name.EndsWith("_preview.jpg", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            if (file != null)
            {
                // The game numbers screenshots from 0 each session and overwrites older files of the
                // same name, so give this one a timestamped name once it has finished writing.
                var saved = await KeepScreenshot(file);
                await NotifyInGame("Screenshot taken");

                ScreenshotPreview.Result? preview = null;
                string? previewError = null;
                if (previewWidth > 0 && OperatingSystem.IsWindows())
                {
                    try { preview = ScreenshotPreview.Make(saved.FullName, Math.Clamp(previewWidth, 64, 4096), cropFraction); }
                    catch (Exception ex) { previewError = $"No preview: {ex.Message}"; }
                }

                var result = ToolJson(new
                {
                    success = true,
                    file = saved.FullName,
                    written = saved.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    bytes = saved.Length,
                    renamedFrom = saved.FullName == file.FullName ? null : file.Name,
                    preview = preview == null ? null : new
                    {
                        file = preview.Path,
                        width = preview.Width,
                        height = preview.Height,
                        bytes = preview.Bytes,
                        sourceSize = $"{preview.SourceWidth}x{preview.SourceHeight}",
                        cropPixels = $"{preview.Crop.X},{preview.Crop.Y},{preview.Crop.Width},{preview.Crop.Height}",
                    },
                    previewError,
                });
                if (showImage && preview != null)
                {
                    result.Content.Add(new ImageContentBlock
                    {
                        Data = Convert.ToBase64String(await File.ReadAllBytesAsync(preview.Path)),
                        MimeType = "image/jpeg",
                    });
                }
                return result;
            }
        }
        return ToolJson(new
        {
            error = $"The screenshot was queued but no new {prefix}* file appeared in {folder} within {waitSeconds} seconds.",
            plugin = DeserializeResponse(data),
            hint = "Skyrim pauses while it isn't the active window, and the screenshot is only written when it renders again: " +
                   "ask the player to click into the game, then call again with a longer waitSeconds.",
        }, isError: true);
    }

    private static CallToolResult ToolJson(object value, bool isError = false) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value) }],
        IsError = isError,
    };

    [McpServerTool]
    [Description("Load a save game by name. Immediately begins loading — current game state is lost. " +
        "CAUTION: Any unsaved progress will be lost. Use SaveGame first if needed. A save of a different character than the one in " +
        "the game is refused at first (some mods crash on it: go through the main menu); otherCharacter: true loads it anyway.")]
    public async Task<object> LoadSave(string saveName, bool otherCharacter = false)
    {
        // Round C: a name that doesn't exist answered {"loading":true} and the game stayed where it was.
        var folder = SaveTools.SaveFolder();
        var bare = saveName.EndsWith(".ess", StringComparison.OrdinalIgnoreCase) ? saveName[..^4] : saveName;
        // MO2 profiles with local saves keep them in the profile's own saves folder.
        var (mo2, profile) = SkyLinkSettings.Mo2Instance();
        var profileSaves = mo2 != null ? Path.Combine(mo2, "profiles", profile ?? "Default", "saves") : null;
        if (Directory.Exists(folder) && !File.Exists(Path.Combine(folder, bare + ".ess")) &&
            !(profileSaves != null && File.Exists(Path.Combine(profileSaves, bare + ".ess"))))
        {
            // Words only (round C: "2026" and "11" matched every dated save and recency filled the list), with camel case split
            // so StackTest finds StackFlood through "Stack".
            var words = System.Text.RegularExpressions.Regex.Matches(bare, "[A-Z]?[a-z]+|[A-Z]+(?![a-z])")
                .Select(m => m.Value).Where(w => w.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var close = new DirectoryInfo(folder).EnumerateFiles("*.ess")
                .Select(f => (name: Path.GetFileNameWithoutExtension(f.Name), hits: words.Count(w => f.Name.Contains(w, StringComparison.OrdinalIgnoreCase)), f.LastWriteTimeUtc))
                .Where(x => x.hits > 0).OrderByDescending(x => x.hits).ThenByDescending(x => x.LastWriteTimeUtc).Take(5).Select(x => x.name).ToArray();
            return new { error = $"No save named \"{bare}\" in {folder}. Nothing was loaded.", closeMatches = close.Length > 0 ? close : null, hint = "ListSaves(filter) finds saves by name, character or location." };
        }
        // FEAT-290: another character's save loaded over a running game crashed Wheeler (Steam, 2026-11-21); from the main menu
        // the same save loads fine.
        var file = new[] { Path.Combine(folder, bare + ".ess"), profileSaves != null ? Path.Combine(profileSaves, bare + ".ess") : null }.FirstOrDefault(f => f != null && File.Exists(f));
        if (file != null && !otherCharacter)
        {
            try
            {
                var state = await _pipe.SendRequestAsync("get_session_state");
                if (state?["gameLoaded"]?.GetValue<bool>() == true)
                {
                    var me = await _pipe.SendRequestAsync("get_appearance");
                    var (id, name) = SaveTools.CharacterOf(file);
                    var myId = me?["characterId"]?.ToString()?.ToUpperInvariant();
                    var myName = me?["name"]?.ToString();
                    var different = id != null && !string.IsNullOrEmpty(myId) ? id != myId : name != null && myName != null && !name.Equals(myName, StringComparison.Ordinal);
                    if (different)
                        return new
                        {
                            loaded = false,
                            reason = $"That save is {name ?? "another character"}'s and {myName ?? "a different character"} is in the game. Loading another character over a running game can crash some mods (Wheeler, seen on Steam).",
                            safer = "Go back to the main menu first (RestartGame with saveFirst=true, then LoadSave), or ask the player to quit to the menu.",
                            toLoadAnyway = "Call again with otherCharacter: true after the player agrees.",
                        };
                }
            }
            catch (Exception) { /* not connected, or no answer: load as asked */ }
        }
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
    [Description("Read a plugin config file (JSON, INI, TXT or TOML) from SKSE\\Plugins, e.g. 'SmoothCam.json' or 'po3_Tweaks.ini'. " +
        "Finds it in the game's Data\\SKSE\\Plugins or, for MO2 and Wabbajack setups, in MO2's overwrite folder and enabled mods " +
        "(set mo2Path with SetSkyLinkSetting). Returns the copy the game uses, its path, and any other copies. " +
        "JSON is returned parsed; other formats as text.")]
    public async Task<object> ReadPluginConfig(string filename)
    {
        if (!IsPlainFileName(filename)) return new { error = "Pass a file name only, with no folders or '..'" };
        var copies = SkyLinkSettings.FindConfigFile(filename);
        if (copies.Count == 0) return new { error = $"File not found: {filename}. {SkyLinkSettings.NotFoundHelp()}" };

        try
        {
            var filePath = copies[0];
            var content = await File.ReadAllTextAsync(filePath);
            var others = copies.Skip(1).ToArray();
            if (filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return new { filename, path = filePath, otherCopies = others, format = "json", data = JsonSerializer.Deserialize<JsonElement>(content) };
                }
                catch { /* not valid JSON: return it as text */ }
            }
            return new { filename, path = filePath, otherCopies = others, format = "text", content };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to read config: {ex.Message}" };
        }
    }

    [McpServerTool]
    [Description("Write a plugin config file (JSON, INI or TXT) in SKSE\\Plugins. Overwrites the copy the game uses (see ReadPluginConfig), " +
        "after saving the old one as <file>.bak. A new file goes to MO2's overwrite folder when mo2Path is set, otherwise to Data\\SKSE\\Plugins. " +
        "Changes take effect on the next MCM reload or game restart, depending on the mod.")]
    public async Task<object> WritePluginConfig(string filename, string content)
    {
        if (!IsPlainFileName(filename)) return new { error = "Invalid filename: no path separators or '..' allowed" };
        if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            !filename.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
            !filename.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return new { error = "Only .json, .ini, and .txt files can be written" };

        try
        {
            var copies = SkyLinkSettings.FindConfigFile(filename);
            string filePath;
            string? backup = null;
            if (copies.Count > 0)
            {
                filePath = copies[0];
                backup = filePath + ".bak";
                File.Copy(filePath, backup, overwrite: true);
            }
            else
            {
                var target = SkyLinkSettings.Load()["mo2Path"] is JsonValue v && v.TryGetValue<string>(out var mo2) && mo2.Length > 0
                    ? Path.Combine(mo2, "overwrite", "SKSE", "Plugins")
                    : SkyLinkSettings.DataPath() is string data ? Path.Combine(data, "SKSE", "Plugins") : null;
                if (target == null) return new { error = $"Don't know where SKSE\\Plugins is. {SkyLinkSettings.NotFoundHelp()}" };
                Directory.CreateDirectory(target);
                filePath = Path.Combine(target, filename);
            }

            await File.WriteAllTextAsync(filePath, content);
            await NotifyInGame($"Config saved: {filename}");
            return new { success = true, filename, path = filePath, backup };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to write config: {ex.Message}" };
        }
    }

    [McpServerTool]
    [Description("List SKSE plugin config files (.json, .ini, .txt, .toml) whose name contains pattern ('*' or empty for all), " +
        "across Data\\SKSE\\Plugins and, with mo2Path set, MO2's overwrite folder and enabled mods. " +
        "Returns file names, folders, sizes and dates; when a file exists in several places, the first listed is the one the game uses.")]
    public Task<object> ListPluginConfigs(string pattern = "*")
    {
        try
        {
            var needle = pattern.Trim('*', ' ');
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<object>();
            foreach (var folder in SkyLinkSettings.ConfigFolders())
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    var name = Path.GetFileName(f);
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is not (".json" or ".ini" or ".txt" or ".toml")) continue;
                    if (needle.Length > 0 && !name.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                    var fi = new FileInfo(f);
                    files.Add(new
                    {
                        filename = name,
                        folder,
                        used = seen.Add(name),
                        sizeBytes = fi.Length,
                        lastModified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    });
                }
            }
            if (files.Count == 0 && !SkyLinkSettings.ConfigFolders().Any(Directory.Exists))
                return Task.FromResult<object>(new { error = $"No SKSE\\Plugins folder found. {SkyLinkSettings.NotFoundHelp()}" });
            return Task.FromResult<object>(new { files, count = files.Count, pattern });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new { error = $"Failed to list configs: {ex.Message}" });
        }
    }

    [McpServerTool]
    [Description("Show SkyLink's settings (Documents\\My Games\\Skyrim Special Edition\\SkyLinkAI\\settings.json), what each one does, " +
        "the game folder SkyLink found and how, and the folders it searches for mod config files.")]
    public Task<object> GetSkyLinkSettings()
    {
        var (game, source) = SkyLinkSettings.SkyrimPath();
        string? rawError = null;
        try { if (File.Exists(SkyLinkSettings.FilePath)) JsonNode.Parse(File.ReadAllText(SkyLinkSettings.FilePath)); }
        catch (Exception ex) { rawError = ex.Message; }
        return Task.FromResult<object>(new
        {
            file = SkyLinkSettings.FilePath,
            settings = DeserializeResponse(SkyLinkSettings.Load()),
            fileError = rawError,
            available = SkyLinkSettings.Known,
            skyrimPath = game,
            skyrimPathFrom = source,
            dataPath = SkyLinkSettings.DataPath(),
            configFolders = SkyLinkSettings.ConfigFolders(),
        });
    }

    [McpServerTool]
    [Description("Set one SkyLink setting (see GetSkyLinkSettings for the list): skyrimPath, dataPath, mo2Path, mo2Profile, " +
        "resaverPath, creationKitPath, xeditPath, lootPath, extraConfigFolders (comma-separated folders), or dismissMessageBoxes (adds one piece of " +
        "a message box's text, with the player's agreement). An empty value removes the setting. Folders must exist. " +
        "Use this for MO2 or Wabbajack setups where config files aren't in the game's own Data folder.")]
    public Task<object> SetSkyLinkSetting(string key, string? value = null)
    {
        if (!SkyLinkSettings.Known.ContainsKey(key))
            return Task.FromResult<object>(new { error = $"Unknown setting '{key}'. Known: {string.Join(", ", SkyLinkSettings.Known.Keys)}" });

        var settings = SkyLinkSettings.Load();
        if (string.IsNullOrWhiteSpace(value))
        {
            settings.Remove(key);
        }
        else if (key == "extraConfigFolders")
        {
            var folders = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var missing = folders.Where(f => !Directory.Exists(f)).ToArray();
            if (missing.Length > 0) return Task.FromResult<object>(new { error = $"Folder not found: {string.Join("; ", missing)}" });
            settings[key] = new JsonArray(folders.Select(f => (JsonNode)f).ToArray());
        }
        else if (key == "dismissMessageBoxes")
        {
            // One more piece of a box's text; an empty value forgets them all. SkyLink still closes only boxes with a single button.
            var piece = value.Trim();
            if (piece.Length < 8) return Task.FromResult<object>(new { error = "Pass at least 8 characters of the box's text, so it can't match other boxes." });
            var list = settings[key] as JsonArray ?? [];
            if (!list.Any(x => x?.ToString() == piece)) list.Add(piece);
            settings[key] = list.DeepClone();
        }
        else if (key == "mo2Profile")
        {
            settings[key] = value.Trim();
        }
        else
        {
            var folder = value.Trim().TrimEnd('\\', '/');
            if (!Directory.Exists(folder)) return Task.FromResult<object>(new { error = $"Folder not found: {folder}" });
            if (key == "mo2Path" && !Directory.Exists(Path.Combine(folder, "mods")))
                return Task.FromResult<object>(new { error = $"{folder} has no 'mods' folder; pass the MO2 instance folder" });
            if (key == "skyrimPath" && !File.Exists(Path.Combine(folder, "SkyrimSE.exe")) && !File.Exists(Path.Combine(folder, "SkyrimVR.exe")))
                return Task.FromResult<object>(new { error = $"{folder} has no SkyrimSE.exe; pass the game folder" });
            if (key == "creationKitPath" && !File.Exists(Path.Combine(folder, "Papyrus Compiler", "PapyrusCompiler.exe")))
                return Task.FromResult<object>(new { error = $"{folder} has no Papyrus Compiler\\PapyrusCompiler.exe; pass the Creation Kit's folder" });
            settings[key] = folder;
        }

        try
        {
            SkyLinkSettings.Save(settings);
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new { error = $"Could not save settings: {ex.Message}" });
        }
        // Settings are kept per game (Steam and GOG have their own). Which one is decided by the game SkyLink last connected to.
        var forGame = SkyLinkSettings.GameDocumentsFolder != null || SkyLinkSettings.RunningGameFolder() != null ? Path.GetFileName(SkyLinkSettings.DocumentsFolder.TrimEnd('\\')) : null;
        return Task.FromResult<object>(new
        {
            saved = string.IsNullOrWhiteSpace(value) ? null : key,
            removed = string.IsNullOrWhiteSpace(value) ? key : null,
            file = SkyLinkSettings.FilePath,
            forGame = forGame ?? "Skyrim Special Edition (default: no game connected yet)",
            warning = forGame == null ? "No game has connected since the server started, so this went to the Steam game's settings. For the GOG game, start it first and set it again." : null,
        });
    }

    private static bool IsPlainFileName(string filename) =>
        !string.IsNullOrWhiteSpace(filename) && !filename.Contains("..") && filename.IndexOfAny(new[] { '/', '\\', ':' }) < 0;
}
