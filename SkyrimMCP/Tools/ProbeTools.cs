using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkyrimMCP.Face;

namespace SkyrimMCP.Tools;

/// <summary>
/// ProbeSlider (FEAT-248): what one face slider does on this head, by measurement: set it, measure, put it back, and
/// report which proportions moved. Replaces guessing slider directions (three were guessed wrong in one day).
/// </summary>
[McpServerToolType]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class ProbeTools : ToolBase
{
    public ProbeTools(IPipeClient pipe) : base(pipe) { }

    // The face as last measured, reused while no face edit has happened since (a probe puts the slider back, so the
    // face after a probe is the face before it).
    private static JsonObject? _baseline;
    private static int _baselineEdits = -1;
    private static int _baselineSamples;

    [McpServerTool]
    [Description("Find out what a face slider does on this head: sets slider to value, measures the face (MeasureFace, live), puts " +
        "the slider back, and reports which proportions and lid measures moved, by how much and which way. Records the result " +
        "with SliderNotes ('inert' when nothing moved beyond noise). Use it before committing to a slider: one call per slider, " +
        "about 20-40 seconds. Measure under the studio's neutral light (StudioGo or StudioLights) so lighting can't fake a change. " +
        "The face before the probe is measured once and reused for later probes while nothing else has changed the face. noise: " +
        "changes under this many percent count as no change (default 2).")]
    public async Task<CallToolResult> ProbeSlider(string slider, double value, int samples = 2, double noise = 2.0)
    {
        try
        {
            samples = Math.Clamp(samples, 1, 3);
            var face = new RaceMenuTools(_pipe);
            var measure = new FaceMeasureTools(_pipe);

            JsonObject before;
            var reused = _baseline != null && _baselineEdits == FaceState.FaceEdits && _baselineSamples >= samples;
            if (reused) before = (JsonObject)_baseline!.DeepClone();
            else
            {
                var (m, error) = await Measure(measure, samples);
                if (m == null) return Error("Measuring the face before the probe failed: " + error);
                before = m;
            }

            var set = JsonSerializer.SerializeToNode(await face.SetFaceSliders(new Dictionary<string, double> { [slider] = value }));
            if (set?["applied"]?.GetValue<bool>() != true)
                return Error("Setting the slider failed: " + (set?["error"]?.ToString() ?? set?.ToJsonString()));
            var change = (set["changes"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var previous = change?["previous"]?.GetValue<double>() ?? 0;
            var unknown = set["notInSliderFiles"] is JsonArray u && u.Count > 0;

            var (after, afterError) = await Measure(measure, samples);

            // Always put the slider back, even when the measurement failed.
            var restore = JsonSerializer.SerializeToNode(await face.SetFaceSliders(new Dictionary<string, double> { [slider] = previous }));
            var restored = restore?["applied"]?.GetValue<bool>() == true;
            if (restored)
            {
                _baseline = (JsonObject)before.DeepClone();
                _baselineEdits = FaceState.FaceEdits;
                _baselineSamples = samples;
            }
            if (after == null)
                return Error($"Measuring with the slider set failed: {afterError}. Slider put back: {restored}.");

            var moved = new JsonArray();
            foreach (var group in new[] { "ratios", "eyes" })
            {
                if (before[group] is not JsonObject b) continue;
                foreach (var (key, bv) in b)
                {
                    var x = bv?.GetValue<double>();
                    var y = after[group]?[key]?.GetValue<double>();
                    if (x == null || y == null) continue;
                    if (key.EndsWith("_deg"))
                    {
                        var d = y.Value - x.Value;
                        if (Math.Abs(d) >= 1.0) moved.Add(new JsonObject { ["measure"] = key, ["before"] = x, ["after"] = y, ["change"] = $"{d:+0.0;-0.0} deg" });
                    }
                    else if (Math.Abs(x.Value) > 1e-9)
                    {
                        var pct = (y.Value - x.Value) / Math.Abs(x.Value) * 100;
                        if (Math.Abs(pct) >= noise)
                            moved.Add(new JsonObject { ["measure"] = key, ["before"] = x, ["after"] = y, ["change"] = $"{pct:+0.0;-0.0}%", ["size"] = Math.Abs(pct) });
                    }
                }
            }
            var sorted = new JsonArray(moved.OfType<JsonObject>()
                .OrderByDescending(m => m["size"]?.GetValue<double>() ?? 0)
                .Select(m => { m.Remove("size"); return (JsonNode?)m.DeepClone(); }).ToArray());

            var summary = sorted.Count == 0
                ? $"inert (probe {previous:0.##} -> {value:0.##}: nothing moved over {noise}%)"
                : $"probe {previous:0.##} -> {value:0.##}: " + string.Join(", ", sorted.Take(4).OfType<JsonObject>()
                    .Select(m => $"{m["measure"]} {m["change"]}"));
            try { await face.SliderNotes(new Dictionary<string, string> { [slider] = summary }); }
            catch (Exception) { /* the note is a convenience */ }

            return Json(new JsonObject
            {
                ["slider"] = slider,
                ["probed"] = $"{previous:0.###} -> {value:0.###}, then back to {previous:0.###}",
                ["putBack"] = restored,
                ["unknownName"] = unknown ? "No slider file or saved preset knows this name: if nothing moved, check the spelling." : null,
                ["moved"] = sorted,
                ["note"] = summary,
                ["baselineReused"] = reused,
                ["studio"] = after["studio"]?.DeepClone(),
                ["warnings"] = Warnings(before, after),
            });
        }
        catch (Exception ex) { return Error("ProbeSlider: " + ex.Message); }
    }

    private static async Task<(JsonObject? Measurement, string? Error)> Measure(FaceMeasureTools measure, int samples)
    {
        var result = await measure.MeasureFace(image: "live", reference: null, samples: samples);
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        JsonNode? node = null;
        try { node = text == null ? null : JsonNode.Parse(text); } catch (JsonException) { }
        if (result.IsError == true || node?["measurement"] is not JsonObject m)
            return (null, node?["error"]?.ToString() ?? text ?? "no reply");
        var copy = (JsonObject)m.DeepClone();
        copy["studio"] = node["studio"]?.DeepClone();
        return (copy, null);
    }

    private static JsonArray? Warnings(JsonObject before, JsonObject after)
    {
        var list = new JsonArray();
        foreach (var w in after["colour"]?["lighting"]?["warnings"] as JsonArray ?? new JsonArray()) list.Add(w?.DeepClone());
        foreach (var f in after["pose"]?["flags"] as JsonArray ?? new JsonArray()) list.Add(f?.DeepClone());
        if (after["studio"]?["preset"] == null) list.Add("no studio light: a lighting change between the two measurements can look like a slider effect");
        return list.Count > 0 ? list : null;
    }

    private static CallToolResult Json(JsonObject body) => new() { Content = [new TextContentBlock { Text = body.ToJsonString() }] };

    private static CallToolResult Error(string message) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = message }) }],
        IsError = true,
    };
}
