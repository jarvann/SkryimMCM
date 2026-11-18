using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// MCP Tools for world interaction — weather, cell info, objects, locks, containers, map, teleport, god mode, stats
/// </summary>
[McpServerToolType]
public class WorldTools : ToolBase
{
    public WorldTools(IPipeClient pipe) : base(pipe) { }

    [McpServerTool]
    [Description("Get the current and previous weather and how far the transition has got (weatherPct). " +
        "Most weathers have no in-game name: 'name' falls back to the editor ID, then to '<category> weather <formId>'. " +
        "currentWeather and previousWeather add category (clear, cloudy, rain, snow, other) and the mod it comes from.")]
    public async Task<object> GetWeather()
    {
        var data = await _pipe.SendRequestAsync("get_weather");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("List all available weather types with FormIDs, editor IDs, categories (clear, cloudy, rain, snow, other), " +
        "and which mod they come from. Use the FormID or editor ID with SetWeather to change the weather.")]
    public async Task<object> ListWeathers(int page = 1, int pageSize = 50)
    {
        var data = await _pipe.SendRequestAsync("list_weathers");
        return PageResponse(data, "weathers", page, pageSize);
    }

    [McpServerTool]
    [Description("Change the weather. Pass a weather FormID or a search term like 'snow', 'rain', 'clear', 'storm', 'fog'. " +
        "The weather transitions naturally over a few seconds.")]
    public async Task<object> SetWeather(string weather)
    {
        var data = await _pipe.SendRequestAsync("set_weather", new JsonObject { ["weather"] = weather });
        var name = data?["weather"]?.GetValue<string>() ?? weather;
        await NotifyInGame($"Weather: {name}");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get detailed info about the player's current cell/location including interior/exterior status, " +
        "worldspace, exact coordinates, and facing angle.")]
    public async Task<object> GetCellInfo()
    {
        var data = await _pipe.SendRequestAsync("get_cell_info");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Find nearby objects like containers, doors, furniture, crafting stations, activators, and flora. " +
        "Filter by type: 'all', 'container', 'chest', 'door', 'furniture', 'crafting', 'activator', 'flora', 'light'. " +
        "Returns refId, name, type, distance, and locked status. Use this for 'what's nearby?' or 'find a forge'.")]
    public async Task<object> GetNearbyObjects(float radius = 2048, string type = "all", int page = 1, int pageSize = 50)
    {
        var data = await _pipe.SendRequestAsync("get_nearby_objects", new JsonObject
        {
            ["radius"] = radius,
            ["type"] = type
        });
        return PageResponse(data, "objects", page, pageSize);
    }

    [McpServerTool]
    [Description("Unlock a locked door or container by reference ID. Use GetNearbyObjects or GetCrosshairRef to find the refId.")]
    public async Task<object> UnlockDoor(string refId)
    {
        var data = await _pipe.SendRequestAsync("unlock_door", new JsonObject { ["refId"] = refId });
        await NotifyInGame("Unlocked");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Lock a door or container by reference ID. lockLevel: 1=novice, 25=apprentice, 50=adept, 75=expert, 100=master, 255=requires key.")]
    public async Task<object> LockDoor(string refId, int lockLevel = 50)
    {
        var data = await _pipe.SendRequestAsync("lock_door", new JsonObject { ["refId"] = refId, ["lockLevel"] = lockLevel });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Read the inventory of a container by reference ID. Use GetNearbyObjects to find containers, then pass the refId. " +
        "Shows all items with name, FormID, count, weight, and value. Also shows locked status.")]
    public async Task<object> GetContainerInventory(string refId)
    {
        var data = await _pipe.SendRequestAsync("get_container_inventory", new JsonObject { ["refId"] = refId });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Discover all map markers on the map. Uses tmm 1 console command. " +
        "CAUTION: This reveals every location on the map and cannot be undone without reloading a save.")]
    public async Task<object> DiscoverAllMapMarkers()
    {
        var data = await _pipe.SendRequestAsync("discover_all_map_markers");
        await NotifyInGame("All map markers discovered");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Teleport the player to a location (e.g. 'whiterun', 'riverwood', 'solitude') or a cell editor ID (SearchForms type " +
        "'cell' finds them); an unknown cell is refused. location 'back' returns her to where the last Teleport started, facing the " +
        "same way. Tell the player first: the screen freezes for several seconds (the game may show as not responding), and " +
        "teleporting during combat, scenes or dialogue can crash the game. The reply waits up to 20 seconds for her to arrive, says " +
        "where she is, and lists followers left far away (MoveActorTo brings them).")]
    public async Task<object> Teleport(string location)
    {
        var backFile = Path.Combine(SkyLinkSettings.Folder, "teleport_back.json");
        var before = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "status" });
        var fromCell = before?["cellFormId"]?.ToString();
        var followersBefore = await FollowerNames();
        string? markerId = null;
        string? oldMarker = null;
        try { if (File.Exists(backFile)) oldMarker = JsonNode.Parse(await File.ReadAllTextAsync(backFile))?["markerId"]?.ToString(); } catch (Exception) { }

        if (location.Trim().Equals("back", StringComparison.OrdinalIgnoreCase))
        {
            // BUG-072: back to the marked starting spot, no console coordinates.
            if (oldMarker == null) return new { error = "No earlier Teleport on record to go back to." };
            var r = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "return", ["markerId"] = oldMarker });
            if (r?["error"] != null) return DeserializeResponse(r);
            var back = await WaitForArrival(fromCell);
            if (back is JsonObject)
            {
                await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "cleanup", ["markerId"] = oldMarker });
                File.Delete(backFile);
            }
            return await ArrivalReply("back", null, back, followersBefore);
        }

        var cellId = SkyrimOffsets.GetLocationCellId(location) ?? location.Trim();
        var data = await _pipe.SendRequestAsync("teleport", new JsonObject { ["cellId"] = cellId });
        if (data?["error"] != null) return DeserializeResponse(data);
        markerId = data?["markerId"]?.ToString();
        if (markerId != null)
        {
            // Keep only the newest starting spot: "back" means the last trip.
            if (oldMarker != null && oldMarker != markerId)
                try { await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "cleanup", ["markerId"] = oldMarker }); } catch (Exception) { }
            Directory.CreateDirectory(SkyLinkSettings.Folder);
            await File.WriteAllTextAsync(backFile, new JsonObject { ["markerId"] = markerId, ["from"] = before?["cellName"]?.DeepClone() }.ToJsonString());
        }
        return await ArrivalReply(location, cellId, await WaitForArrival(fromCell), followersBefore);
    }

    // BUG-071: waits out the frozen screen (the game thread doesn't answer during it); the new cell, or null after 20 s.
    private async Task<JsonNode?> WaitForArrival(string? fromCell)
    {
        for (var waited = 0; waited < 20000; waited += 750)
        {
            await Task.Delay(750);
            try
            {
                var now = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "status" });
                var cell = now?["cellFormId"]?.ToString();
                if (cell != null && cell != fromCell) return now;
            }
            catch (Exception) { /* still loading */ }
        }
        return null;
    }

    // Followers by reference ID, with names (only followers in loaded cells are found).
    private async Task<Dictionary<string, string>> FollowerNames()
    {
        var map = new Dictionary<string, string>();
        try
        {
            var followers = await _pipe.SendRequestAsync("get_followers");
            foreach (var f in (followers?["followers"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (f["refId"]?.ToString() is string id) map[id] = f["name"]?.ToString() ?? id;
        }
        catch (Exception) { }
        return map;
    }

    private async Task<object> ArrivalReply(string location, string? cellId, JsonNode? arrived, Dictionary<string, string> followersBefore)
    {
        if (arrived == null)
            return new { success = true, arrived = false, location, cellId,
                         note = "The move started but she hadn't arrived after 20 seconds. Check with GetCellInfo shortly." };
        // BUG-072: followers don't always come along, and once she is far away they aren't found at all: compare with the
        // followers she had before the move.
        await Task.Delay(1500);  // followers that do come along arrive just after her
        var leftBehind = new List<object>();
        try
        {
            var near = new Dictionary<string, double>();
            var followers = await _pipe.SendRequestAsync("get_followers");
            foreach (var f in (followers?["followers"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (f["refId"]?.ToString() is string id) near[id] = f["distance"]?.GetValue<double>() ?? 0;
            foreach (var (id, name) in followersBefore)
                if (!near.TryGetValue(id, out var d) || d > 3000)
                    leftBehind.Add(new { name, refId = id, distance = near.ContainsKey(id) ? Math.Round(d).ToString() : "not nearby" });
        }
        catch (Exception) { }
        return new
        {
            success = true, arrived = true, location, cellId,
            cellName = arrived["cellName"]?.ToString(), cellFormId = arrived["cellFormId"]?.ToString(),
            followersLeftBehind = leftBehind.Count > 0 ? leftBehind : null,
            followersNote = leftBehind.Count > 0
                ? "MoveActorTo(refId) brings a follower to her. Whether followers travel depends on how long she stays; they may still catch up." : null,
            back = location == "back" ? null : "Teleport('back') returns her to where she started.",
        };
    }

    [McpServerTool]
    [Description("Toggle collision for the player or a specific actor/object, the same as the console's tcl. Returns the new state as the game reports it. " +
        "Pass refId to toggle collision on a specific target, or leave empty for the player. " +
        "CAUTION: Disabling collision can cause the target to fall through the world. " +
        "Remind the user to save before using and to re-enable collision when done.")]
    public async Task<object> ToggleCollision(string? refId = null)
    {
        var parms = new JsonObject();
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        var data = await _pipe.SendRequestAsync("toggle_collision", parms);
        // The plugin reads the state from the game's own console reply; null means the
        // reply was missing, so say so rather than guess.
        bool? enabled = data?["collisionEnabled"]?.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
        var target = data?["target"]?.GetValue<string>() ?? "Player";
        var consoleOutput = data?["consoleOutput"]?.GetValue<string>() ?? "";
        var msg = enabled switch
        {
            true => $"Collision On ({target})",
            false => $"No Clip ({target})",
            null => $"Collision toggled ({target}); the game did not report the new state",
        };
        await NotifyInGame(msg);
        return new { success = true, collisionEnabled = enabled, target, consoleOutput, message = msg };
    }

    [McpServerTool]
    [Description("Set player's health to a specific value")]
    public async Task<object> SetHealth(float value)
    {
        await _pipe.SendRequestAsync("set_actor_value", new JsonObject
        {
            ["attribute"] = "health",
            ["value"] = value
        });
        await NotifyInGame($"Health set to {value}");
        return new { success = true, message = $"Set health to {value}" };
    }

    [McpServerTool]
    [Description("Set player's magicka to a specific value")]
    public async Task<object> SetMagicka(float value)
    {
        await _pipe.SendRequestAsync("set_actor_value", new JsonObject
        {
            ["attribute"] = "magicka",
            ["value"] = value
        });
        await NotifyInGame($"Magicka set to {value}");
        return new { success = true, message = $"Set magicka to {value}" };
    }

    [McpServerTool]
    [Description("Set player's stamina to a specific value")]
    public async Task<object> SetStamina(float value)
    {
        await _pipe.SendRequestAsync("set_actor_value", new JsonObject
        {
            ["attribute"] = "stamina",
            ["value"] = value
        });
        await NotifyInGame($"Stamina set to {value}");
        return new { success = true, message = $"Set stamina to {value}" };
    }

    [McpServerTool]
    [Description("Find nearby merchants/vendors within a given radius. Returns their name, merchant faction, " +
        "distance, and refId for use with GetMerchantInventory.")]
    public async Task<object> GetNearbyMerchants(float radius = 4096)
    {
        var data = await _pipe.SendRequestAsync("get_nearby_merchants", new JsonObject { ["radius"] = radius });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Read a merchant's inventory — what they have for sale. Pass the refId from GetNearbyMerchants. " +
        "Shows items with name, FormID, count, value, weight, and type.")]
    public async Task<object> GetMerchantInventory(string refId)
    {
        var data = await _pipe.SendRequestAsync("get_merchant_inventory", new JsonObject { ["refId"] = refId });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get all active bounties across all holds/factions. Shows which factions have bounties on the player and how much.")]
    public async Task<object> GetBounties()
    {
        var data = await _pipe.SendRequestAsync("get_bounties");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Clear the player's bounty with a specific faction. Pass the factionFormId from GetBounties. " +
        "CAUTION: This removes the bounty without paying — guards will still be hostile until you leave and return.")]
    public async Task<object> ClearBounty(string factionFormId)
    {
        var data = await _pipe.SendRequestAsync("clear_bounty", new JsonObject { ["factionFormId"] = factionFormId });
        await NotifyInGame("Bounty cleared");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Scan nearby containers for valuable loot. Finds all containers within radius, " +
        "reads their inventories, and returns items sorted by value. " +
        "Shows container name, distance, locked status, and top items by value.")]
    public async Task<object> ScanLoot(float radius = 2048, int minValue = 10)
    {
        // Step 1: find nearby containers
        var objectsData = await _pipe.SendRequestAsync("get_nearby_objects", new JsonObject
        {
            ["radius"] = radius,
            ["type"] = "container"
        });

        var objects = objectsData?["objects"]?.AsArray();
        if (objects == null || objects.Count == 0)
            return new { containers = Array.Empty<object>(), message = "No containers nearby" };

        var results = new List<object>();

        // Step 2: read each container's inventory
        foreach (var obj in objects)
        {
            var refId = obj?["refId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(refId)) continue;

            try
            {
                var invData = await _pipe.SendRequestAsync("get_container_inventory", new JsonObject
                {
                    ["refId"] = refId
                });

                var items = invData?["items"]?.AsArray();
                if (items == null || items.Count == 0) continue;

                // Filter and sort by value
                var valuableItems = items
                    .Where(i => (i?["value"]?.GetValue<int>() ?? 0) >= minValue)
                    .OrderByDescending(i => i?["value"]?.GetValue<int>() ?? 0)
                    .Take(10)
                    .Select(i => new
                    {
                        name = i?["name"]?.GetValue<string>() ?? "",
                        value = i?["value"]?.GetValue<int>() ?? 0,
                        count = i?["count"]?.GetValue<int>() ?? 0,
                        formId = i?["formId"]?.GetValue<string>() ?? ""
                    })
                    .ToList();

                if (valuableItems.Count == 0) continue;

                var totalValue = valuableItems.Sum(i => i.value * i.count);

                results.Add(new
                {
                    container = obj?["name"]?.GetValue<string>() ?? "Unknown",
                    refId,
                    distance = obj?["distance"]?.GetValue<float>() ?? 0,
                    isLocked = obj?["isLocked"]?.GetValue<bool>() ?? false,
                    totalValue,
                    topItems = valuableItems
                });
            }
            catch { continue; }
        }

        // Sort containers by total value
        var sorted = results.OrderByDescending(r => ((dynamic)r).totalValue).ToList();

        return new { containers = sorted, count = sorted.Count };
    }
}
