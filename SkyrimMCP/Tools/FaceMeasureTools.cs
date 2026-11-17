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

    // Colour differences treated as matched (FEAT-242: ratios and log2 ratios of linear RGB; degrees for the hue).
    private static readonly Dictionary<string, double> ColourTolerance = new()
    {
        ["skin_hue_deg (lighting-dependent)"] = 10,
        ["brow_lightness (brow / skin luminance)"] = 0.08,
        ["lip_redness (log2 R/G vs skin)"] = 0.12,
        ["lip_lightness (lip / skin luminance)"] = 0.08,
        ["blush (log2 R/G cheek vs skin)"] = 0.1,
        ["iris_lightness (iris / skin luminance)"] = 0.05,
        ["skin_warmth (log2 R/B vs eye white)"] = 0.1,
        ["skin_pink (log2 R/G vs eye white)"] = 0.08,
        ["iris_warmth (log2 R/B vs eye white)"] = 0.15,
        ["iris_green (log2 G/(R+B)/2 vs eye white)"] = 0.1,
    };

    [McpServerTool]
    [Description("Measure a face and, if a reference is registered (SetFaceReference), compare it. image: 'live' (default) takes " +
        "CaptureFace shots of the player without returning images (view, hideHair as CaptureFace; samples = how many captures, " +
        "the median is used; expression/strength/mouthOpen as CaptureFace; a neutral front capture flagged as turned, smiling or open-mouthed is retaken, up to twice; expression and strength as CaptureFace, to match a smiling reference), or a full path to any picture with a face. Returns: 13 proportions (eye width/opening/spacing/tilt, " +
        "brow height, nose, mouth, lips, jaw, chin, face shape, iris size), lid gaps (upper_lid_gap / iris_radius under 1 means " +
        "the lid covers the top of the iris; over 1, white shows above it: a stare; the lower-lid measures are not reliable on game " +
        "renders: the landmark model doesn't follow the real lower lid edge, so judge the lower lid by eye), head pose (yaw, pitch, roll) and " +
        "expression flags (smile, open mouth: those skew mouth and eye numbers), colours of skin, cheeks, lips, brows and iris " +
        "(hex, and light-independent ratios between parts of the face: brow and lip lightness and iris lightness against the skin, " +
        "lip redness and blush as log2 red/green against the skin, and, when the eye is big enough in the picture, skin and iris " +
        "colour against the white of the eye), lighting warnings (one-sided light, clipped highlights, haze) and a colour-cast " +
        "warning. With a reference: each measurement's gap, 'ok' when within tolerance " +
        "(default 3% for proportions), worst first. Measure the reference with " +
        "SetFaceReference too: numbers from another tool (e.g. MediaPipe in a notebook) differ by a few percent.")]
    public async Task<CallToolResult> MeasureFace(string image = "live", string? reference = "default", int samples = 1,
        double tolerance = 3, string view = "front", bool hideHair = false, string expression = "neutral", double strength = 0.7, double mouthOpen = 0)
    {
        if (!OperatingSystem.IsWindows()) return Error("Face measurement needs Windows.");
        samples = Math.Clamp(samples, 1, 5);

        var measured = new List<JsonObject>();
        var files = new List<string>();
        var rejected = new JsonArray();
        if (image.Trim().Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            // FEAT-243: a neutral capture that came back flagged (head turned, smile, open mouth: caught mid-animation)
            // is retaken, up to two extra captures in all.
            var neutral = expression.Trim().Equals("neutral", StringComparison.OrdinalIgnoreCase) && mouthOpen <= 0;
            var retakes = 0;
            for (var i = 0; i < samples; i++)
            {
                var (file, error) = await CaptureLive(view, hideHair, expression, strength, mouthOpen);
                if (error != null) return Error(error);
                var m = await MeasureFile(file!);
                if (m == null) return Error($"No face found in the capture {file}: check it with CaptureFace.");
                var flagged = (m["pose"]?["flags"] as JsonArray)?.Count > 0;
                if (neutral && flagged && retakes < 2 && view.Trim().Equals("front", StringComparison.OrdinalIgnoreCase))
                {
                    retakes++;
                    rejected.Add(new JsonObject { ["image"] = file, ["flags"] = m["pose"]?["flags"]?.DeepClone() });
                    i--;
                    continue;
                }
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
            ["studio"] = _lastStudio?.DeepClone(),
            ["retaken"] = rejected.Count > 0 ? rejected : null,
            ["comparison"] = comparison,
            ["reference"] = referenceNote,
            ["measurement"] = measurement,
        });
    }

    [McpServerTool]
    [Description("Register a picture as the face to aim for: measures it once and keeps the result (Documents\\My Games\\Skyrim Special " +
        "Edition\\SkyLinkAI\\face_references.json), so every MeasureFace compares against it. image: a full path, or 'live' for the " +
        "player's current face (a before snapshot). name: 'default' unless you keep several. Read the returned pose flags: a turned " +
        "head, a smile or an open mouth make some measurements unreliable as a target. morePictures: more pictures of the same " +
        "person; the reference is then the median of each measure over the pictures without pose flags (all of them if every " +
        "one is flagged), with colour from the first whose colour is usable.")]
    public async Task<CallToolResult> SetFaceReference(string image, string name = "default", string? note = null, string[]? morePictures = null)
    {
        if (!OperatingSystem.IsWindows()) return Error("Face measurement needs Windows.");
        var paths = new List<string>();
        if (image.Trim().Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            var (file, error) = await CaptureLive("front", false, "neutral", 0, 0);
            if (error != null) return Error(error);
            paths.Add(file!);
        }
        else paths.Add(image.Trim().Trim('"'));
        paths.AddRange((morePictures ?? []).Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0));
        foreach (var path in paths)
            if (!File.Exists(path)) return Error($"No file at {path}: pass full paths.");

        // FEAT-246: several pictures of one person make one reference: the median of each measure over the pictures
        // with no pose flags (a turned head, a smile), or over all of them when every one is flagged.
        var measured = new List<(string Path, JsonObject M, bool Flagged)>();
        var notFound = new JsonArray();
        foreach (var path in paths)
        {
            var m = await MeasureFile(path);
            if (m == null) { notFound.Add(path); continue; }
            measured.Add((path, m, (m["pose"]?["flags"] as JsonArray)?.Count > 0));
        }
        if (measured.Count == 0)
            return Error("No face found in any picture. A face under about 100 pixels wide may not be found: crop and enlarge it first.");
        var clean = measured.Where(x => !x.Flagged).ToList();
        var used = clean.Count > 0 ? clean : measured;
        var measurement = used.Count == 1 ? (JsonObject)used[0].M.DeepClone() : Median(used.Select(x => x.M).ToList());
        // BUG-045: when every picture has some flag, choose per measure: leave out the pictures whose flag skews that
        // measure (a turned head skews widths, a tilt heights, a smile the mouth, lips, chin and eyes), so a turned
        // photo doesn't pull the width ratios of a frontal one.
        var perMeasure = new JsonObject();
        if (clean.Count == 0 && measured.Count > 1)
        {
            foreach (var group in new[] { "ratios", "eyes" })
            {
                if (measurement[group] is not JsonObject target) continue;
                foreach (var key in target.Select(kv => kv.Key).ToList())
                {
                    var eligible = measured.Where(x => !Skews(x.M, key)).ToList();
                    if (eligible.Count == 0 || eligible.Count == measured.Count) continue;
                    var values = eligible.Select(x => x.M[group]?[key]?.GetValue<double>()).OfType<double>().OrderBy(v => v).ToArray();
                    if (values.Length == 0) continue;
                    var mid = values.Length / 2;
                    target[key] = Math.Round(values.Length % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2, 3);
                    perMeasure[key] = new JsonArray(eligible.Select(x => (JsonNode?)JsonValue.Create(Path.GetFileName(x.Path))).ToArray());
                }
            }
        }
        // Colour only from pictures whose colour is usable (not sepia or black and white).
        var colourSource = used.FirstOrDefault(x => x.M["colour"]?["usable"]?.GetValue<bool>() != false).M;
        if (colourSource != null && used.Count > 1) measurement["colour"] = colourSource["colour"]?.DeepClone();

        var refs = LoadReferences();
        refs[name.Trim()] = new JsonObject
        {
            ["image"] = used[0].Path,
            ["pictures"] = new JsonArray(used.Select(x => (JsonNode?)JsonValue.Create(x.Path)).ToArray()),
            ["note"] = note,
            ["measuredAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["measurement"] = measurement,
        };
        Directory.CreateDirectory(SkyLinkSettings.Folder);
        await File.WriteAllTextAsync(ReferenceFile, refs.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return Json(new JsonObject
        {
            ["registered"] = name.Trim(),
            ["pictures"] = new JsonArray(measured.Select(x => (JsonNode?)new JsonObject
            {
                ["image"] = x.Path,
                ["used"] = used.Contains(x),
                ["flags"] = x.M["pose"]?["flags"]?.DeepClone(),
                ["colourUsable"] = x.M["colour"]?["usable"]?.DeepClone(),
            }).ToArray()),
            ["noFaceFound"] = notFound.Count > 0 ? notFound : null,
            ["perMeasure"] = perMeasure.Count > 0 ? perMeasure : null,
            ["colourFrom"] = colourSource == null ? "none usable" : used.Count > 1 ? used.First(x => x.M == colourSource).Path : null,
            ["flags"] = measurement["pose"]?["flags"]?.DeepClone(),
            ["colourCast"] = measurement["colour"]?["cast"]?.DeepClone(),
            ["measurement"] = measurement.DeepClone(),
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

    private JsonNode? _lastStudio;

    private async Task<(string? File, string? Error)> CaptureLive(string view, bool hideHair, string expression, double strength, double mouthOpen)
    {
        var shot = await new FaceCaptureTools(_pipe).CaptureFace(view: view, hideHair: hideHair, showImage: false,
            expression: expression, strength: strength, mouthOpen: mouthOpen);
        string? preview = null, full = null;
        foreach (var block in shot.Content.OfType<TextContentBlock>())
        {
            JsonNode? node;
            try { node = JsonNode.Parse(block.Text); } catch (JsonException) { continue; }
            if (node?["error"] is JsonNode error) return (null, "CaptureFace: " + error);
            preview ??= node?["preview"]?["file"]?.GetValue<string>();
            _lastStudio ??= node?["studio"]?.DeepClone();
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

    // Whether a picture's pose flags make one measure unreliable.
    private static bool Skews(JsonObject m, string key)
    {
        var flags = (m["pose"]?["flags"] as JsonArray ?? new JsonArray()).Select(f => f?.ToString() ?? "").ToList();
        bool Has(string word) => flags.Any(f => f.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (Has("turned") && (key.Contains("width") || key.Contains("spacing"))) return true;
        if (Has("tilted") && (key.Contains("height") || key.Contains("length") || key.Contains("opening") || key.Contains("lid"))) return true;
        if ((Has("smiling") || Has("mouth open")) &&
            (key.Contains("mouth") || key.Contains("lip") || key.Contains("chin") || key.Contains("opening") || key.Contains("lid"))) return true;
        return false;
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
        bool Usable(JsonObject m) => m["colour"]?["usable"]?.GetValue<bool>() != false;
        var colourUsable = Usable(reference) && Usable(now);
        if (colourUsable && reference["colour"]?["relative"] is JsonObject refColour)
        {
            foreach (var (key, refValue) in refColour)
            {
                var r = refValue?.GetValue<double>();
                var n = now["colour"]?["relative"]?[key]?.GetValue<double>();
                if (r == null || n == null) continue;
                var diff = n.Value - r.Value;
                if (key.Contains("hue_deg")) diff = (diff + 540) % 360 - 180;
                var tol = ColourTolerance.GetValueOrDefault(key, 0.1);
                var ok = Math.Abs(diff) <= tol;
                colourRows.Add((ok, Math.Abs(diff) / tol, new JsonObject
                {
                    ["measure"] = key, ["reference"] = r.Value, ["now"] = n.Value, ["diff"] = Math.Round(diff, 3), ["ok"] = ok,
                    ["direction"] = ok ? null : (diff > 0 ? "too high" : "too low"),
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

        foreach (var (label, m) in new[] { ("reference", reference), ("now", now) })
            foreach (var w in m["colour"]?["lighting"]?["warnings"] as JsonArray ?? new JsonArray())
                flags.Add($"{label}: {w}");
        var refL = reference["colour"]?["lighting"]?["skin_L"]?.GetValue<double>();
        var nowL = now["colour"]?["lighting"]?["skin_L"]?.GetValue<double>();
        if (refL != null && nowL != null && Math.Abs(nowL.Value - refL.Value) > 20)
            flags.Add($"lighting much {(nowL > refL ? "brighter" : "darker")} than the reference (skin lightness {nowL:0} vs {refL:0}): trust the relative colours over the hex values and the lighting-dependent skin hue");

        var shapeOk = rows.Count(r => r.Ok);
        var colourOk = colourRows.Count(r => r.Ok);
        return new JsonObject
        {
            ["reference"] = name,
            ["shape"] = $"{shapeOk}/{rows.Count} within tolerance",
            ["colour"] = colourUsable ? $"{colourOk}/{colourRows.Count} within tolerance"
                : $"not compared: the {(Usable(reference) ? "capture" : "reference")} is monochrome or sepia",
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
