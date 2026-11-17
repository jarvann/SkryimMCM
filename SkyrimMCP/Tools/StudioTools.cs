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
        "and MeasureFace report the preset in use.")]
    public async Task<object> StudioLights(string preset = "neutral", double intensity = 1.0)
    {
        var data = await _pipe.SendRequestAsync("studio", new JsonObject
        {
            ["mode"] = "lights", ["preset"] = preset.Trim().ToLowerInvariant(), ["intensity"] = intensity,
        });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Take the player to a studio: marks her exact spot and facing (a marker in the world), then moves her to an interior " +
        "cell by editor ID (default QASmoke, the developers' test hall: fixed indoor light, no sun or weather) and, once she has " +
        "arrived, rebuilds the face tint (a cell change can leave it flat) and turns on the studio lights (preset as StudioLights; " +
        "'off' for none). Followers may come along. Save the game " +
        "first. Refused in combat or while already in the studio. StudioReturn brings her back.")]
    public async Task<object> StudioGo(string cell = "QASmoke", string preset = "neutral", double intensity = 1.0)
    {
        if (LoadState()["markerId"] is JsonNode existing)
            return new { error = $"Already in the studio (marker {existing}): StudioReturn first." };
        var before = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "status" });
        // BUG-038: the move can start a loading screen before the reply comes back, and the reply then times out.
        // The marker is kept in the plugin, so it is read back from the status once she has arrived.
        JsonNode? go = null;
        string? goNote = null;
        try
        {
            go = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "go", ["cell"] = cell.Trim() });
            if (go?["error"] is JsonNode error) return new { error = error.ToString() };
        }
        catch (Exception ex) { goNote = "The move reply was lost across the loading screen (" + ex.Message + "); recovered from the status."; }

        var arrived = await WaitForCell(c => c.Equals(cell.Trim(), StringComparison.OrdinalIgnoreCase), 60);
        var markerId = go?["markerId"]?.GetValue<string>();
        if (markerId == null)
        {
            try { markerId = (await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "status" }))?["markerId"]?.GetValue<string>(); }
            catch (Exception) { /* reported below */ }
        }
        if (markerId == null)
            return new { error = "The marker of the starting spot couldn't be read back: don't save; load the save made before StudioGo to undo.", arrived };
        SaveState(new JsonObject
        {
            ["markerId"] = markerId,
            ["markedCell"] = go?["markedCell"]?.DeepClone() ?? before?["cell"]?.DeepClone(),
            ["markedPosition"] = go?["markedPosition"]?.DeepClone(),
            ["studioCell"] = cell.Trim(),
            ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        });

        JsonNode? lights = null;
        object? face = null;
        if (arrived)
        {
            await Task.Delay(1500);  // let her 3D finish loading
            face = await RepairFaceAfterMove();
            if (!preset.Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    lights = await _pipe.SendRequestAsync("studio", new JsonObject
                    {
                        ["mode"] = "lights", ["preset"] = preset.Trim().ToLowerInvariant(), ["intensity"] = intensity,
                    });
                }
                catch (Exception ex) { lights = JsonValue.Create("Lights not turned on: " + ex.Message + ". Call StudioLights."); }
            }
        }
        await NotifyInGame("Studio: StudioReturn brings you back");
        return DeserializeResponse(new JsonObject
        {
            ["from"] = before?["cell"]?.DeepClone(),
            ["markerId"] = markerId,
            ["arrived"] = arrived,
            ["lights"] = lights,
            ["face"] = face == null ? null : JsonSerializer.SerializeToNode(face),
            ["moveNote"] = goNote,
            ["note"] = arrived ? "In the studio. Use CaptureFace/MeasureFace as usual, then StudioReturn."
                               : "The move was started but she hadn't arrived after 45 s: check the game, then StudioReturn when ready.",
        });
    }

    [McpServerTool]
    [Description("Bring the player back from the studio to the exact spot and facing StudioGo marked, remove the studio lights, and " +
        "delete the marker once she is back.")]
    public async Task<object> StudioReturn()
    {
        var state = LoadState();
        var markerId = state["markerId"]?.GetValue<string>();
        if (markerId == null) return new { error = "No studio trip on record: nothing to return from." };
        var studioCell = state["studioCell"]?.GetValue<string>() ?? "";
        string? backNote = null;
        try
        {
            var back = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "return", ["markerId"] = markerId });
            if (back?["error"] is JsonNode error) return new { error = error.ToString(), markerId };
        }
        catch (Exception ex) { backNote = "The move reply was lost across the loading screen (" + ex.Message + ")."; }

        var left = await WaitForCell(c => !c.Equals(studioCell, StringComparison.OrdinalIgnoreCase), 60);
        JsonNode? cleanup = null;
        object? face = null;
        if (left)
        {
            await Task.Delay(1500);
            face = await RepairFaceAfterMove();
            try
            {
                cleanup = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "cleanup", ["markerId"] = markerId });
                SaveState(new JsonObject());
            }
            catch (Exception ex) { cleanup = JsonValue.Create("Marker not deleted yet: " + ex.Message + ". Call StudioReturn again."); }
        }
        return DeserializeResponse(new JsonObject
        {
            ["returned"] = left,
            ["to"] = state["markedCell"]?.DeepClone(),
            ["cleanup"] = cleanup,
            ["face"] = face == null ? null : JsonSerializer.SerializeToNode(face),
            ["moveNote"] = backNote,
            ["note"] = left ? "Back where she started." : "The move back was started but hasn't finished: call StudioReturn again once she has arrived.",
        });
    }

    // BUG-038: after a cell change the face came up flat (no lip colour or blush) until RepairFace rebuilt its tint.
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
