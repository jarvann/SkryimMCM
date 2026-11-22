using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// Face studio (FEAT-244): the same light for every face capture, so colours can be tuned by number. Studio lights are
/// engine-level point lights around the player's head (nothing goes into the save). StudioGo/StudioReturn move the
/// player to an interior with fixed light and back to the exact spot, Mark/Recall style.
/// </summary>
[McpServerToolType]
public class StudioTools : ToolBase
{
    public StudioTools(IPipeClient pipe) : base(pipe) { }

    private static string StateFile => Path.Combine(SkyLinkSettings.Folder, "studio.json");

    [McpServerTool]
    [Description("Light the player's face with a fixed studio setup for captures: a key light at her front-right, a fill light at " +
        "her front-left and a rim light behind, placed around her head and kept with her. preset: neutral (white, even on both " +
        "sides: use it for measuring, default), portrait (strong key, soft fill: for looks, trips the one-sided-light warning), warm " +
        "(indoor), daylight (warm key, cool fill), soft (key and fill equal), or off to remove them. intensity scales all three " +
        "(default 1). The lights are engine lights, not objects: nothing is saved, and they go away on a cell change or load. They " +
        "add to the scene's own light, so for steady colour use them indoors (StudioGo), always with the same preset; CaptureFace " +
        "and MeasureFace report the preset in use. refId: light that character instead of the player (a spawned NPC).")]
    public async Task<object> StudioLights(string preset = "neutral", double intensity = 1.0, string? refId = null)
    {
        var data = await _pipe.SendRequestAsync("studio", new JsonObject
        {
            ["mode"] = "lights", ["preset"] = preset.Trim().ToLowerInvariant(), ["intensity"] = intensity, ["refId"] = refId?.Trim() ?? "",
        });
        return DeserializeResponse(data);
    }

    // BUG-040: a studio trip goes through a loading screen. Waiting it out inside one call ran into the client's
    // tool timeout (a bare "An error occurred invoking" although the move worked), so each call waits at most
    // WaitSeconds and leaves the rest (face rebuild, lights, marker clean-up) to StudioStatus.
    private const int WaitSeconds = 20;

