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

    private sealed record HomeChoice(string Name, string Kind, string Ref, string? Location);

    /// <summary>Where a character can live: the player's houses, the family homes, SkyLink homes (built for this character).</summary>
    private async Task<(List<HomeChoice> Homes, string? CharacterId, string? Error)> HomeChoices()
    {
        var cid = await CharacterId();
        var homes = new List<HomeChoice>();
        string? error = null;
        try
        {
            var houses = await _pipe.SendRequestAsync("owned_houses");
            if (houses?["error"] != null) error = houses["error"]!.ToString();
            foreach (var h in (houses?["owned"] as JsonArray ?? []).OfType<JsonObject>())
                if (h["ref"]?.ToString() is string r) homes.Add(new(h["name"]?.ToString() ?? r, "house", r, h["location"]?.ToString()));
        }
        catch (McpException ex) { error = ex.Message; }
        foreach (var (center, entry) in HomeRegistry.Load())
            if (entry is JsonObject e && (e["characterId"]?.ToString() == cid || e["characterId"] == null))
                homes.Add(new(e["name"]?.ToString() ?? center, "skylink", center, null));
        return (homes, cid, error);
    }

    /// <summary>The home a choice names: "here", "family" (the main family home), a house or SkyLink home by name, or a reference.</summary>
    private static HomeChoice? ResolveHome(string home, List<HomeChoice> homes, string? characterId)
    {
        var key = home.Trim();
        if (key.StartsWith("family", StringComparison.OrdinalIgnoreCase))
            return FamilyHomes.Of(characterId).FirstOrDefault() is { } f ? new(f["name"]!.ToString(), f["kind"]!.ToString(), f["ref"]!.ToString(), f["location"]?.ToString()) : null;
        key = key.Replace("(SkyLink home)", "", StringComparison.OrdinalIgnoreCase).Trim();
        return homes.FirstOrDefault(h => h.Name.Equals(key, StringComparison.OrdinalIgnoreCase) || h.Ref.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? homes.FirstOrDefault(h => h.Name.StartsWith(key, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> HomeOptionNames(List<HomeChoice> homes, string? characterId)
    {
        var names = new List<string> { "here (stays where it stands)" };
        if (FamilyHomes.Of(characterId).FirstOrDefault() is { } f) names.Add($"family ({f["name"]})");
        names.AddRange(homes.Select(h => h.Kind == "skylink" ? $"{h.Name} (SkyLink home)" : h.Name));
        return names.Distinct().ToList();
    }

    /// <summary>
    /// Gives a character its home: the spouse through the marriage quest (SetSpouseHome), anyone else through set_home (lives there,
    /// the place counts as theirs). A relative's first home becomes the family home.
    /// </summary>
    private async Task<object> ApplyHome(string refId, string home, string? familyRole)
    {
        if (home.Trim().StartsWith("here", StringComparison.OrdinalIgnoreCase)) return new { home = "here", note = "Stays where it stands." };
        var (homes, cid, error) = await HomeChoices();
        if (ResolveHome(home, homes, cid) is not { } chosen)
            return new { error = $"No home called {home}.", choices = HomeOptionNames(homes, cid), houses = error, note = CharacterChoices.HouseNote };
        string? spouse = null;
        try { spouse = (await SpouseAlias("LoveInterest", "GetActorRef"))?["result"]?.ToString(); } catch (McpException) { }
        object applied;
        if (string.Equals(spouse, refId, StringComparison.OrdinalIgnoreCase))
            applied = chosen.Kind == "skylink" ? await SetSpouseHome("skylink", chosen.Ref) : await SetSpouseHome(chosen.Name);
        else
        {
            var set = await _pipe.SendRequestAsync("set_home", new JsonObject { ["refId"] = refId, ["at"] = chosen.Ref });
            applied = DeserializeResponse(set);
            if (set?["error"] != null) return applied;
        }
        string? family = null;
        if (familyRole != null && cid != null && FamilyHomes.Of(cid).Count == 0)
        {
            FamilyHomes.Add(cid, new JsonObject { ["name"] = chosen.Name, ["kind"] = chosen.Kind, ["ref"] = chosen.Ref, ["location"] = chosen.Location }, main: true);
            family = $"{chosen.Name} is now the family home.";
        }
        if (SlotRegistry.SlotForRef(refId) is int slot && SlotRegistry.Load()[slot.ToString()] is JsonObject entry)
        {
            var copy = entry.DeepClone().AsObject();
            copy["home"] = new JsonObject { ["name"] = chosen.Name, ["kind"] = chosen.Kind, ["ref"] = chosen.Ref };
            SlotRegistry.Reserve(slot, copy);
        }
        return new { home = chosen.Name, kind = chosen.Kind, applied, familyHome = family };
    }

    [McpServerTool]
    [Description("Where a spawned character lives (FEAT-307). Without home: the choices (the player's own houses, the family home, SkyLink homes). " +
        "home: 'here', 'family', a house by name (Breezehome...) or a SkyLink home. The spouse moves through the marriage quest (SetSpouseHome), so " +
        "'your spouse's house' follows. A relative's first home becomes the family home. Ask the player which.")]
    public async Task<object> SetHome(string refId, string? home = null)
    {
        var (homes, cid, error) = await HomeChoices();
        if (string.IsNullOrWhiteSpace(home)) return new { choices = HomeOptionNames(homes, cid), houses = error, note = CharacterChoices.HouseNote };
        string? role = null;
        if (SlotRegistry.SlotForRef(refId.Trim()) is int slot) role = SlotRegistry.Load()[slot.ToString()]?["family"]?.GetValue<string?>();
        return await ApplyHome(refId.Trim(), home, role);
    }

    [McpServerTool]
    [Description("The family's homes (FEAT-307), kept per player character: one of the player's own houses or a SkyLink home. Without home: " +
        "the family homes and the choices. home: add one (main: true makes it the one relatives are offered first). remove: a family home's name.")]
    public async Task<object> SetFamilyHome(string? home = null, bool main = true, string? remove = null)
    {
        var (homes, cid, error) = await HomeChoices();
        if (cid == null) return new { error = "No player character (load a game first)." };
        if (!string.IsNullOrWhiteSpace(remove))
            return FamilyHomes.Remove(cid, remove.Trim()) ? new { removed = remove.Trim(), familyHomes = FamilyHomes.Of(cid) } : new { error = $"No family home called {remove}." };
        if (string.IsNullOrWhiteSpace(home))
            return new { familyHomes = FamilyHomes.Of(cid), choices = homes.Select(h => h.Kind == "skylink" ? $"{h.Name} (SkyLink home)" : h.Name), houses = error,
                note = homes.Count == 0 ? "The player owns no house yet: buy one, or build a SkyLink home (BuildHomeLayout)." : null };
        if (ResolveHome(home, homes, null) is not { } chosen || home.Trim().StartsWith("family", StringComparison.OrdinalIgnoreCase))
            return new { error = $"No house or SkyLink home called {home}.", choices = homes.Select(h => h.Name) };
        FamilyHomes.Add(cid, new JsonObject { ["name"] = chosen.Name, ["kind"] = chosen.Kind, ["ref"] = chosen.Ref, ["location"] = chosen.Location }, main);
        return new { familyHomes = FamilyHomes.Of(cid) };
    }

    /// <summary>A home's own pieces: those given, else SkyLink's record of the home; null when neither exists.</summary>
    private static HashSet<string>? OwnPieces(string center, string? given) =>
        string.IsNullOrWhiteSpace(given)
            ? HomeRegistry.Pieces(center)
            : given.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<string?> CharacterId()
    {
        try { return (await _pipe.SendRequestAsync("get_appearance"))?["characterId"]?.ToString(); } catch (McpException) { return null; }
    }

    [McpServerTool]
    [Description("Save a homestead built in game as a home layout (FEAT-307): every object placed at run time within radius of its centre " +
        "marker (center: that reference, e.g. the marker SetSpouseHome or set_home used), as base objects, offsets and angles, to " +
        "Documents\\My Games\\...\\SkyLinkAI\\HomeLayouts. Only the home's own pieces: those SkyLink built (it keeps a record), or pieces " +
        "(reference IDs) for a home it didn't; never the game's own objects nearby. " +
        "BuildHomeLayout builds it again, here or anywhere.")]
    public async Task<object> SaveHomeLayout(string name, string center, double radius = 1500, bool overwrite = false, string? pieces = null)
    {
        if (string.IsNullOrWhiteSpace(name) || LayoutSlug(name).Length == 0) return new { error = "name is required" };
        var found = await _pipe.SendRequestAsync("placed_objects", new JsonObject { ["center"] = center.Trim(), ["radius"] = radius });
        if (found?["error"] != null) return DeserializeResponse(found);
        var near = (found?["pieces"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var own = OwnPieces(center, pieces);
        if (own == null)
            return new
            {
                error = "SkyLink has no record of this home's pieces. Pass pieces (their reference IDs, comma-separated) from the list below; " +
                    "the game places objects of its own nearby (critters and others), which aren't part of the home.",
                placedNearby = near.Select(p => new { refId = p["refId"]?.ToString(), type = p["type"]?.ToString(), what = p["what"]?.ToString(), offset = p["offset"] }),
            };
        var chosen = near.Where(p => own.Contains(p["refId"]?.ToString() ?? "")).ToList();
        if (chosen.Count == 0) return new { error = "None of the home's pieces are in this game (another save?)." };
        if (pieces != null) HomeRegistry.Record(center.Trim(), name.Trim(), await CharacterId(), chosen.Select(p => p["refId"]!.ToString()));
        var file = Path.Combine(SavedLayouts, LayoutSlug(name) + ".json");
        if (File.Exists(file) && !overwrite) return new { error = $"A layout called {name} exists ({file}). Pass overwrite: true to replace it." };
        var layout = new JsonObject
        {
            ["name"] = name.Trim(),
            ["note"] = "Saved by SkyLink from objects placed in game. Offsets are world units from the centre marker; facing is the marker's angle.",
            ["saved"] = DateTime.Now.ToString("s"),
            ["facing"] = Math.Round(found!["facing"]?.GetValue<double>() ?? 0, 1),
            ["world"] = new JsonObject { ["center"] = found["center"]?.DeepClone() },
            ["pieces"] = new JsonArray(chosen.Select(p => (JsonNode)new JsonObject
            {
                ["role"] = "piece", ["base"] = p["base"]?.DeepClone(), ["offset"] = p["offset"]?.DeepClone(), ["angle"] = p["angle"]?.DeepClone(),
                ["what"] = p["what"]?.DeepClone(), ["placedRef"] = p["refId"]?.DeepClone(),
            }).ToArray()),
        };
        Directory.CreateDirectory(SavedLayouts);
        await File.WriteAllTextAsync(file, layout.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new { saved = file, pieces = chosen.Count, note = "The centre marker itself isn't a piece: BuildHomeLayout places a new one." };
    }

    [McpServerTool]
    [Description("Build a saved home layout in the world (FEAT-307). Without name: the layouts there are. where: 'here' (default: centred " +
        "distance units in front of the player, turned to face the way the player looks, as when it was built) or 'original' (its " +
        "own spot), or a reference such as an old home's centre marker (rebuilt in the same place, turned the same way; build first, then RemoveHomeLayout the old one). resident: a spawned NPC who lives there (a spouse also moves the marriage quest's house, as SetSpouseHome does). " +
        "The building and its foundation stand with the door at ground level; each yard piece stands on its own ground (the land only: rocks " +
        "aren't seen, so check pieces near rocks). " +
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
        else if (System.Text.RegularExpressions.Regex.IsMatch(where, "^[0-9a-f]{8}$"))
        {
            // A home's centre marker (or any reference): rebuild in the same place, turned the same way.
            var rx = Number(await Console1($"{where}.getpos x")); var ry = Number(await Console1($"{where}.getpos y"));
            var rz = Number(await Console1($"{where}.getpos z")); var ra = Number(await Console1($"{where}.getangle z"));
            if (rx == null || ry == null || rz == null || ra == null) return new { error = $"No reference {where} to build at." };
            (cx, cy, cz, turn) = (rx.Value, ry.Value, rz.Value, ra.Value - facing);
        }
        else return new { error = "where must be 'here', 'original' or a reference (a home's centre marker)" };

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
        var placedRefs = new List<string>();
        var failed = new List<object>();

        // Where each piece goes, turned clockwise by `turn`, as Skyrim measures angles (0 north, 90 east).
        var spots = new List<(JsonObject Piece, double X, double Y, double Oz)>();
        foreach (var p in pieces)
        {
            if (p["offset"] is not JsonArray { Count: 3 } o) continue;
            double ox = o[0]!.GetValue<double>(), oy = o[1]!.GetValue<double>();
            spots.Add((p, cx + ox * Math.Cos(t) + oy * Math.Sin(t), cy - ox * Math.Sin(t) + oy * Math.Cos(t), o[2]!.GetValue<double>()));
        }
        // Steam, 2026-11-24: all at the marker's height plus the layout's offsets, a slope buried the uphill pieces (the door) and left
        // the downhill ones in the air (table, chairs, the pot over the fire). Now the building and its foundation move as one, with
        // the door at ground level (footing walls hide a drop behind, the hill takes a rise), and each yard piece stands on its own
        // ground; a piece stacked on another (a bed in the tent, the pot over the fire) keeps its height above it.
        var house = spots.FirstOrDefault(s => s.Piece["role"]?.ToString() == "house");
        double? houseZ = null;
        if (house.Piece != null)
        {
            var dx = cx - house.X; var dy = cy - house.Y; var len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var door = new JsonArray(house.X + dx / len * 250, house.Y + dy / len * 250);
            var probe = await _pipe.SendRequestAsync("ground_heights", new JsonObject { ["points"] = new JsonArray(door) });
            if ((probe?["heights"] as JsonArray)?.FirstOrDefault() is JsonValue h && h.TryGetValue<double>(out var doorGround)) houseZ = doorGround - 10;
        }
        var anchors = new Dictionary<(long, long), (double Oz, double Z)>();
        foreach (var (p, x, y, oz) in spots.OrderBy(s => s.Oz))
        {
            var role = p["role"]?.ToString();
            var built = role is "house" or "foundation" or "footing";
            var key = ((long)Math.Round(x / 20), (long)Math.Round(y / 20));
            var stacked = !built && anchors.ContainsKey(key);
            double z = built && houseZ is double hz ? hz + oz - house.Oz
                : stacked ? anchors[key].Z + oz - anchors[key].Oz
                : groundZ + oz;
            var res = await _pipe.SendRequestAsync("place_object", new JsonObject
            {
                ["base"] = p["base"]?.ToString(), ["x"] = x, ["y"] = y, ["z"] = z,
                ["angle"] = ((p["angle"]?.GetValue<double>() ?? 0) + turn + 360) % 360,
                ["onGround"] = !built && !stacked,
            });
            if (res?["error"] != null) { failed.Add(new { what = p["what"]?.ToString(), error = res["error"]!.ToString() }); continue; }
            placed.Add(new { what = p["what"]?.ToString(), refId = res!["refId"]?.ToString() });
            if (res["refId"]?.ToString() is string placedRef) placedRefs.Add(placedRef);
            if (!built && !stacked && res["z"]?.GetValue<double>() is double onGround) anchors[key] = (oz, onGround);
        }

        HomeRegistry.Record(markerRef, layout["name"]?.ToString() ?? name, await CharacterId(), placedRefs);

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
        "marker. Residents stay where they are (the result names them: give them another home); a family home stops being one. dryRun (default true) lists what would go.")]
    public async Task<object> RemoveHomeLayout(string center, double radius = 1500, bool dryRun = true, string? pieces = null)
    {
        var found = await _pipe.SendRequestAsync("placed_objects", new JsonObject { ["center"] = center.Trim(), ["radius"] = radius });
        if (found?["error"] != null) return DeserializeResponse(found);
        var own = OwnPieces(center, pieces);
        if (own == null) return new { error = "SkyLink has no record of this home's pieces, so nothing was removed. Pass pieces (reference IDs, comma-separated)." };
        var refs = (found?["pieces"] as JsonArray ?? []).OfType<JsonObject>().Where(p => own.Contains(p["refId"]?.ToString() ?? ""))
            .Select(p => (p["refId"]?.ToString(), p["what"]?.ToString())).ToList();
        refs.Add((center.Trim(), "centre marker"));
        if (dryRun) return new { dryRun = true, wouldRemove = refs.Select(r => new { refId = r.Item1, what = r.Item2 }), note = "Pass dryRun: false to remove them." };
        var removed = 0;
        foreach (var (refId, _) in refs)
            if ((await _pipe.SendRequestAsync("remove_object", new JsonObject { ["refId"] = refId }))?["removed"]?.GetValue<bool>() == true) removed++;
        if (removed == refs.Count) HomeRegistry.Forget(center);
        // Steam, 2026-11-24: a removed homestead stayed the family home, pointing at a marker that was gone.
        var familyLists = FamilyHomes.RemoveEverywhere(center.Trim());
        var residents = SlotRegistry.Load().Select(kv => kv.Value as JsonObject)
            .Where(e => string.Equals(e?["home"]?["ref"]?.ToString(), center.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(e => $"{e!["name"]} ({e["refId"]})").ToList();
        return new
        {
            removed, of = refs.Count,
            familyHome = familyLists > 0 ? "It was the family home and isn't any more: SetFamilyHome sets another." : null,
            residents = residents.Count > 0 ? residents : null,
            note = residents.Count > 0 ? "These lived there: give each another home (SetHome)." : null,
        };
    }
}
