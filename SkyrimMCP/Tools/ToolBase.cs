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
    /// <summary>
    /// Maps a blueprint entry's plugin + local ID (formIdSource) to this game's form ID through the load order; falls back to the
    /// raw formId. Null when the plugin isn't loaded.
    /// </summary>
    protected async Task<Func<JsonObject, string?>> PortableResolver()
    {
        JsonNode? lo = null;
        try { lo = await _pipe.SendRequestAsync("get_load_order"); } catch (Exception) { }
        var mods = (lo?["mods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .GroupBy(m => m["name"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()["prefix"]?.ToString(), StringComparer.OrdinalIgnoreCase);
        return entry =>
        {
            if (entry["formIdSource"] is JsonObject src && src["plugin"]?.ToString() is string plugin && src["localId"]?.ToString() is string local)
            {
                if (!mods.TryGetValue(plugin, out var prefix) || prefix == null) return null;
                var id = Convert.ToUInt32(local, 16);
                return prefix.Length == 5 ? $"{prefix}{id & 0xFFF:X3}" : $"{prefix}{id & 0xFFFFFF:X6}";
            }
            return entry["formId"]?.ToString();
        };
    }

    // null = not checked yet; false = MCM Recorder isn't installed (don't ask again this session).
    private static bool? s_mcmRecorderInstalled;

    /// <summary>The reason to wait when MCM Recorder is playing a recording, or null.</summary>
    protected async Task<string?> McmRecorderPlaying()
    {
        if (s_mcmRecorderInstalled == false) return null;
        try
        {
            var r = await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject
            {
                ["className"] = "McmRecorder_Player", ["functionName"] = "IsPlayingRecording", ["args"] = new JsonArray(),
            });
            if (r?["error"] != null)
            {
                if (r["error"]!.ToString().Contains("not found", StringComparison.OrdinalIgnoreCase)) s_mcmRecorderInstalled = false;
                return null;
            }
            s_mcmRecorderInstalled = true;
            if (r?["result"] is not JsonValue v || !v.TryGetValue<bool>(out var playing) || !playing)
            {
                // A recording session: whoever records is changing mods' settings in their menus (2026-11-20: face edits were
                // allowed meanwhile).
                try
                {
                    var rec = await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject
                    {
                        ["className"] = "McmRecorder_Recorder", ["functionName"] = "IsRecording", ["args"] = new JsonArray(),
                    });
                    if (rec?["result"] is JsonValue rv && rv.TryGetValue<bool>(out var recording) && recording)
                        return "MCM Recorder is recording settings: finish or stop the recording first";
                }
                catch (Exception) { }
                return null;
            }
            string? name = null;
            try
            {
                name = (await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject
                {
                    ["className"] = "McmRecorder_Player", ["functionName"] = "GetCurrentlyPlayingRecordingName", ["args"] = new JsonArray(),
                }))?["result"]?.ToString();
            }
            catch (Exception) { }
            return $"MCM Recorder is playing a recording{(string.IsNullOrEmpty(name) ? "" : $" ('{name}')")}: wait until it finishes";
        }
        catch (ModelContextProtocol.McpException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            s_mcmRecorderInstalled = false;
            return null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// FEAT-302: closes message boxes the player has told SkyLink to close (setting dismissMessageBoxes): only boxes with a single button,
    /// where closing decides nothing. Returns the text of those closed.
    /// </summary>
    protected async Task<List<string>> DismissKnownMessageBoxes()
    {
        List<string> closed = [];
        if (SkyLinkSettings.Load()["dismissMessageBoxes"] is not JsonArray { Count: > 0 } known) return closed;
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var box = await _pipe.SendRequestAsync("get_message_box");
                if (box?["readable"]?.GetValue<bool>() != true || (box["buttons"] as JsonArray)?.Count != 1) break;
                var text = box["text"]?.ToString() ?? "";
                if (!known.Any(k => k?.ToString() is { Length: >= 8 } piece && text.Contains(piece, StringComparison.OrdinalIgnoreCase))) break;
                await _pipe.SendRequestAsync("answer_message_box", new JsonObject { ["boxId"] = box["boxId"]?.ToString(), ["button"] = 0 });
                closed.Add(text);
                await Task.Delay(700);
            }
        }
        catch (Exception) { }
        return closed;
    }

    protected async Task<string?> SafetyBlock(string kind, string? actorRef = null)
    {
        if (kind != "actor") await DismissKnownMessageBoxes();
        var reasons = new List<string>();
        var actor = string.IsNullOrWhiteSpace(actorRef) ? "player" : actorRef.Trim();
        // FEAT-261: MCM Recorder plays recorded menu settings step by step; between steps no menu is open, so the menu checks
        // pass, but a move, capture or face load would knock a step out.
        if (kind != "actor" && await McmRecorderPlaying() is string recording) reasons.Add(recording);
        // An OStim scene seen through its menu (OStim's API can report none while its scene runs): blocks every kind,
        // moving another actor too.
        if (kind == "actor")
        {
            try
            {
                var safety = await _pipe.SendRequestAsync("get_game_safety");
                reasons.AddRange((safety?["moveBlockers"] as JsonArray ?? new JsonArray()).Select(b => b?.ToString() ?? "").Where(b => b.Contains("OStim scene is running")));
            }
            catch (Exception) { }
        }
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
            if (kind is "camera" or "camera-seated") reasons.AddRange(blockers.Where(b => b.Contains("on a mount")));
            // The capture refuses with a pausing menu open (BUG-057); say so in the look-ahead too (BUG-078).
            if (kind is "camera" or "camera-seated")
                reasons.AddRange(blockers.Where(b => b.Contains("a menu is open") || b.Contains("mod's menu") ||
                                                     (b.Contains(" is open (") && !b.Contains("before she is moved"))));
            if (kind == "face" && actor == "player") reasons.AddRange(notes.Where(n => n.Contains("beast form")));
            // BUG-081: a mod's modal menu or a message box is waiting on the player (MCM Recorder, a start-up mod setting her
            // up): don't rebuild her head underneath it.
            if (kind == "face") reasons.AddRange(blockers.Where(b => b.Contains("mod's menu") || b.Contains("message box")));
            // Face edits with the Journal/MCM (or another pausing menu) open were allowed (2026-11-20): someone may be changing a
            // mod's settings by hand, and RaceMenu's own menu writes the same data.
            if (kind == "face") reasons.AddRange(blockers.Where(b => b.Contains("a menu is open") || (b.Contains(" is open (") && !b.Contains("OStim"))));
        }
        if (reasons.Count == 0) return null;
        var text = "Not now: " + string.Join("; ", reasons.Distinct()) + ".";
        // BUG-078: point at what does work. A plain screenshot doesn't touch the camera or her pose.
        if (kind.StartsWith("camera")) text += " For a picture of the screen as it is, use TakeScreenshot.";
        return text;
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
            // Round B: the summary was only a count. Add what fits in a line: how many of each type, and totals where the items
            // carry weight and value (inventories).
            var objs = array.OfType<JsonObject>().ToList();
            double Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
            var hasWeight = objs.Any(o => o.ContainsKey("weight"));
            var hasValue = objs.Any(o => o.ContainsKey("value"));
            return new
            {
                totalItems,
                totalPages = pageSize > 0 ? totalPages : 0,
                summaryOnly = true,
                byType = objs.Any(o => o.ContainsKey("type"))
                    ? objs.GroupBy(o => o["type"]?.ToString() ?? "?").OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count())
                    : null,
                totalWeight = hasWeight ? Math.Round(objs.Sum(o => Num(o, "weight") * Math.Max(1, Num(o, "count"))), 1) : (double?)null,
                totalValue = hasValue ? Math.Round(objs.Sum(o => Num(o, "value") * Math.Max(1, Num(o, "count")))) : (double?)null,
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