    [McpServerTool]
    [Description("Take the player to a studio: marks her exact spot and facing (a marker in the world), then moves her to an interior " +
        "cell by editor ID (default QASmoke, the developers' test hall: fixed indoor light, no sun or weather). Once she has arrived " +
        "it rebuilds the face tint (a cell change can leave it flat) and turns on the studio lights (preset as StudioLights; 'off' " +
        "for none). If the loading screen takes longer than about 20 seconds the reply says 'moving': call StudioStatus to finish. " +
        "Followers may come along. Save the game first. Refused in combat or while already in the studio. StudioReturn brings her back.")]
    public async Task<object> StudioGo(string cell = "QASmoke", string preset = "neutral", double intensity = 1.0)
    {
        if (await SafetyBlock("move", null) is string gateReason) return new { error = gateReason };  // FEAT-260
        try
        {
            if (LoadState()["markerId"] is JsonNode existing)
                return new { error = $"Already in the studio (marker {existing}): StudioReturn first." };
            var before = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "status" });
            var faceSnapshot = await new RaceMenuTools(_pipe).SnapshotFace("_studio_before_go");
            // The move can start the loading screen before the reply comes back; the marker is kept in the plugin
            // and read back from the status.
            JsonNode? go = null;
            string? goNote = null;
            try
            {
                go = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "go", ["cell"] = cell.Trim() });
                if (go?["error"] is JsonNode error) return new { error = error.ToString() };
            }
            catch (Exception ex) when (LostAcrossLoad(ex)) { goNote = "The move reply was lost across the loading screen (" + ex.Message + ")."; }
            catch (Exception ex) { return new { error = ex.Message }; }  // BUG-074: a refusal (a menu open) is an answer, not a lost reply

            MarkerRegistry.Add(go?["markerId"]?.ToString(), "studio trip start");
            var state = new JsonObject
            {
                ["markerId"] = go?["markerId"]?.DeepClone(),
                ["markedCell"] = go?["markedCell"]?.DeepClone() ?? before?["cell"]?.DeepClone(),
                ["markedPosition"] = go?["markedPosition"]?.DeepClone(),
                ["studioCell"] = cell.Trim(),
                ["faceSnapshot"] = faceSnapshot,
                ["pending"] = "arrive",
                ["preset"] = preset.Trim().ToLowerInvariant(),
                ["intensity"] = intensity,
                ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            SaveState(state);
            var arrived = await WaitForCell(c => c.Equals(cell.Trim(), StringComparison.OrdinalIgnoreCase), WaitSeconds);
            if (!arrived)
                return new { status = "moving", note = "Still on the loading screen: call StudioStatus in a few seconds to finish.", moveNote = goNote };
            var finished = await Finish(state);
            await NotifyInGame("Studio: StudioReturn brings you back");
            finished["from"] = before?["cell"]?.DeepClone();
            finished["lightSpellsDispelled"] = go?["lightSpellsDispelled"]?.DeepClone()
                ?? (go == null ? JsonValue.Create("unknown (the move reply was lost)") : new JsonArray());
            finished["moveNote"] = goNote;
            return DeserializeResponse(finished);
        }
        catch (Exception ex) { return new { error = "StudioGo: " + ex.Message, next = "Call StudioStatus to see where she is." }; }
    }

    [McpServerTool]
    [Description("Bring the player back from the studio to the exact spot and facing StudioGo marked: removes the studio lights, moves " +
        "her, rebuilds the face tint and deletes the marker once she is back. If the loading screen takes longer than about 20 " +
        "seconds the reply says 'moving': call StudioStatus to finish. markerId: only when no trip is on record (a lost StudioGo " +
        "reply); without it, the reply lists the candidate markers.")]
    public async Task<object> StudioReturn(string? markerId = null)
    {
        if (await SafetyBlock("move", null) is string gateReason) return new { error = gateReason };  // FEAT-260
        try
        {
            var state = LoadState();
            markerId ??= state["markerId"]?.GetValue<string>();
            var status = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "status" });
            markerId ??= status?["markerId"]?.GetValue<string>();
            if (markerId == null)
            {
                var found = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "find" });
                return DeserializeResponse(new JsonObject
                {
                    ["error"] = "No studio trip on record. Markers placed at run time, newest first, are listed below: check the cell " +
                                "and position against where she was, then call StudioReturn(markerId).",
                    ["candidates"] = found?["markers"]?.DeepClone(),
                });
            }
            var studioCell = state["studioCell"]?.GetValue<string>() ?? status?["cell"]?.GetValue<string>() ?? "";
            // The face as it is now, edits made in the studio included, to check the makeup against after the move.
            state["faceSnapshot"] = await new RaceMenuTools(_pipe).SnapshotFace("_studio_before_return");
            string? backNote = null;
            try
            {
                var back = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "return", ["markerId"] = markerId });
                if (back?["error"] is JsonNode error) return new { error = error.ToString(), markerId };
            }
            catch (Exception ex) when (LostAcrossLoad(ex)) { backNote = "The move reply was lost across the loading screen (" + ex.Message + ")."; }
            catch (Exception ex) { return new { error = ex.Message }; }

            state["markerId"] = markerId;
            state["studioCell"] = studioCell;
            state["pending"] = "return";
            SaveState(state);
            var left = await WaitForCell(c => !c.Equals(studioCell, StringComparison.OrdinalIgnoreCase), WaitSeconds);
            if (!left)
                return new { status = "moving", note = "Still on the loading screen: call StudioStatus in a few seconds to finish.", moveNote = backNote };
            var finished = await Finish(state);
            finished["moveNote"] = backNote;
            return DeserializeResponse(finished);
        }
        catch (Exception ex) { return new { error = "StudioReturn: " + ex.Message, next = "Call StudioStatus to see where she is." }; }
    }

    [McpServerTool]
    [Description("Where the studio trip stands: the player's cell, the studio lights and preset, the marker of the starting spot. If a " +
        "StudioGo or StudioReturn reply said 'moving', this finishes it once she has arrived (face rebuild, lights, marker clean-up); " +
        "a trip that never left is cleared. With no trip on, lists the run-time markers SkyLink placed (showAllMarkers for every " +
        "XMarker placed at run time, including other mods'); deleteMarker removes one of SkyLink's after asking the player " +
        "(another mod's needs force=true; one marker that follows the player belongs to another mod).")]
    public async Task<object> StudioStatus(string? deleteMarker = null, bool showAllMarkers = false, bool force = false)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(deleteMarker))
            {
                // BUG-075: a marker SkyLink didn't record may be another mod's (one followed the player around).
                if (MarkerRegistry.Get(deleteMarker) == null && !force)
                    return new { error = $"{deleteMarker} wasn't placed by SkyLink (or was placed before 1.3): it may belong to another mod. " +
                                         "Delete it only if the player is sure, with force=true." };
                // BUG-074: markers left by trips whose replies were lost; delete one the player agreed to.
                var r = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "cleanup", ["markerId"] = deleteMarker.Trim() });
                MarkerRegistry.Remove(deleteMarker);
                return DeserializeResponse(r);
            }
            var state = LoadState();
            var status = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "status" });
            var cell = status?["cell"]?.GetValue<string>() ?? "";
            var studioCell = state["studioCell"]?.GetValue<string>() ?? "";
            var pending = state["pending"]?.GetValue<string>();
            // BUG-074: a trip whose move never started (refused, or written by an older build) said "still moving" forever:
            // no marker, she is still in the cell she started in, and it is over a minute old.
            string? cleared = null;
            if (pending == "arrive" && state["markerId"] == null && status?["markerId"] == null &&
                cell.Equals(state["markedCell"]?.GetValue<string>() ?? "", StringComparison.OrdinalIgnoreCase) &&
                DateTime.TryParse(state["at"]?.GetValue<string>(), out var at) && DateTime.Now - at > TimeSpan.FromMinutes(1))
            {
                SaveState(new JsonObject());
                state = new JsonObject();
                pending = null;
                cleared = "A studio trip on record never left this cell (no marker, still where it started); it was cleared.";
            }
            JsonNode? strayMarkers = null;
            if (pending == null)
            {
                try
                {
                    strayMarkers = (await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "find" }))?.DeepClone();
                    string? backMarker = null;
                    try
                    {
                        var backFile = Path.Combine(SkyLinkSettings.Folder, "teleport_back.json");
                        if (File.Exists(backFile)) backMarker = JsonNode.Parse(File.ReadAllText(backFile))?["markerId"]?.ToString();
                    }
                    catch (Exception) { }
                    var listed = strayMarkers?["markers"] as JsonArray ?? new JsonArray();
                    foreach (var m in listed.OfType<JsonObject>())
                    {
                        var id = m["markerId"]?.ToString();
                        var mine = MarkerRegistry.Get(id);
                        m["placedBy"] = mine != null ? "SkyLink" : "unknown (another mod, or SkyLink before 1.3)";
                        if (mine != null) m["purpose"] = mine["purpose"]?.DeepClone();
                        if (id != null && id.Equals(backMarker, StringComparison.OrdinalIgnoreCase)) m["inUse"] = "Teleport('back') returns here: keep";
                    }
                    // Only SkyLink's by default; the rest are counted.
                    if (!showAllMarkers && strayMarkers is JsonObject sm)
                    {
                        var mineOnly = new JsonArray(listed.OfType<JsonObject>().Where(m => m["placedBy"]?.ToString() == "SkyLink" || m["inUse"] != null)
                            .Select(m => (JsonNode?)m.DeepClone()).ToArray());
                        sm["otherMarkers"] = listed.Count - mineOnly.Count;
                        sm["markers"] = mineOnly;
                    }
                }
                catch (Exception) { }
            }
            var done = pending switch
            {
                "arrive" => cell.Equals(studioCell, StringComparison.OrdinalIgnoreCase),
                "return" => cell.Length > 0 && !cell.Equals(studioCell, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            JsonObject? finished = done ? await Finish(state) : null;
            return DeserializeResponse(new JsonObject
            {
                ["cell"] = cell,
                ["lights"] = status?["lights"]?.DeepClone(),
                ["preset"] = status?["preset"]?.DeepClone(),
                ["trip"] = LoadState().DeepClone(),
                ["pending"] = pending == null ? null : (done ? pending + " finished" : pending + ": still moving, call again shortly"),
                ["finished"] = finished,
                ["cleared"] = cleared,
                ["strayMarkers"] = strayMarkers,
                ["strayNote"] = strayMarkers == null ? null
                    : "Run-time XMarkers in the world. placedBy says which SkyLink placed (recorded from 1.3); an unknown one may belong to " +
                      "another mod, so leave it unless the player knows it. With the player's agreement, StudioStatus(deleteMarker: <id>) " +
                      "deletes one. Keep the one marked inUse.",
            });
        }
        catch (Exception ex) { return new { error = "StudioStatus: " + ex.Message }; }
    }

    // A timeout or dropped connection during the load: the move may have started. Anything else is the plugin's answer.
    private static bool LostAcrossLoad(Exception ex) =>
        ex.Message.Contains("Timed out", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Lost connection", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Empty response", StringComparison.OrdinalIgnoreCase) ||
        ex is IOException || ex is TimeoutException;

    // Finishes an arrival (face rebuild, lights) or a return (face rebuild, marker deleted, trip cleared).
    private async Task<JsonObject> Finish(JsonObject state)
    {
        await Task.Delay(1500);  // let her 3D finish loading
        var result = new JsonObject();
        var face = await RepairFaceAfterMove();
        result["face"] = face == null ? null : JsonSerializer.SerializeToNode(face);
        if (state["faceSnapshot"]?.GetValue<string>() is { } snapshot)
        {
            try { result["makeup"] = await new RaceMenuTools(_pipe).RestoreTintsIfChanged(snapshot); }
            catch (Exception ex) { result["makeup"] = "Makeup not checked: " + ex.Message + ". DiffFacePresets against the last preset."; }
        }
        var pending = state["pending"]?.GetValue<string>();
        if (pending == "arrive")
        {
            var preset = state["preset"]?.GetValue<string>() ?? "neutral";
            if (preset != "off")
            {
                try
                {
                    // BUG-043: copy the reply: a node that belongs to the pipe response can't be put in another object.
                    result["lights"] = (await _pipe.SendRequestAsync("studio", new JsonObject
                    {
                        ["mode"] = "lights", ["preset"] = preset, ["intensity"] = state["intensity"]?.GetValue<double>() ?? 1.0,
                    }))?.DeepClone();
                }
                catch (Exception ex) { result["lights"] = "Lights not turned on: " + ex.Message + ". Call StudioLights."; }
            }
            state["pending"] = null;
            if (state["markerId"] == null)
            {
                try
                {
                    state["markerId"] = (await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "status" }))?["markerId"]?.DeepClone();
                    MarkerRegistry.Add(state["markerId"]?.ToString(), "studio trip start");
                }
                catch (Exception) { }
            }
            SaveState(state);
            result["arrived"] = state["studioCell"]?.DeepClone();
            result["markerId"] = state["markerId"]?.DeepClone();
            result["note"] = state["markerId"] == null
                ? "In the studio, but the starting marker couldn't be read back: StudioReturn will list candidates."
                : "In the studio. Use CaptureFace/MeasureFace as usual, then StudioReturn.";
        }
        else if (pending == "return")
        {
            try
            {
                result["cleanup"] = (await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "cleanup", ["markerId"] = state["markerId"]?.ToString() }))?.DeepClone();
                MarkerRegistry.Remove(state["markerId"]?.ToString());
                SaveState(new JsonObject());
                result["returned"] = true;
                result["to"] = state["markedCell"]?.DeepClone();
                result["note"] = "Back where she started.";
            }
            catch (Exception ex) { result["cleanup"] = "Marker not deleted yet: " + ex.Message + ". Call StudioStatus again."; }
        }
        return result;
    }

    // After a cell change the face came up flat (no lip colour or blush) until RepairFace rebuilt its tint (BUG-038).
    private async Task<object?> RepairFaceAfterMove()
    {
        try { return await new RaceMenuTools(_pipe).RepairFace(); }
        catch (Exception ex) { return "Face tint not rebuilt: " + ex.Message + ". Call RepairFace."; }
    }

    private async Task<bool> WaitForCell(Func<string, bool> done, int seconds)
    {
        for (var waited = 0; waited < seconds * 1000; waited += 750)
        {
            await Task.Delay(750);
            try
            {
                var status = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "status" });
                var cell = status?["cell"]?.GetValue<string>();
                if (cell != null && done(cell)) return true;
            }
            catch (Exception) { /* loading screen: the game thread may not answer yet */ }
        }
        return false;
    }

    private static JsonObject LoadState()
    {
        try
        {
            if (File.Exists(StateFile)) return JsonNode.Parse(File.ReadAllText(StateFile)) as JsonObject ?? new JsonObject();
        }
        catch (Exception) { }
        return new JsonObject();
    }

    private static void SaveState(JsonObject state)
    {
        Directory.CreateDirectory(SkyLinkSettings.Folder);
        File.WriteAllText(StateFile, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
