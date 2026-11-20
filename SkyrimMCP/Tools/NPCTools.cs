using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// MCP Tools for NPC operations — nearby NPCs, detailed info, inventory, followers, detection, crosshair, kill
/// </summary>
[McpServerToolType]
public class NPCTools : ToolBase
{
    public NPCTools(IPipeClient pipe) : base(pipe) { }

    [McpServerTool]
    [Description("Get nearby NPCs within a given radius. Returns refId (use for dot-notation console commands like 'refId.kill') " +
        "and baseId (use for setessential). Also includes name, race, level, distance, hostility, and dead status.")]
    public async Task<object> GetNearbyNPCs(float radius = 4096)
    {
        var data = await _pipe.SendRequestAsync("get_nearby_npcs", new JsonObject
        {
            ["radius"] = radius
        });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get detailed info about an NPC by reference ID — class, combat style, outfit, factions, stats, " +
        "state flags (essential, protected, ghost, child, follower, commanded), position, and hostility. " +
        "Much more detailed than GetActorInfo.")]
    public async Task<object> GetNPCDetailedInfo(string refId)
    {
        var data = await _pipe.SendRequestAsync("get_npc_detailed_info", new JsonObject { ["refId"] = refId });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Read an NPC's inventory by reference ID. Shows what items they're carrying — " +
        "weapons, armor, potions, misc items with FormIDs, counts, and values. " +
        "Alias for GetInventory with a refId — kept for convenience.")]
    public async Task<object> GetNPCInventory(string refId)
    {
        var data = await _pipe.SendRequestAsync("get_inventory", new JsonObject { ["refId"] = refId });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("List all current followers/teammates — NPCs flagged as player teammates. " +
        "Shows name, race, level, class, health, distance, combat state, and essential status.")]
    public async Task<object> GetFollowers()
    {
        var data = await _pipe.SendRequestAsync("get_followers");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Check if an NPC has detected the player. Returns detection level (0-100) and state " +
        "(undetected, lost, suspicious, detected). Use with GetNearbyNPCs to check stealth status.")]
    public async Task<object> GetDetectionLevel(string refId)
    {
        var data = await _pipe.SendRequestAsync("get_detection_level", new JsonObject { ["refId"] = refId });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get the reference ID of whatever the player's crosshair is pointing at. " +
        "Returns refId and baseId. Use refId with dot-notation for console commands (e.g., 'refId.kill'). " +
        "Use baseId for commands like setessential. " +
        "Use this when the player says 'that NPC' or 'this object' — it tells you exactly what they're looking at.")]
    public async Task<object> GetCrosshairRef()
    {
        var data = await _pipe.SendRequestAsync("get_crosshair_ref");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Kill an actor by reference ID. Removes essential/protected flags first. " +
        "Use GetCrosshairRef or GetNearbyNPCs to get the refId. " +
        "CAUTION: Killing essential NPCs can break quests. Confirm with user first.")]
    public async Task<object> KillActor(string refId)
    {
        var data = await _pipe.SendRequestAsync("kill_actor", new JsonObject
        {
            ["refId"] = refId
        });
        var name = data?["name"]?.GetValue<string>() ?? refId;
        await NotifyInGame($"{name} killed");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Move an actor (a follower left behind, an NPC) to the player or to another reference, natively (no console). " +
        "refId: the actor's reference ID (GetFollowers, GetNearbyNPCs, or a Teleport reply's followersLeftBehind). target: 'player' " +
        "(default) or a reference ID. Brings followers left behind by a teleport.")]
    public async Task<object> MoveActorTo(string refId, string target = "player")
    {
        if (await SafetyBlock("actor", refId) is string gateReason) return new { error = gateReason };  // FEAT-260
        var data = await _pipe.SendRequestAsync("move_actor_to", new JsonObject { ["actorFormId"] = refId, ["target"] = target });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Play an idle animation on the player or an NPC via native API (no console commands). " +
        "Pass the idle animation's FormID. Optional refId targets a specific NPC — leave empty for the player. " +
        "Use SearchForms with type='idle' to find animation FormIDs. " +
        "Works with all mod types including ESL-flagged plugins.")]
    public async Task<object> PlayIdle(string idleFormId, string? refId = null)
    {
        var data = await _pipe.SendRequestAsync("play_idle", new JsonObject
        {
            ["idleFormId"] = idleFormId,
            ["refId"] = refId ?? ""
        });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get the currently playing idle animation on the player or an NPC. " +
        "Returns the idle form's ID, editor ID, animation file, and event name. " +
        "Returns null if no idle is playing. Optional refId targets a specific NPC.")]
    public async Task<object> GetCurrentIdle(string? refId = null)
    {
        var data = await _pipe.SendRequestAsync("get_current_idle", new JsonObject
        {
            ["refId"] = refId ?? ""
        });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Stop the currently playing idle animation on the player or an NPC. " +
        "Use this to cancel an animation started by PlayIdle. " +
        "Optional refId targets a specific NPC — leave empty for the player.")]
    public async Task<object> StopIdle(string? refId = null)
    {
        // 2026-11-20: StopIdle forced the default pose under a running OStim scene and OStim crashed the game.
        if (await SafetyBlock("actor", refId) is string gateReason) return new { error = gateReason };
        var data = await _pipe.SendRequestAsync("stop_idle", new JsonObject
        {
            ["refId"] = refId ?? ""
        });
        return DeserializeResponse(data);
    }
}
