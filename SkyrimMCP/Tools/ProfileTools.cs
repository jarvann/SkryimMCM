using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkyrimMCP.Face;

namespace SkyrimMCP.Tools;

/// <summary>
/// Profile measures from the head mesh itself (FEAT-252): how far the nose, lips, chin and brow stand out, read from the
/// depth of the face vertices under each landmark in a front capture (the sculpt brush's vertex snapshot). A picture
/// can't show depth reliably; the mesh can, so slider and sculpt moves forward or back can be checked by number.
/// </summary>
[McpServerToolType]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class ProfileTools : ToolBase
{
    public ProfileTools(IPipeClient pipe) : base(pipe) { }

    [McpServerTool]
    [Description("Measure the player's face in depth, from the head mesh (game only; experimental): takes a front capture with the " +
        "vertex snapshot, finds landmarks, and reads how far each feature stands out toward the viewer, in eye widths: " +
        "nose_projection (nose tip in front of the eye corners), nose_bridge (bridge between the eyes in front of the corners), " +
        "upper_lip / lower_lip (lips in front of the mouth corners), chin_projection (chin relative to the lower lip; negative = " +
        "behind it), brow_ridge (brows in front of the eye corners), cheek_fullness (cheeks in front of the jaw sides), " +
        "forehead_slope (top of the forehead behind the brow). Use it before and after a slider or sculpt that moves things " +
        "forward or back: a front picture can't show that.")]
    public async Task<CallToolResult> MeasureProfile()
    {
        if (await SafetyBlock("camera", null) is string gateReason) return Blocked(gateReason);  // FEAT-260
        try
        {
            var capture = new FaceCaptureTools(_pipe);
            var shot = await capture.CaptureForSculpt(hideHair: true);
            if (shot.IsError == true) return Error("Capture failed: " + string.Join(" ", shot.Content.OfType<TextContentBlock>().Select(b => b.Text)));
            var face = (capture.Snapshot?["hosts"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(h => h["type"]?.GetValue<int>() == 1);
            if (face?["xyd"] is not JsonArray xyd) return Error("No face mesh in the vertex snapshot.");

            string? preview = null;
            int[]? crop = null;
            int sw = 0, sh = 0;
            foreach (var block in shot.Content.OfType<TextContentBlock>())
            {
                JsonNode? node;
                try { node = JsonNode.Parse(block.Text); } catch (JsonException) { continue; }
                if (node?["preview"] is JsonObject p)
                {
                    preview = p["file"]?.GetValue<string>();
                    crop = p["cropPixels"]?.GetValue<string>()?.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    var size = p["sourceSize"]?.GetValue<string>()?.Split('x');
                    if (size is { Length: 2 }) { sw = int.Parse(size[0], CultureInfo.InvariantCulture); sh = int.Parse(size[1], CultureInfo.InvariantCulture); }
                }
            }
            if (preview == null || crop is not { Length: 4 } || sw == 0) return Error("The capture didn't report its preview and crop.");
            var found = FaceMeasurer.Shared.FindLandmarks(preview);
            if (found == null) return Error("No face found in the capture.");
            var (lm, pw, ph) = found.Value;
            (double X, double Y) Px(int i) => (crop[0] + lm[i, 0] * crop[2] / pw, crop[1] + lm[i, 1] * crop[3] / ph);

            var verts = new List<(double X, double Y, double D)>();
            for (var v = 0; v + 2 < xyd.Count; v += 3)
            {
                var x = xyd[v]!.GetValue<double>();
                var y = xyd[v + 1]!.GetValue<double>();
                if (x < 0 || y < 0) continue;
                verts.Add((x * sw, y * sh, xyd[v + 2]!.GetValue<double>()));
            }
            var eyeWidthPx = (Dist(Px(33), Px(133)) + Dist(Px(362), Px(263))) / 2;
            // The camera-facing surface under a landmark: among the vertices within a small radius on screen, the nearest.
            double Depth(params int[] ids)
            {
                var ds = new List<double>();
                foreach (var id in ids)
                {
                    var p = Px(id);
                    // BUG-055: widen the search if no vertex lies right under the landmark (a sparse part of the mesh).
                    foreach (var reach in new[] { 0.08, 0.16, 0.3 })
                    {
                        var near = verts.Where(v => Dist((v.X, v.Y), p) < eyeWidthPx * reach).Select(v => v.D).ToList();
                        if (near.Count > 0) { ds.Add(near.Min()); break; }
                    }
                }
                return ds.Count > 0 ? ds.Average() : double.NaN;
            }
            // Eye width in world units, from the jacobian (screen pixels per mesh unit).
            var jac = (face["jacobian"] as JsonArray)!.OfType<JsonArray>()
                .Select(a => Math.Sqrt(Math.Pow(a[0]!.GetValue<double>() * sw, 2) + Math.Pow(a[1]!.GetValue<double>() * sh, 2)))
                .OrderByDescending(v => v).Take(2).Average();
            var eyeWidthUnits = eyeWidthPx / Math.Max(1e-6, jac);
            // A measure with no vertices under its landmarks comes back null, not NaN (which can't be written as JSON).
            JsonNode? Out(double behind, double front)
            {
                var v = (behind - front) / eyeWidthUnits;
                return double.IsNaN(v) || double.IsInfinity(v) ? null : JsonValue.Create(Math.Round(v, 3));
            }

            var corners = Depth(33, 133, 263, 362);
            var result = new JsonObject
            {
                ["nose_projection"] = Out(corners, Depth(1, 4)),
                ["nose_bridge"] = Out(corners, Depth(6, 168)),
                ["upper_lip"] = Out(Depth(61, 291), Depth(0)),
                ["lower_lip"] = Out(Depth(61, 291), Depth(17)),
                ["chin_projection"] = Out(Depth(17), Depth(152, 175)),
                ["brow_ridge"] = Out(corners, Depth(105, 334, 66, 296)),
                ["cheek_fullness"] = Out(Depth(172, 397), Depth(50, 280, 205, 425)),
                ["forehead_slope"] = Out(Depth(10, 151), Depth(9, 107, 336)),
            };
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = new JsonObject
                {
                    ["unit"] = "eye widths toward the viewer (positive = stands out more)",
                    ["eyeWidthUnits"] = double.IsFinite(eyeWidthUnits) ? Math.Round(eyeWidthUnits, 3) : null,
                    ["profile"] = result,
                    ["capture"] = preview,
                }.ToJsonString() }],
            };
        }
        catch (Exception ex) { return Error("MeasureProfile: " + ex.Message); }
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static CallToolResult Error(string message) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = message }) }],
        IsError = true,
    };
}
