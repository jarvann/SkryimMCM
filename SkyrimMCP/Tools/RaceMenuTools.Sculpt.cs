using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkyrimMCP.Face;

namespace SkyrimMCP.Tools;

/// <summary>
/// Face builder M7 (FEAT-233): a sculpt brush. Sliders can't move everything (the lower eyelid on a sculpted head
/// answered to none of five), so this moves the vertices under a face region directly, the way RaceMenu's sculpt tab
/// does, and stores them in the preset's sculpt data. How it finds the vertices: a front capture is taken with every
/// head-part vertex projected onto the same frozen frame (plugin snapshot); the face landmarks found in that capture
/// mark the region; vertices near it, on the side facing the camera, get a smooth share of the move.
/// </summary>
public partial class RaceMenuTools
{
    // MediaPipe face-mesh landmarks per region; both sides together, so a move is mirrored.
    private static readonly Dictionary<string, int[]> SculptRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lower_lids"] = [145, 153, 154, 144, 163, 7, 374, 380, 381, 373, 390, 249],
        ["upper_lids"] = [159, 158, 157, 160, 161, 386, 385, 384, 387, 388],
        ["outer_eye_corners"] = [33, 130, 263, 359],
        ["inner_eye_corners"] = [133, 243, 362, 463],
        ["brows"] = [70, 63, 105, 66, 107, 46, 53, 52, 65, 55, 300, 293, 334, 296, 336, 276, 283, 282, 295, 285],
        ["nose_tip"] = [1, 2, 4, 5, 19, 94],
        ["nose_bridge"] = [6, 168, 197, 195],
        ["nostrils"] = [129, 358, 98, 327, 48, 278, 64, 294],
        ["upper_lip"] = [0, 37, 39, 40, 185, 267, 269, 270, 409, 13, 82, 81, 80, 312, 311, 310],
        ["lower_lip"] = [17, 84, 181, 91, 314, 405, 321, 14, 87, 178, 88, 317, 402, 318],
        ["mouth_corners"] = [61, 291, 146, 375],
        ["chin"] = [152, 148, 176, 149, 377, 400, 378, 175, 199, 200],
        ["jaw"] = [172, 136, 150, 397, 365, 379, 58, 288],
        ["cheeks"] = [50, 280, 101, 330, 118, 347, 205, 425, 123, 352],
        ["cheekbones"] = [116, 345, 117, 346, 111, 340],
    };

    private static readonly string[] SculptDirections = ["up", "down", "in", "out", "forward", "back"];

    [McpServerTool]
    [Description("Sculpt the player's face like RaceMenu's sculpt brush, for what no slider moves (FEAT-233, experimental). region: " +
        "lower_lids, upper_lids, outer_eye_corners, inner_eye_corners, brows, nose_tip, nose_bridge, nostrils, upper_lip, lower_lip, " +
        "mouth_corners, chin, jaw, cheeks, cheekbones (both sides at once, mirrored). direction: up, down, in (toward the middle of " +
        "the face), out, forward (toward the viewer) or back. amount: in eye widths, the same unit as MeasureFace's lid measures " +
        "(0.01 = 1% of an eye width; the measurement sees from about 0.03; at most 0.25 a call). radius: how far the move spreads around the region, in eye widths " +
        "(default 0.6 for cheeks, cheekbones, jaw and chin, 0.3 elsewhere), fading smoothly. Takes a front capture (no image returned) to find the region, moves the vertices of every " +
        "head part there except the eyeballs and hair (lashes, tear ducts and brows follow), and loads the result like SetFaceSliders, with " +
        "the same session backup. Small steps, then MeasureFace(samples: 3) under the studio light; undo with the opposite " +
        "direction or LoadRaceMenuPreset. dryRun shows what would move without changing the face.")]
    public async Task<object> SculptFace(string region, string direction, double amount, double? radius = null, bool dryRun = false)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return new { error = "Sculpting needs Windows (face landmarks)." };
            if (!SculptRegions.TryGetValue(region.Trim(), out var landmarkIds))
                return new { error = $"region must be one of {string.Join(", ", SculptRegions.Keys)}" };
            var dir = direction.Trim().ToLowerInvariant();
            if (!SculptDirections.Contains(dir)) return new { error = $"direction must be one of {string.Join(", ", SculptDirections)}" };
            if (amount <= 0 || amount > 0.25) return new { error = "amount must be above 0 and at most 0.25 eye widths a call" };
            // BUG-052: broad regions need a wider reach (cheeks at 0.3 moved only 21 head vertices, at 0.6 43 to 50).
        var wide = region.Trim().ToLowerInvariant() is "cheeks" or "cheekbones" or "jaw" or "chin";
        var reachRadius = Math.Clamp(radius ?? (wide ? 0.6 : 0.3), 0.05, 1.5);
        // The eye mesh (lashes, tear duct) only follows the lids and eye corners; other strokes leave it alone.
        var eyeRegion = region.Trim().ToLowerInvariant().Contains("lid") || region.Trim().ToLowerInvariant().Contains("eye_corner");
            if (await RaceMenuOpenError() is string open) return new { error = open };
    
            // 1. A front capture with the vertex snapshot of the same frozen frame.
            var capture = new FaceCaptureTools(_pipe);
            var shot = await capture.CaptureForSculpt(hideHair: true);
            if (shot.IsError == true)
                return new { error = "Capture failed: " + string.Join(" ", shot.Content.OfType<TextContentBlock>().Select(b => b.Text)) };
            var snapshot = capture.Snapshot;
            if (snapshot?["error"] is JsonNode snapError) return new { error = "Vertex snapshot failed: " + snapError };
            if (snapshot?["hosts"] is not JsonArray hosts || hosts.Count == 0) return new { error = "No sculptable head parts found in the snapshot." };
    
            string? preview = null;
            int[]? cropPixels = null;
            int sourceW = 0, sourceH = 0;
            foreach (var block in shot.Content.OfType<TextContentBlock>())
            {
                JsonNode? node;
                try { node = JsonNode.Parse(block.Text); } catch (JsonException) { continue; }
                if (node?["preview"] is JsonObject p)
                {
                    preview = p["file"]?.GetValue<string>();
                    cropPixels = p["cropPixels"]?.GetValue<string>()?.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    var size = p["sourceSize"]?.GetValue<string>()?.Split('x');
                    if (size is { Length: 2 }) { sourceW = int.Parse(size[0], CultureInfo.InvariantCulture); sourceH = int.Parse(size[1], CultureInfo.InvariantCulture); }
                }
            }
            if (preview == null || cropPixels is not { Length: 4 } || sourceW == 0)
                return new { error = "The capture didn't report its preview and crop." };
    
            // 2. Face landmarks in the capture, in pixels of the full screenshot (the snapshot's frame).
            var found = FaceMeasurer.Shared.FindLandmarks(preview);
            if (found == null) return new { error = "No face found in the capture: check it with CaptureFace." };
            var (lm, pw, ph) = found.Value;
            double sx = (double)cropPixels[2] / pw, sy = (double)cropPixels[3] / ph;
            (double X, double Y) Px(int i) => (cropPixels[0] + lm[i, 0] * sx, cropPixels[1] + lm[i, 1] * sy);
            double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            var eyeWidthPx = (Dist(Px(33), Px(133)) + Dist(Px(362), Px(263))) / 2;
            var centreX = Px(168).X;
            var regionPts = landmarkIds.Select(Px).ToArray();
            var reach = reachRadius * eyeWidthPx;
    
            // 3. Candidate vertices near the region, on the camera side (vertices behind the surface also project there).
            var candidates = new List<(JsonObject Host, int Index, double Weight, double PxX, double Dist)>();
            double pxPerUnit = 0;
            var notes = new List<string>();
            foreach (var host in hosts.OfType<JsonObject>())
            {
                var type = host["type"]?.GetValue<int>() ?? -1;
                if (type == 3) continue;  // hair
            if (type == 2 && !eyeRegion) continue;  // eye mesh: only for the lids and eye corners
                if (host["xyd"] is not JsonArray xyd) continue;
                // BUG-047: an eye mesh also carries the lower lashes, the tear duct and a lid strip; those must follow the
                // lids. Leave out only the eyeballs: the largest connected pieces of its chargen TRI.
                HashSet<int>? skip = null;
                if (type == 2)
                {
                    var hostPath = host["host"]?.GetValue<string>() ?? "";
                    var pieces = TriFile.Pieces(hostPath);
                    if (pieces == null || pieces.Count < 2)
                    {
                        notes.Add($"{hostPath}: TRI file not found as a loose file, so the eye mesh (with its lower lashes) was left out.");
                        continue;
                    }
                    var eyeball = pieces[0].Length;
                    skip = pieces.Where(p => p.Length == eyeball).SelectMany(p => p).ToHashSet();
                }
                if (type == 1 && host["jacobian"] is JsonArray jac)
                    pxPerUnit = jac.OfType<JsonArray>().Select(a => Math.Sqrt(Math.Pow(a[0]!.GetValue<double>() * sourceW, 2) + Math.Pow(a[1]!.GetValue<double>() * sourceH, 2)))
                        .OrderByDescending(v => v).Take(2).Average();
                for (var v = 0; v + 2 < xyd.Count; v += 3)
                {
                    if (skip != null && skip.Contains(v / 3)) continue;
                    var x = xyd[v]!.GetValue<double>();
                    var y = xyd[v + 1]!.GetValue<double>();
                    if (x < 0 || y < 0) continue;
                    var pt = (x * sourceW, y * sourceH);
                    var d = regionPts.Min(r => Dist(r, pt));
                    if (d >= reach) continue;
                    var t = d / reach;
                    candidates.Add((host, v / 3, Math.Pow(1 - t * t, 2), pt.Item1, xyd[v + 2]!.GetValue<double>()));
                }
            }
            if (candidates.Count == 0) return new { error = $"No vertices found under {region}: is the face in the capture? Try a larger radius." };
            if (pxPerUnit <= 0) pxPerUnit = 1;
            var eyeWidthUnits = eyeWidthPx / pxPerUnit;
            var nearest = candidates.Min(c => c.Dist);
            var band = 0.6 * eyeWidthUnits;
            candidates = candidates.Where(c => c.Dist <= nearest + band).ToList();
    
            // 4. The move for each vertex, in its mesh's own space: invert how a mesh unit moves on screen (snapshot jacobian).
            var movePx = amount * eyeWidthPx;
            var moveUnits = amount * eyeWidthUnits;
            var edits = new Dictionary<string, (JsonObject Host, Dictionary<int, double[]> Offsets)>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates)
            {
                var jac = (c.Host["jacobian"] as JsonArray)!.OfType<JsonArray>().Select(a => a.Select(n => n!.GetValue<double>()).ToArray()).ToArray();
                // screen = M * mesh, with M[component, axis]
                var m = new double[3, 3];
                for (var axis = 0; axis < 3; axis++)
                    for (var comp = 0; comp < 3; comp++) m[comp, axis] = jac[axis][comp];
                var side = c.PxX < centreX ? 1.0 : -1.0;  // toward the middle of the face on screen
                double[] target = dir switch
                {
                    "up" => [0, -movePx / sourceH, 0],
                    "down" => [0, movePx / sourceH, 0],
                    "in" => [side * movePx / sourceW, 0, 0],
                    "out" => [-side * movePx / sourceW, 0, 0],
                    "forward" => [0, 0, -moveUnits],
                    _ => [0, 0, moveUnits],
                };
                var mesh = Solve3(m, target);
                if (mesh == null) continue;
                var hostName = c.Host["host"]!.GetValue<string>();
                if (!edits.TryGetValue(hostName, out var e)) edits[hostName] = e = (c.Host, new Dictionary<int, double[]>());
                e.Offsets[c.Index] = mesh.Select(v => v * c.Weight).ToArray();
            }
    
            var summary = edits.Select(kv => new
            {
                host = kv.Key,
                part = kv.Value.Host["part"]?.GetValue<string>(),
                verticesMoved = kv.Value.Offsets.Count,
                largestMove = Math.Round(kv.Value.Offsets.Values.Max(o => Math.Sqrt(o[0] * o[0] + o[1] * o[1] + o[2] * o[2])), 4),
            }).ToList();
    
            // 5. Add the offsets to the preset's sculpt data (sculptDivisor 10000) and load it.
            var (outcome, backup, file) = await RoundTrip(preset =>
            {
                var morphs = preset["morphs"] as JsonObject ?? new JsonObject();
                preset["morphs"] = morphs;
                var divisor = morphs["sculptDivisor"]?.GetValue<double>() ?? 10000;
                morphs["sculptDivisor"] = divisor;
                var sculpt = morphs["sculpt"] as JsonArray ?? new JsonArray();
                morphs["sculpt"] = sculpt;
                static string Norm(string? h) => (h ?? "").Replace('/', '\\').Trim().ToLowerInvariant();
                foreach (var (hostName, (host, offsets)) in edits)
                {
                    var entries = sculpt.OfType<JsonObject>().Where(h => Norm(h["host"]?.GetValue<string>()) == Norm(hostName)).ToList();
                    if (entries.Count == 0)
                    {
                        var created = new JsonObject { ["host"] = hostName, ["vertices"] = host["vertices"]?.GetValue<int>() ?? 0, ["data"] = new JsonArray() };
                        sculpt.Add(created);
                        entries.Add(created);
                    }
                    foreach (var entry in entries)
                    {
                        var data = entry["data"] as JsonArray ?? new JsonArray();
                        entry["data"] = data;
                        var byIndex = new Dictionary<int, JsonArray>();
                        foreach (var row in data.OfType<JsonArray>())
                            if (row.Count >= 4) byIndex[(int)Number(row[0])] = row;
                        foreach (var (index, o) in offsets)
                        {
                            if (!byIndex.TryGetValue(index, out var row))
                            {
                                row = new JsonArray((long)index, 0L, 0L, 0L);
                                data.Add(row);
                                byIndex[index] = row;
                            }
                            for (var k = 0; k < 3; k++)
                                row[k + 1] = (long)Math.Round(Number(row[k + 1]) + o[k] * divisor);
                        }
                    }
                }
                return true;
            }, dryRun);
    
            var loaded = outcome?.Loaded == true;
            if (loaded) await NotifyInGame($"Sculpted: {region} {dir}");
            return new
            {
                applied = loaded,
                dryRun,
                region,
                direction = dir,
                amount,
                radius = reachRadius,
                eyeWidthPixels = Math.Round(eyeWidthPx, 1),
                eyeWidthUnits = Math.Round(eyeWidthUnits, 3),
                moved = summary,
                headParts = outcome?.HeadParts?.DeepClone(),
                tints = outcome?.Tints,
                backup,
                restoreWith = _sessionBackup != null ? $"LoadRaceMenuPreset('{_sessionBackup}')" : null,
                file,
                notes = notes.Count > 0 ? notes : null,
                next = "MeasureFace(samples: 3) under the studio light; look at a side view for forward/back moves. Moves under " +
                       "about 0.03 eye widths are below what the measurement can see.",
                error = !dryRun && !loaded ? "CharGen.LoadCharacterPreset returned false: the face is unchanged." : null,
            };
        
        }
        catch (Exception ex) { return new { error = "SculptFace: " + ex.Message }; }
    }

    // BUG-051: a number from the preset file (a JsonElement) or one written here (an int or long): GetValue<double>()
    // throws on the latter, which broke the brush on a face with no sculpt yet (every row new).
    private static double Number(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return 0;
    }

    // Solves m * x = b for a 3x3 m (Cramer's rule); null when m is singular.
    private static double[]? Solve3(double[,] m, double[] b)
    {
        double Det(double[,] a) =>
            a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) -
            a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0]) +
            a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
        var det = Det(m);
        if (Math.Abs(det) < 1e-12) return null;
        var x = new double[3];
        for (var col = 0; col < 3; col++)
        {
            var a = (double[,])m.Clone();
            for (var row = 0; row < 3; row++) a[row, col] = b[row];
            x[col] = Det(a) / det;
        }
        return x;
    }
}
