using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkyrimMCP.Tools;

/// <summary>
/// Base class for all MCP tool classes. Provides shared pipe access and helper methods.
/// </summary>
public abstract class ToolBase
{
    protected readonly IPipeClient _pipe;

    protected ToolBase(IPipeClient pipe)
    {
        _pipe = pipe;
    }

    /// <summary>
    /// FEAT-260: what each kind of action needs before it runs. "move" (Teleport, studio trips): the player free to move, no
    /// animation scene. "camera" (captures, measurements, probes) and "face" (face and body edits): no animation or scripted
    /// scene, no script-held controls, no conversation, combat, kill move, load or save; "face" also not in beast form.
    /// "camera" also refuses while she is using furniture (the pose reset stands her up); "camera-seated" doesn't.
    /// "actor" (moving someone else): that actor not in an animation scene. Returns the reason it can't run, or null.
    /// Reads (inventory, quests, morphs...) have no gate.
    /// </summary>
    protected async Task<string?> SafetyBlock(string kind, string? actorRef = null)
    {
        var reasons = new List<string>();
        var actor = string.IsNullOrWhiteSpace(actorRef) ? "player" : actorRef.Trim();
        if (await InAnimationScene(actor) is string scene) reasons.Add(scene);
        if (kind != "actor")
        {
            JsonNode? safety = null;
            try { safety = await _pipe.SendRequestAsync("get_game_safety"); } catch (Exception) { }
            var blockers = (safety?["moveBlockers"] as JsonArray ?? new JsonArray()).Select(b => b?.ToString() ?? "").ToList();
            var notes = (safety?["notes"] as JsonArray ?? new JsonArray()).Select(b => b?.ToString() ?? "").ToList();
            string[] stillWords = ["scripted scene", "controls are taken", "conversation", "combat", "kill move", "loading", "saving"];
            // A scene poses her off the ground; "in the air, try again in a moment" would mislead (BUG-076).
            if (reasons.Count > 0) blockers = blockers.Where(b => !b.Contains("in the air")).ToList();
            if (kind == "move") reasons.AddRange(blockers);
            else reasons.AddRange(blockers.Where(b => stillWords.Any(w => b.Contains(w, StringComparison.OrdinalIgnoreCase))));
            // BUG-077: a capture's neutral-pose reset pulled her out of a chair (seen in game); measurements also need her
            // standing. "camera-seated" is a capture that keeps her pose (CaptureFace neutralPose=false).
            if (kind == "camera") reasons.AddRange(blockers.Where(b => b.Contains("sitting, lying")).Select(b => b + " (a capture would pull her out of it)"));
            if (kind == "face" && actor == "player") reasons.AddRange(notes.Where(n => n.Contains("beast form")));
        }
        return reasons.Count == 0 ? null : "Not now: " + string.Join("; ", reasons.Distinct()) + ".";
    }

    // OStim (and frameworks exposing the same call): OActor.IsInOStim(actor). Without OStim the call fails and this is null.
    private async Task<string?> InAnimationScene(string actor)
    {
        try
        {
            var r = await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject
            {
                ["className"] = "OActor", ["functionName"] = "IsInOStim", ["args"] = new JsonArray(actor),
            });
            if (r?["result"] is JsonValue v && v.TryGetValue<bool>(out var inScene) && inScene)
                return (actor == "player" ? "she is" : actor + " is") + " in an OStim scene: wait until it ends";
        }
        catch (Exception) { }
        return null;
    }

    protected static ModelContextProtocol.Protocol.CallToolResult Blocked(string reason) => new()
    {
        Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = JsonSerializer.Serialize(new { error = reason }) }],
        IsError = true,
    };

    /// <summary>
    /// Send an in-game notification to the player's HUD.
    /// Fire-and-forget — errors are silently ignored.
    /// </summary>
    protected async Task NotifyInGame(string message)
    {
        try
        {
            await _pipe.SendRequestAsync("show_notification", new JsonObject
            {
                ["message"] = message
            });
        }
        catch { /* notification failure shouldn't break the tool */ }
    }

    /// <summary>
    /// Deserialize a JsonNode response to an object suitable for MCP tool return.
    /// </summary>
    protected static object DeserializeResponse(JsonNode? data)
    {
        return (object?)JsonSerializer.Deserialize<JsonElement>(data?.ToJsonString() ?? "{}") ?? new { error = "No data returned" };
    }

    /// <summary>
    /// Apply paging to a response that contains an array field.
    /// Returns a paged envelope with items, page, pageSize, totalItems, totalPages, hasMore.
    /// If page=0 or pageSize=0, returns summary only (no items).
    /// </summary>
    protected static object PageResponse(JsonNode? data, string arrayField, int page = 1, int pageSize = 50)
    {
        if (data == null) return new { error = "No data returned" };

        var array = data[arrayField]?.AsArray();
        if (array == null) return DeserializeResponse(data);

        int totalItems = array.Count;
        int totalPages = pageSize > 0 ? (int)Math.Ceiling((double)totalItems / pageSize) : 1;

        // Summary only mode
        if (page == 0 || pageSize == 0)
        {
            return new
            {
                totalItems,
                totalPages = pageSize > 0 ? totalPages : 0,
                summaryOnly = true
            };
        }

        // Slice the array
        var items = array
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => JsonSerializer.Deserialize<JsonElement>(i?.ToJsonString() ?? "{}"))
            .ToArray();

        return new
        {
            items,
            page,
            pageSize,
            totalItems,
            totalPages,
            hasMore = page < totalPages
        };
    }
}
