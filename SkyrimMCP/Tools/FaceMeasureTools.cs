using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkyrimMCP.Face;

namespace SkyrimMCP.Tools;

/// <summary>
/// Face builder M4 (FEAT-230): measures a face in a capture or a reference picture (FaceMeasurer) and
/// compares it with a registered reference, so the AI can tell by number which features still differ.
/// </summary>
[McpServerToolType]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class FaceMeasureTools : ToolBase
{
    public FaceMeasureTools(IPipeClient pipe) : base(pipe) { }

    private static string ReferenceFile => Path.Combine(SkyLinkSettings.Folder, "face_references.json");

    // Colour differences (Lab units, degrees for hues) treated as matched.
    private static readonly Dictionary<string, double> ColourTolerance = new()
    {
        ["brow_darkness (skin L - brow L)"] = 4,
        ["lip_redness (lip a - skin a)"] = 3,
        ["lip_lightness (lip L - skin L)"] = 4,
        ["blush (cheek a - skin a)"] = 3,
        ["iris_lightness (iris L - skin L)"] = 5,
        ["iris_hue_deg"] = 15,
        ["iris_chroma"] = 4,
        ["skin_hue_deg"] = 10,
    };

    [McpServerTool]
    [Description("Measure a face and, if a reference is registered (SetFaceReference), compare it. image: 'live' (default) takes " +
        "CaptureFace shots of the player without returning images (view, hideHair as CaptureFace; samples = how many captures, " +
        "the median is used; expression and strength as CaptureFace, to match a smiling reference), or a full path to any picture with a face. Returns: 13 proportions (eye width/opening/spacing/tilt, " +
        "brow height, nose, mouth, lips, jaw, chin, face shape, iris size), lid gaps (upper_lid_gap / iris_radius under 1 means " +
        "the lid covers the top of the iris; over 1, white shows above it: a stare), head pose (yaw, pitch, roll) and " +
        "expression flags (smile, open mouth: those skew mouth and eye numbers), colours of skin, cheeks, lips, brows and iris " +
        "(hex, and relative to the skin so room lighting mostly cancels: brow darkness, lip redness, blush, iris lightness and " +
        "hue, skin hue), and a colour-cast warning. With a reference: each measurement's gap, 'ok' when within tolerance " +
        "(default 3% for proportions; about 3-5 Lab units for colours), worst first. Measure the reference with " +
        "SetFaceReference too: numbers from another tool (e.g. MediaPipe in a notebook) differ by a few percent.")]
    public async Task<CallToolResult> MeasureFace(string image = "live", string? reference = "default", int samples = 1,
        double tolerance = 3, string view = "front", bool hideHair = false, string expression = "neutral", double strength = 0.7)
    {
        if (!OperatingSystem.IsWindows()) return Error("Face measurement needs Windows.");
        samples = Math.Clamp(samples, 1, 5);

        var measured = new List<JsonObject>();
        var files = new List<string>();
        if (image.Trim().Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; i < samples; i++)
            {
                var (file, error) = await CaptureLive(view, hideHair, expression, strength);
                if (error != null) return Error(error);
                var m = await MeasureFile(file!);
                if (m == null) return Error($"No face found in the capture {file}: check it with CaptureFace.");
                measured.Add(m);
                files.Add(file!);
            }
        }
        else
        {
            var path = image.Trim().Trim('"');
            if (!File.Exists(path)) return Error($"No file at {path}: pass a full path, or 'live'.");
            var m = await MeasureFile(path);
            if (m == null) return Error($"No face found in {path}. A face under about 100 pixels wide may not be found: crop and enlarge it first.");
            measured.Add(m);
            files.Add(path);
        }

        var measurement = measured.Count == 1 ? measured[0] : Median(measured);
        JsonObject? comparison = null;
        string? referenceNote = null;
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var stored = LoadReferences()[reference.Trim()] as JsonObject;
            if (stored?["measurement"] is JsonObject refMeasurement) comparison = Compare(refMeasurement, measurement, tolerance, reference.Trim());
            else if (reference.Trim() != "default") referenceNote = $"No reference named '{reference}': ListFaceReferences shows them.";
            else referenceNote = "No reference registered: SetFaceReference(image) registers one, then every MeasureFace compares against it.";
        }

        return Json(new JsonObject
        {
            ["images"] = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["samples"] = measured.Count,
            ["comparison"] = comparison,
            ["reference"] = referenceNote,
            ["measurement"] = measurement,
        });
    }

    [McpServerTool]
    [Description("Register a picture as the face to aim for: measures it once and keeps the result (Documents\\My Games\\Skyrim Special " +
        "Edition\\SkyLinkAI\\face_references.json), so every MeasureFace compares against it. image: a full path, or 'live' for the " +
        "player's current face (a before snapshot). name: 'default' unless you keep several. Read the returned pose flags: a turned " +
        "head, a smile or an open mouth make some measurements unreliable as a target.")]
    public async Task<CallToolResult> SetFaceReference(string image, string name = "default", string? note = null)
    {
        if (!OperatingSystem.IsWindows()) return Error("Face measurement needs Windows.");
        string path;
        if (image.Trim().Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            var (file, error) = await CaptureLive("front", false, "neutral", 0);
            if (error != null) return Error(error);
            path = file!;
        }
        else
        {
            path = image.Trim().Trim('"');
            if (!File.Exists(path)) return Error($"No file at {path}: pass a full path.");
        }
        var m = await MeasureFile(path);
        if (m == null) return Error($"No face found in {path}. A face under about 100 pixels wide may not be found: crop and enlarge it first.");

        var refs = LoadReferences();
        refs[name.Trim()] = new JsonObject
        {
            ["image"] = path,
            ["note"] = note,
            ["measuredAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["measurement"] = m,
        };
        Directory.CreateDirectory(SkyLinkSettings.Folder);
        await File.WriteAllTextAsync(ReferenceFile, refs.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return Json(new JsonObject
        {
            ["registered"] = name.Trim(),
            ["image"] = path,
            ["flags"] = m["pose"]?["flags"]?.DeepClone(),
            ["colourCast"] = m["colour"]?["cast"]?.DeepClone(),
            ["measurement"] = m.DeepClone(),
        });
    }

    [McpServerTool]
    [Description("List the registered face references (SetFaceReference): name, picture, when measured, and flags.")]
    public Task<CallToolResult> ListFaceReferences()
    {
        var list = new JsonArray();
        foreach (var (name, value) in LoadReferences())
        {
            list.Add(new JsonObject
            {
                ["name"] = name,
                ["image"] = value?["image"]?.DeepClone(),
                ["note"] = value?["note"]?.DeepClone(),
                ["measuredAt"] = value?["measuredAt"]?.DeepClone(),
                ["flags"] = value?["measurement"]?["pose"]?["flags"]?.DeepClone(),
            });
        }
        return Task.FromResult(Json(new JsonObject { ["references"] = list, ["file"] = ReferenceFile }));
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<(string? File, string? Error)> CaptureLive(string view, bool hideHair, string expression, double strength)
    {
        var shot = await new FaceCaptureTools(_pipe).CaptureFace(view: view, hideHair: hideHair, showImage: false,
            expression: expression, strength: strength);
        string? preview = null, full = null;
        foreach (var block in shot.Content.OfType<TextContentBlock>())
        {
            JsonNode? node;
            try { node = JsonNode.Parse(block.Text); } catch (JsonException) { continue; }
            if (node?["error"] is JsonNode error) return (null, "CaptureFace: " + error);
            preview ??= node?["preview"]?["file"]?.GetValue<string>();
            full ??= node?["file"]?.GetValue<string>();
        }
        if (shot.IsError == true) return (null, "CaptureFace failed: " + string.Join(" ", shot.Content.OfType<TextContentBlock>().Select(b => b.Text)));
        var file = preview ?? full;
        return file == null ? (null, "CaptureFace returned no image file.") : (file, null);
    }

    private static async Task<JsonObject?> MeasureFile(string path)
    {
        var result = await Task.Run(() => FaceMeasurer.Shared.Measure(path));
        return result == null ? null : JsonSerializer.SerializeToNode(result) as JsonObject;
    }

    private static JsonObject LoadReferences()
    {
        try
        {
            if (File.Exists(ReferenceFile)) return JsonNode.Parse(File.ReadAllText(ReferenceFile)) as JsonObject ?? new JsonObject();
        }
        catch (Exception) { /* unreadable: start a new file */ }
        return new JsonObject();
    }

    /// <summary>Median of every number across the samples; everything else from the first.</summary>
    private static JsonObject Median(List<JsonObject> samples)
    {
        JsonNode? Merge(List<JsonNode?> nodes)
        {
            var first = nodes[0];
            if (first is JsonObject obj)
            {
                var result = new JsonObject();
                foreach (var (key, _) in obj)
                    result[key] = Merge(nodes.Select(n => (n as JsonObject)?[key]).ToList());
                return result;
            }
            if (first is JsonValue v && v.TryGetValue<double>(out _))
            {
                var values = nodes.OfType<JsonValue>().Select(n => n.GetValue<double>()).OrderBy(x => x).ToArray();
                var m = values.Length / 2;
                return Math.Round(values.Length % 2 == 1 ? values[m] : (values[m - 1] + values[m]) / 2, 3);
            }
            return first?.DeepClone();
        }
        return (JsonObject)Merge(samples.Cast<JsonNode?>().ToList())!;
    }

    private static JsonObject Compare(JsonObject reference, JsonObject now, double tolerance, string name)
    {
        var rows = new List<(bool Ok, double Size, JsonObject Row)>();
        foreach (var group in new[] { "ratios", "eyes" })
        {
            if (reference[group] is not JsonObject refGroup) continue;
            foreach (var (key, refValue) in refGroup)
            {
                var r = refValue?.GetValue<double>();
                var n = now[group]?[key]?.GetValue<double>();
                if (r == null || n == null) continue;
                bool ok;
                double size;
                string gap;
                if (key.EndsWith("_deg"))
                {
                    size = n.Value - r.Value;
                    ok = Math.Abs(size) <= 1.5;
                    gap = $"{size:+0.0;-0.0} deg";
                    size /= 1.5;
                }
                else
                {
                    var pct = (n.Value - r.Value) / Math.Abs(r.Value) * 100;
                    ok = Math.Abs(pct) <= tolerance;
                    gap = $"{pct:+0;-0}%";
                    size = pct / tolerance;
                }
                rows.Add((ok, Math.Abs(size), new JsonObject
                {
                    ["measure"] = key, ["reference"] = r.Value, ["now"] = n.Value, ["gap"] = gap, ["ok"] = ok,
                    ["direction"] = ok ? null : (n.Value > r.Value ? "too high" : "too low"),
                }));
            }
        }
        var colourRows = new List<(bool Ok, double Size, JsonObject Row)>();
        if (reference["colour"]?["relative"] is JsonObject refColour)
        {
            foreach (var (key, refValue) in refColour)
            {
                var r = refValue?.GetValue<double>();
                var n = now["colour"]?["relative"]?[key]?.GetValue<double>();
                if (r == null || n == null) continue;
                var diff = n.Value - r.Value;
                if (key.EndsWith("hue_deg")) diff = (diff + 540) % 360 - 180;
                var tol = ColourTolerance.GetValueOrDefault(key, 4);
                var ok = Math.Abs(diff) <= tol;
                colourRows.Add((ok, Math.Abs(diff) / tol, new JsonObject
                {
                    ["measure"] = key, ["reference"] = r.Value, ["now"] = n.Value, ["diff"] = Math.Round(diff, 1), ["ok"] = ok,
                }));
            }
        }

        var flags = new JsonArray();
        foreach (var (label, m) in new[] { ("reference", reference), ("now", now) })
            foreach (var f in m["pose"]?["flags"] as JsonArray ?? new JsonArray())
                flags.Add($"{label}: {f}");
        var refPose = reference["pose"];
        var nowPose = now["pose"];
        double? Angle(JsonNode? pose, string key) => pose?[key]?.GetValue<double>();
        var yawDiff = Angle(nowPose, "yaw_deg") - Angle(refPose, "yaw_deg");
        var pitchDiff = Angle(nowPose, "pitch_deg") - Angle(refPose, "pitch_deg");
        if (Math.Abs(yawDiff ?? 0) > 6 || Math.Abs(pitchDiff ?? 0) > 6)
            flags.Add($"head angle differs from the reference (yaw {yawDiff:+0;-0}, pitch {pitchDiff:+0;-0} degrees): capture at a matching angle (CaptureFace angle, heightOffset)");

        if (now["colour"]?["lighting"]?["warning"] is JsonNode lightWarning)
            flags.Add($"now: {lightWarning}");
        var refL = reference["colour"]?["lighting"]?["skin_L"]?.GetValue<double>();
        var nowL = now["colour"]?["lighting"]?["skin_L"]?.GetValue<double>();
        if (refL != null && nowL != null && Math.Abs(nowL.Value - refL.Value) > 20)
            flags.Add($"lighting much {(nowL > refL ? "brighter" : "darker")} than the reference (skin lightness {nowL:0} vs {refL:0}): trust the relative colours over the hex values");

        var shapeOk = rows.Count(r => r.Ok);
        var colourOk = colourRows.Count(r => r.Ok);
        return new JsonObject
        {
            ["reference"] = name,
            ["shape"] = $"{shapeOk}/{rows.Count} within tolerance",
            ["colour"] = $"{colourOk}/{colourRows.Count} within tolerance",
            ["flags"] = flags,
            ["shapeGaps"] = new JsonArray(rows.OrderBy(r => r.Ok).ThenByDescending(r => r.Size).Select(r => (JsonNode?)r.Row).ToArray()),
            ["colourGaps"] = new JsonArray(colourRows.OrderBy(r => r.Ok).ThenByDescending(r => r.Size).Select(r => (JsonNode?)r.Row).ToArray()),
            ["colours"] = new JsonObject
            {
                ["reference"] = reference["colour"]?["hex"]?.DeepClone(),
                ["now"] = now["colour"]?["hex"]?.DeepClone(),
            },
        };
    }

    private static CallToolResult Json(JsonObject body) => new()
    {
        Content = [new TextContentBlock { Text = body.ToJsonString() }],
    };

    private static CallToolResult Error(string message) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = message }) }],
        IsError = true,
    };
}
