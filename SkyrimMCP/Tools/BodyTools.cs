using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkyrimMCP.Face;

namespace SkyrimMCP.Tools;

/// <summary>Body measurement (FEAT-255, experimental): body proportions of the player or of a picture, and a body reference.</summary>
[McpServerToolType]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class BodyTools : ToolBase
{
    public BodyTools(IPipeClient pipe) : base(pipe) { }

    private static string ReferenceFile => Path.Combine(SkyLinkSettings.Folder, "body_references.json");

    [McpServerTool]
    [Description("Measure body proportions (experimental): image 'live' takes a full-body capture of the player (the face camera " +
        "pulled back: distance default 200, heightOffset -60 aims at the hips; neutral pose, no image returned), or a full path " +
        "to a picture of a whole standing person (crop it to the person if the picture is wide or busy). Use it in the studio: outdoors, " +
        "low light or a follower in frame gave nonsense, and below a confidence floor it now refuses. Returns shoulder width, " +
        "hip to shoulder width, torso, legs, arms and head-and-neck against height, and silhouette widths at chest, waist and hips " +
        "against the shoulders (unproven: hair, clothes and arms count; not compared), with confidence (lowest visibility of shoulders, hips and ankles: under 0.5, the feet or hips " +
        "weren't seen and the numbers are guesses). With reference (SetBodyReference), the gaps. Body morph tools (SetBodyMorph) " +
        "change these; compare captures in the same pose.")]
    public async Task<CallToolResult> MeasureBody(string image = "live", string? reference = null, double distance = 200, double heightOffset = -60)
    {
        try
        {
            string path;
            if (image.Trim().Equals("live", StringComparison.OrdinalIgnoreCase))
            {
                var shot = await new FaceCaptureTools(_pipe).CaptureFace(view: "front", distance: distance, heightOffset: heightOffset, showImage: false);
                string? file = null;
                foreach (var block in shot.Content.OfType<TextContentBlock>())
                {
                    try { file ??= JsonNode.Parse(block.Text)?["preview"]?["file"]?.GetValue<string>(); } catch (JsonException) { }
                }
                if (shot.IsError == true || file == null) return Error("Capture failed: " + string.Join(" ", shot.Content.OfType<TextContentBlock>().Select(b => b.Text)));
                path = file;
            }
            else path = image.Trim().Trim('"');
            if (!File.Exists(path)) return Error("No file at " + path);
            var m = await Task.Run(() => BodyMeasurer.Shared.Measure(path));
            if (m == null) return Error("No body found in " + path + ". The whole standing person should be in the picture.");
            var measurement = JsonSerializer.SerializeToNode(m) as JsonObject;
            // BUG-056: below this the numbers are guesses (outdoors at dusk with a follower beside her: shoulders 1.7 of
            // height, a negative head height); refuse rather than return them as measurements.
            var keyVisibility = (measurement?["confidence"]?["lowest_key_visibility"] as JsonValue)?.GetValue<double>() ?? 0;
            var presence = (measurement?["confidence"]?["presence"] as JsonValue)?.GetValue<double>() ?? 0;
            if (keyVisibility < 0.5 || presence < 0.6)
                return ErrorJson(new JsonObject
                {
                    ["error"] = $"Body not measured reliably (key visibility {keyVisibility:0.00}, presence {presence:0.00}): measure in the studio " +
                                "(StudioGo), with nobody else in frame and the whole body in the picture.",
                    ["image"] = path,
                    ["unreliable"] = measurement,
                });
            JsonObject? comparison = null;
            if (!string.IsNullOrWhiteSpace(reference) && LoadRefs()[reference.Trim()]?["measurement"] is JsonObject refM)
            {
                var gaps = new JsonArray();
                foreach (var (key, rv) in refM["ratios"] as JsonObject ?? new JsonObject())
                {
                    var a = (rv as JsonValue)?.GetValue<double>();
                    var b = (measurement?["ratios"]?[key] as JsonValue)?.GetValue<double>();
                    if (a == null || b == null || a.Value <= 0 || b.Value <= 0) continue;
                    var pct = (b.Value - a.Value) / Math.Abs(a.Value) * 100;
                    gaps.Add(new JsonObject { ["measure"] = key, ["reference"] = a, ["now"] = b, ["gap"] = $"{pct:+0;-0}%", ["ok"] = Math.Abs(pct) <= 4 });
                }
                comparison = new JsonObject { ["reference"] = reference.Trim(), ["gaps"] = gaps };
            }
            return Json(new JsonObject { ["image"] = path, ["measurement"] = measurement, ["comparison"] = comparison });
        }
        catch (Exception ex) { return Error("MeasureBody: " + ex.Message); }
    }

    [McpServerTool]
    [Description("Register a picture of a whole standing person as the body to aim for (experimental), so MeasureBody(reference) " +
        "reports the gaps. image: a full path ('live' registers the player's current body).")]
    public async Task<CallToolResult> SetBodyReference(string image, string name = "default")
    {
        var measured = await MeasureBody(image);
        var text = measured.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        if (measured.IsError == true || text == null) return measured;
        var node = JsonNode.Parse(text);
        var refs = LoadRefs();
        refs[name.Trim()] = new JsonObject
        {
            ["image"] = node?["image"]?.DeepClone(),
            ["measuredAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["measurement"] = node?["measurement"]?.DeepClone(),
        };
        Directory.CreateDirectory(SkyLinkSettings.Folder);
        await File.WriteAllTextAsync(ReferenceFile, refs.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return Json(new JsonObject { ["registered"] = name.Trim(), ["measurement"] = node?["measurement"]?.DeepClone() });
    }

    private static JsonObject LoadRefs()
    {
        try { if (File.Exists(ReferenceFile)) return JsonNode.Parse(File.ReadAllText(ReferenceFile)) as JsonObject ?? new JsonObject(); }
        catch (Exception) { }
        return new JsonObject();
    }

    private static CallToolResult ErrorJson(JsonObject body) => new() { Content = [new TextContentBlock { Text = body.ToJsonString() }], IsError = true };

    private static CallToolResult Json(JsonObject body) => new() { Content = [new TextContentBlock { Text = body.ToJsonString() }] };

    private static CallToolResult Error(string message) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = message }) }],
        IsError = true,
    };
}
