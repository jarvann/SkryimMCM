using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// FEAT-307: home layouts. A homestead built in game (place_object) is saved as JSON (base objects, offsets from its centre
/// marker, angles) and built again anywhere: at its own spot, or in front of the player turned the way they look.
/// </summary>
public partial class RaceMenuTools
{
    private const string XMarker = "Skyrim.esm|00003B";
    private static string ShippedLayouts => Path.Combine(AppContext.BaseDirectory, "HomeLayouts");
    private static string SavedLayouts => Path.Combine(SkyLinkSettings.Folder, "HomeLayouts");

    private static IEnumerable<(string File, bool Shipped)> LayoutFiles() =>
        new[] { (SavedLayouts, false), (ShippedLayouts, true) }.Where(d => Directory.Exists(d.Item1))
            .SelectMany(d => Directory.GetFiles(d.Item1, "*.json").Select(f => (f, d.Item2)));

    private static string LayoutSlug(string name) =>
        new string(name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    private async Task<string?> Console1(string command) =>
        (await _pipe.SendRequestAsync("execute_command", new JsonObject { ["command"] = command }))?["output"]?.ToString();

    private static double? Number(string? output) =>
        output != null && output.LastIndexOf(">>", StringComparison.Ordinal) is var i and >= 0 && double.TryParse(output[(i + 2)..].Trim().Split(' ')[0],
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    [McpServerTool]
    [Description("Save a homestead built in game as a home layout (FEAT-307): every object placed at run time within radius of its centre " +
        "marker (center: that reference, e.g. the marker SetSpouseHome or set_home used), as base objects, offsets and angles, to " +
        "Documents\\My Games\\...\\SkyLinkAI\\HomeLayouts. BuildHomeLayout builds it again, here or anywhere.")]
    public async Task<object> SaveHomeLayout(string name, string center, double radius = 1500, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(name) || LayoutSlug(name).Length == 0) return new { error = "name is required" };
        var found = await _pipe.SendRequestAsync("placed_objects", new JsonObject { ["center"] = center.Trim(), ["radius"] = radius });
        if (found?["error"] != null) return DeserializeResponse(found);
        var pieces = (found?["pieces"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (pieces.Count == 0) return new { error = "No objects placed at run time are within radius of that centre." };
        var file = Path.Combine(SavedLayouts, LayoutSlug(name) + ".json");
        if (File.Exists(file) && !overwrite) return new { error = $"A layout called {name} exists ({file}). Pass overwrite: true to replace it." };
        var layout = new JsonObject
        {
            ["name"] = name.Trim(),
            ["note"] = "Saved by SkyLink from objects placed in game. Offsets are world units from the centre marker; facing is the marker's angle.",
            ["saved"] = DateTime.Now.ToString("s"),
            ["facing"] = Math.Round(found!["facing"]?.GetValue<double>() ?? 0, 1),
            ["world"] = new JsonObject { ["center"] = found["center"]?.DeepClone() },
            ["pieces"] = new JsonArray(pieces.Select(p => (JsonNode)new JsonObject
            {
                ["role"] = "piece", ["base"] = p["base"]?.DeepClone(), ["offset"] = p["offset"]?.DeepClone(), ["angle"] = p["angle"]?.DeepClone(),
                ["what"] = p["what"]?.DeepClone(), ["placedRef"] = p["refId"]?.DeepClone(),
            }).ToArray()),
        };
        Directory.CreateDirectory(SavedLayouts);
        await File.WriteAllTextAsync(file, layout.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new { saved = file, pieces = pieces.Count, note = "The centre marker itself isn't a piece: BuildHomeLayout places a new one." };
    }

    [McpServerTool]
    [Description("Build a saved home layout in the world (FEAT-307). Without name: the layouts there are. where: 'here' (default: centred " +
        "distance units in front of the player, turned to face the way the player looks, as when it was built) or 'original' (its " +
        "own spot). resident: a spawned NPC who lives there (a spouse also moves the marriage quest's house, as SetSpouseHome does). " +
        "Ground differs from place to place: the foundation was made for the slope it was built on, so check it and adjust. " +
        "dryRun (default true) says what would be built. RemoveHomeLayout takes it away.")]
    public async Task<object> BuildHomeLayout(string? name = null, string where = "here", double distance = 700, string? resident = null, bool dryRun = true)
    {
        var files = LayoutFiles().ToList();
        if (string.IsNullOrWhiteSpace(name))
            return new
            {
                layouts = files.Select(f =>
                {
                    try { var d = JsonNode.Parse(File.ReadAllText(f.File)); return (object)new { name = d?["name"]?.ToString(), pieces = (d?["pieces"] as JsonArray)?.Count, shipped = f.Shipped, file = f.File }; }
                    catch (Exception ex) { return new { name = Path.GetFileNameWithoutExtension(f.File), error = ex.Message }; }
                }),
            };
        var match = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.File).Equals(LayoutSlug(name), StringComparison.OrdinalIgnoreCase)
            || (JsonNode.Parse(File.ReadAllText(f.File))?["name"]?.ToString() ?? "").Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match.File == null) return new { error = $"No layout called {name}. BuildHomeLayout without name lists them." };
        var layout = JsonNode.Parse(await File.ReadAllTextAsync(match.File))!;
        var facing = layout["facing"]?.GetValue<double>() ?? 0;
        var origin = layout["world"]?["center"] as JsonArray;
        where = where.Trim().ToLowerInvariant();

        double cx, cy, cz, turn;
        if (where == "original")
        {
            if (origin is not { Count: 3 }) return new { error = "This layout has no original spot." };
            (cx, cy, cz, turn) = (origin[0]!.GetValue<double>(), origin[1]!.GetValue<double>(), origin[2]!.GetValue<double>(), 0);
        }
        else if (where == "here")
        {
            var px = Number(await Console1("player.getpos x")); var py = Number(await Console1("player.getpos y"));
            var pz = Number(await Console1("player.getpos z")); var heading = Number(await Console1("player.getangle z"));
            if (px == null || py == null || pz == null || heading == null) return new { error = "The player's position couldn't be read." };
            var h = heading.Value * Math.PI / 180;
            (cx, cy, cz, turn) = (px.Value + distance * Math.Sin(h), py.Value + distance * Math.Cos(h), pz.Value, heading.Value - facing);
        }
        else return new { error = "where must be 'here' or 'original'" };

        var pieces = (layout["pieces"] as JsonArray ?? []).OfType<JsonObject>().Where(p => p["role"]?.ToString() != "center").ToList();
        var t = turn * Math.PI / 180;
        if (dryRun)
            return new { dryRun = true, layout = layout["name"]?.ToString(), center = new[] { Math.Round(cx), Math.Round(cy) }, turned = Math.Round(turn, 1), pieces = pieces.Count, note = "Pass dryRun: false to build it." };

        // The centre marker on the ground first; every piece then sits where it did relative to it.
        var marker = await _pipe.SendRequestAsync("place_object", new JsonObject { ["base"] = XMarker, ["x"] = cx, ["y"] = cy, ["z"] = cz, ["angle"] = (facing + turn) % 360, ["onGround"] = true });
        if (marker?["error"] != null) return DeserializeResponse(marker);
        var markerRef = marker!["refId"]!.ToString();
        var groundZ = marker["z"]!.GetValue<double>();
        var placed = new List<object>();
        var failed = new List<object>();
        foreach (var p in pieces)
        {
            if (p["offset"] is not JsonArray { Count: 3 } o) continue;
            double ox = o[0]!.GetValue<double>(), oy = o[1]!.GetValue<double>(), oz = o[2]!.GetValue<double>();
            // Turned clockwise by `turn`, as Skyrim measures angles (0 north, 90 east).
            var nx = ox * Math.Cos(t) + oy * Math.Sin(t);
            var ny = -ox * Math.Sin(t) + oy * Math.Cos(t);
            var res = await _pipe.SendRequestAsync("place_object", new JsonObject
            {
                ["base"] = p["base"]?.ToString(), ["x"] = cx + nx, ["y"] = cy + ny, ["z"] = groundZ + oz,
                ["angle"] = ((p["angle"]?.GetValue<double>() ?? 0) + turn + 360) % 360, ["onGround"] = false,
            });
            if (res?["error"] != null) failed.Add(new { what = p["what"]?.ToString(), error = res["error"]!.ToString() });
            else placed.Add(new { what = p["what"]?.ToString(), refId = res!["refId"]?.ToString() });
        }

        object? residentResult = null;
        if (!string.IsNullOrWhiteSpace(resident))
        {
            string? spouse = null;
            try { spouse = (await SpouseAlias("LoveInterest", "GetActorRef"))?["result"]?.ToString(); } catch (McpException) { }
            residentResult = string.Equals(spouse, resident.Trim(), StringComparison.OrdinalIgnoreCase)
                ? await SetSpouseHome("skylink", markerRef)
                : DeserializeResponse(await _pipe.SendRequestAsync("set_home", new JsonObject { ["refId"] = resident.Trim(), ["at"] = markerRef }));
        }
        return new { built = layout["name"]?.ToString(), center = markerRef, placed = placed.Count, failed, resident = residentResult, pieces = placed,
            note = "The marker is the home's centre: set_home and SetSpouseHome take it, SaveHomeLayout and RemoveHomeLayout start from it." };
    }

    [McpServerTool]
    [Description("Take a home built in game away (FEAT-307): every object placed at run time within radius of its centre marker, and the " +
        "marker. Residents stay where they are; give them another home first. dryRun (default true) lists what would go.")]
    public async Task<object> RemoveHomeLayout(string center, double radius = 1500, bool dryRun = true)
    {
        var found = await _pipe.SendRequestAsync("placed_objects", new JsonObject { ["center"] = center.Trim(), ["radius"] = radius });
        if (found?["error"] != null) return DeserializeResponse(found);
        var refs = (found?["pieces"] as JsonArray ?? []).OfType<JsonObject>().Select(p => (p["refId"]?.ToString(), p["what"]?.ToString())).ToList();
        refs.Add((center.Trim(), "centre marker"));
        if (dryRun) return new { dryRun = true, wouldRemove = refs.Select(r => new { refId = r.Item1, what = r.Item2 }), note = "Pass dryRun: false to remove them." };
        var removed = 0;
        foreach (var (refId, _) in refs)
            if ((await _pipe.SendRequestAsync("remove_object", new JsonObject { ["refId"] = refId }))?["removed"]?.GetValue<bool>() == true) removed++;
        return new { removed, of = refs.Count };
    }
}
