using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// Face builder M1 (FEAT-228): read and change the live face through RaceMenu presets. RaceMenu has no
/// script call to set one face slider (checked in game: NiOverride and CharGen expose none), so a change
/// is a round trip: CharGen.SaveCharacterPreset writes the live face to a .jslot, SkyLink edits that
/// JSON, and CharGen.LoadCharacterPreset applies it, the same loader as RaceMenu's Presets tab.
/// </summary>
public partial class RaceMenuTools
{
    // Working presets live in their own subfolder of CharGen\Presets so they don't crowd the player's.
    private const string WorkFolder = "_SkyLink";
    private static string? _sessionBackup;

    // RaceMenu's vanilla face morphs (morphs.default.morphs), in the record's NAM9 order.
    private static readonly string[] VanillaMorphs =
    {
        "noseLongShort", "noseUpDown", "jawUpDown", "jawNarrowWide", "jawForwardBack",
        "cheeksUpDown", "cheeksForwardBack", "eyesUpDown", "eyesInOut",
        "browsUpDown", "browsInOut", "browsForwardBack", "lipsUpDown", "lipsInOut",
        "chinNarrowWide", "chinUpDown", "chinUnderbiteOverbite", "eyesForwardBack",
    };

    // morphs.default.presets: nose, (unused), eyes, mouth.
    private static readonly (string Name, int Index)[] VanillaPresets = { ("presetNose", 0), ("presetEyes", 2), ("presetMouth", 3) };

    /// <summary>"a/b" with each part a plain file name, or null.</summary>
    private static string? CleanPresetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var parts = name.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        var clean = string.Join('/', parts);
        return clean.EndsWith(".jslot", StringComparison.OrdinalIgnoreCase) ? clean[..^6] : clean;
    }

    private static string PapyrusPresetName(string clean) => clean.Replace('/', '\\');

    private async Task<string?> RaceMenuOpenError()
    {
        var menus = await _pipe.SendRequestAsync("get_menu_state");
        return (menus?["allOpenMenus"]?.ToJsonString() ?? "").Contains("RaceSex Menu", StringComparison.OrdinalIgnoreCase)
            ? "RaceMenu is open. Close it first: changing the face by script while it is open breaks the face tint."
            : null;
    }

    // SaveCharacterPreset doesn't create subfolders, so make sure the preset's folder exists in the real Data folder.
    private static void EnsurePresetFolder(string clean)
    {
        var slash = clean.LastIndexOf('/');
        var (game, _) = SkyLinkSettings.SkyrimPath();
        if (slash < 0 || game == null) return;
        try { Directory.CreateDirectory(Path.Combine(game, "Data", "SKSE", "Plugins", "CharGen", "Presets", clean[..slash].Replace('/', Path.DirectorySeparatorChar))); }
        catch { /* the save reports it if the folder is missing */ }
    }

    /// <summary>Saves the player's live look as a Presets-tab preset and returns the file it wrote.</summary>
    private async Task<PresetFile> SaveLivePreset(string clean)
    {
        EnsurePresetFolder(clean);
        var started = DateTime.UtcNow.AddSeconds(-2);
        await Papyrus("CharGen", "SaveCharacterPreset", "player", PapyrusPresetName(clean));
        for (var i = 0; i < 20; i++)
        {
            var file = FindPresets().FirstOrDefault(p => p.Kind == "Presets" && p.Name.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (file != null && File.GetLastWriteTimeUtc(file.Path) >= started) return file;
            await Task.Delay(100);
        }
        throw new McpException($"CharGen.SaveCharacterPreset didn't write CharGen\\Presets\\{PapyrusPresetName(clean)}.jslot");
    }

    /// <summary>Applies a Presets-tab preset to the player with RaceMenu's own preset loader.</summary>
    private async Task<bool> LoadPresetsTabPreset(string clean)
    {
        var appearance = await _pipe.SendRequestAsync("get_appearance");
        var hairColor = appearance?["hairColorFormId"]?.GetValue<string>();
        var result = await Papyrus("CharGen", "LoadCharacterPreset", "player", PapyrusPresetName(clean),
            hairColor == null ? null : JsonValue.Create(hairColor));
        return result?.GetValueKind() == JsonValueKind.True;
    }

    private static JsonObject ReadJslot(string path) =>
        JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new McpException($"{path} is not a JSON object");

    private static void WriteJslot(string path, JsonObject preset) =>
        File.WriteAllText(path, preset.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private PresetFile ResolvePreset(string name)
    {
        var clean = CleanPresetName(name) ?? throw new McpException("name must be a preset name, e.g. 'MyFace' or 'MyFolder/MyFace'");
        var all = FindPresets();
        return all.FirstOrDefault(p => p.Name.Equals(clean, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(p => Path.GetFileName(p.Name).Equals(Path.GetFileName(clean), StringComparison.OrdinalIgnoreCase))
               ?? throw new McpException($"No preset named '{clean}'. Presets: {string.Join(", ", all.Select(p => $"{p.Kind}/{p.Name}"))}");
    }

    // ---- reading ----

    private static (uint Argb, string Hex, double Alpha) Tint(JsonNode? color)
    {
        var argb = color is JsonValue v && v.TryGetValue<long>(out var l) ? unchecked((uint)l) : 0u;
        return (argb, $"#{argb & 0xFFFFFF:X6}", Math.Round(((argb >> 24) & 0xFF) / 255.0, 3));
    }

    private static Dictionary<string, double> Sliders(JsonObject preset)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (preset["morphs"]?["default"]?["morphs"] is JsonArray vanilla)
            for (var i = 0; i < Math.Min(vanilla.Count, VanillaMorphs.Length); i++)
                result[VanillaMorphs[i]] = vanilla[i]?.GetValue<double>() ?? 0;
        if (preset["morphs"]?["default"]?["presets"] is JsonArray presets)
            foreach (var (name, index) in VanillaPresets)
                if (index < presets.Count && presets[index] is JsonValue pv && pv.TryGetValue<long>(out var n)) result[name] = n;
        if (preset["morphs"]?["custom"] is JsonArray custom)
            foreach (var entry in custom.OfType<JsonObject>())
                if (entry["name"]?.GetValue<string>() is string n) result[n] = entry["value"]?.GetValue<double>() ?? 0;
        return result;
    }

    private static List<object> SculptSummary(JsonObject preset)
    {
        var divisor = preset["morphs"]?["sculptDivisor"]?.GetValue<double>() ?? 10000;
        var hosts = new List<object>();
        if (preset["morphs"]?["sculpt"] is not JsonArray sculpt) return hosts;
        foreach (var host in sculpt.OfType<JsonObject>())
        {
            var data = host["data"] as JsonArray ?? new JsonArray();
            double max = 0;
            foreach (var row in data.OfType<JsonArray>())
            {
                if (row.Count < 4) continue;
                double dx = row[1]!.GetValue<double>(), dy = row[2]!.GetValue<double>(), dz = row[3]!.GetValue<double>();
                max = Math.Max(max, Math.Sqrt(dx * dx + dy * dy + dz * dz) / divisor);
            }
            hosts.Add(new
            {
                host = host["host"]?.GetValue<string>(),
                meshVertices = host["vertices"]?.GetValue<int>(),
                sculptedVertices = data.Count,
                largestOffset = Math.Round(max, 4),
            });
        }
        return hosts;
    }

    private static object Describe(PresetFile file, JsonObject preset, bool allSliders)
    {
        var sliders = Sliders(preset);
        var tints = (preset["tintInfo"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(t =>
        {
            var (_, hex, alpha) = Tint(t["color"]);
            return new { index = t["index"]?.GetValue<int>(), texture = t["texture"]?.GetValue<string>(), color = hex, alpha };
        }).ToList();
        return new
        {
            preset = $"{file.Kind}/{file.Name}",
            file = file.Path,
            vanillaMorphs = VanillaMorphs.ToDictionary(n => n, n => sliders.GetValueOrDefault(n)),
            vanillaPresets = VanillaPresets.ToDictionary(p => p.Name, p => sliders.GetValueOrDefault(p.Name)),
            sliderCount = sliders.Count - VanillaMorphs.Length - VanillaPresets.Length,
            sliders = sliders.Where(kv => !VanillaMorphs.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)
                                          && !VanillaPresets.Any(p => p.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                                          && (allSliders || Math.Abs(kv.Value) > 1e-6))
                             .OrderByDescending(kv => Math.Abs(kv.Value))
                             .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 4)),
            slidersNote = allSliders ? null : "Only non-zero extended sliders are listed; pass allSliders=true for every one.",
            tints = tints.Where(t => t.alpha > 0).OrderByDescending(t => t.alpha).ToList(),
            hiddenTints = tints.Count(t => t.alpha <= 0),
            headParts = preset["headParts"]?.DeepClone(),
            sculpt = SculptSummary(preset),
            bodyMorphs = (preset["bodyMorphs"] as JsonArray)?.Count ?? 0,
            transforms = (preset["transforms"] as JsonArray)?.Count ?? 0,
            actor = preset["actor"]?.DeepClone(),
        };
    }

    [McpServerTool]
    [Description("Read a face: the 18 vanilla face morphs and 3 vanilla presets, RaceMenu's extended sliders by name (CME_*, NSK_*, " +
        "EFM_* and so on), makeup tints with colour and alpha (0-1), head parts, and a sculpt summary per mesh (vertices sculpted, " +
        "largest offset). Without name it reads the player's LIVE face (saved to CharGen\\Presets\\_SkyLink\\_current first); " +
        "with name it reads that preset file (Presets or Exported, subfolders as 'MyFolder/Name'). allSliders lists zero sliders too. " +
        "A face with a full-head sculpt reacts to sliders less predictably: change one thing at a time.")]
    public async Task<object> GetFacePreset(string? name = null, bool allSliders = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            if (await RaceMenuOpenError() is string open) return new { error = open };
            var live = await SaveLivePreset($"{WorkFolder}/_current");
            return Describe(live, ReadJslot(live.Path), allSliders);
        }
        var file = ResolvePreset(name);
        return Describe(file, ReadJslot(file.Path), allSliders);
    }

    [McpServerTool]
    [Description("Compare two RaceMenu presets (or a preset and the live face: pass 'live' as either name): sliders that differ, " +
        "tints that differ, head parts added or removed, and how many sculpted vertices differ per mesh.")]
    public async Task<object> DiffFacePresets(string a, string b)
    {
        async Task<(PresetFile File, JsonObject Json)> Load(string n)
        {
            if (n.Trim().Equals("live", StringComparison.OrdinalIgnoreCase))
            {
                if (await RaceMenuOpenError() is string open) throw new McpException(open);
                var live = await SaveLivePreset($"{WorkFolder}/_current");
                return (live, ReadJslot(live.Path));
            }
            var f = ResolvePreset(n);
            return (f, ReadJslot(f.Path));
        }
        var (fa, ja) = await Load(a);
        var (fb, jb) = await Load(b);

        var sa = Sliders(ja); var sb = Sliders(jb);
        var sliders = sa.Keys.Union(sb.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(k => (k, va: sa.GetValueOrDefault(k), vb: sb.GetValueOrDefault(k)))
            .Where(x => Math.Abs(x.va - x.vb) > 1e-4)
            .OrderByDescending(x => Math.Abs(x.va - x.vb))
            .Select(x => new { slider = x.k, a = Math.Round(x.va, 4), b = Math.Round(x.vb, 4) }).ToList();

        Dictionary<int, JsonObject> Tints(JsonObject j) => (j["tintInfo"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(t => t["index"] != null).GroupBy(t => t["index"]!.GetValue<int>()).ToDictionary(g => g.Key, g => g.First());
        var ta = Tints(ja); var tb = Tints(jb);
        var tints = ta.Keys.Union(tb.Keys).OrderBy(i => i).Select(i =>
        {
            var (argbA, hexA, alphaA) = Tint(ta.GetValueOrDefault(i)?["color"]);
            var (argbB, hexB, alphaB) = Tint(tb.GetValueOrDefault(i)?["color"]);
            return (i, argbA, argbB, texture: (ta.GetValueOrDefault(i) ?? tb.GetValueOrDefault(i))?["texture"]?.GetValue<string>(), hexA, alphaA, hexB, alphaB);
        }).Where(x => x.argbA != x.argbB)
          .Select(x => new { index = x.i, x.texture, a = $"{x.hexA} @ {x.alphaA}", b = $"{x.hexB} @ {x.alphaB}" }).ToList();

        HashSet<string> Parts(JsonObject j) => (j["headParts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(p => p["formIdentifier"]?.ToString() ?? p["formId"]?.ToString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pa = Parts(ja); var pb = Parts(jb);

        Dictionary<string, Dictionary<int, (long, long, long)>> Sculpt(JsonObject j) =>
            (j["morphs"]?["sculpt"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Where(h => h["host"] != null)
                .GroupBy(h => h["host"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (g.First()["data"] as JsonArray ?? new JsonArray()).OfType<JsonArray>()
                    .Where(r => r.Count >= 4)
                    .GroupBy(r => r[0]!.GetValue<int>())
                    .ToDictionary(r => r.Key, r => (r.First()[1]!.GetValue<long>(), r.First()[2]!.GetValue<long>(), r.First()[3]!.GetValue<long>())),
                    StringComparer.OrdinalIgnoreCase);
        var ka = Sculpt(ja); var kb = Sculpt(jb);
        var sculpt = ka.Keys.Union(kb.Keys, StringComparer.OrdinalIgnoreCase).Select(h =>
        {
            var da = ka.GetValueOrDefault(h) ?? new(); var db = kb.GetValueOrDefault(h) ?? new();
            var changed = da.Keys.Union(db.Keys).Count(v => da.GetValueOrDefault(v) != db.GetValueOrDefault(v));
            return new { host = h, verticesA = da.Count, verticesB = db.Count, verticesDiffering = changed };
        }).Where(x => x.verticesDiffering > 0).ToList();

        return new
        {
            a = $"{fa.Kind}/{fa.Name}",
            b = $"{fb.Kind}/{fb.Name}",
            sliders,
            tints,
            headPartsOnlyInA = pa.Except(pb).ToList(),
            headPartsOnlyInB = pb.Except(pa).ToList(),
            sculpt,
            note = "Sculpt entries with a zero offset are dropped when RaceMenu resaves, so small vertex-count differences can be harmless.",
        };
    }

    // ---- changing the live face ----

    /// <summary>Saves the live face, lets the caller edit the JSON, writes it as _SkyLink/_edit and loads it.</summary>
    private async Task<(bool Loaded, string? Backup, string Edited)> RoundTrip(Func<JsonObject, bool> edit, bool dryRun)
    {
        if (await RaceMenuOpenError() is string open) throw new McpException(open);

        string? backupMade = null;
        if (_sessionBackup == null && !dryRun)
        {
            var name = $"{WorkFolder}/backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            await SaveLivePreset(name);
            _sessionBackup = backupMade = name;
        }

        var live = await SaveLivePreset($"{WorkFolder}/_current");
        var preset = ReadJslot(live.Path);
        if (!edit(preset) || dryRun) return (false, backupMade, live.Path);

        var editedPath = Path.Combine(Path.GetDirectoryName(live.Path)!, "_edit.jslot");
        WriteJslot(editedPath, preset);
        var loaded = await LoadPresetsTabPreset($"{WorkFolder}/_edit");
        return (loaded, backupMade, editedPath);
    }

    [McpServerTool]
    [Description("Change face sliders on the player's live face, several at once: sliders is a map of slider name to value, " +
        "e.g. {\"NSK_EyelidOpen\": -0.3, \"eyesInOut\": 0.2}. Names: the vanilla morphs (noseLongShort ... eyesForwardBack, -1 to 1), " +
        "presetNose/presetEyes/presetMouth (whole numbers), or any RaceMenu extended slider by its name as GetFacePreset lists it. " +
        "Values beyond a slider's menu range are accepted. How it works: RaceMenu has no script call for one slider, so the live face " +
        "is saved as a preset, edited, and loaded back with RaceMenu's preset loader (about a second). The first change in a session " +
        "first saves a backup (CharGen\\Presets\\_SkyLink\\backup_<time>; restore with LoadRaceMenuPreset). Refused while RaceMenu is open. " +
        "Reports each slider's previous value. dryRun=true shows the changes without applying them. Take a screenshot to judge the result.")]
    public async Task<object> SetFaceSliders(Dictionary<string, double> sliders, bool dryRun = false)
    {
        if (sliders == null || sliders.Count == 0) return new { error = "sliders is empty: pass a map of slider name to value" };
        var changes = new List<object>();
        var unknown = new List<string>();

        var (loaded, backup, file) = await RoundTrip(preset =>
        {
            var morphs = preset["morphs"] as JsonObject ?? new JsonObject();
            preset["morphs"] = morphs;
            var def = morphs["default"] as JsonObject ?? new JsonObject();
            morphs["default"] = def;
            var vanilla = def["morphs"] as JsonArray ?? new JsonArray();
            def["morphs"] = vanilla;
            while (vanilla.Count < 19) vanilla.Add(0.0);
            var presets = def["presets"] as JsonArray;
            var custom = morphs["custom"] as JsonArray ?? new JsonArray();
            morphs["custom"] = custom;

            foreach (var (rawName, value) in sliders)
            {
                var name = rawName.Trim();
                var vi = Array.FindIndex(VanillaMorphs, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (vi >= 0)
                {
                    var before = vanilla[vi]?.GetValue<double>() ?? 0;
                    vanilla[vi] = value;
                    changes.Add(new { slider = VanillaMorphs[vi], kind = "vanilla morph", previous = Math.Round(before, 4), value,
                                      note = Math.Abs(value) > 1 ? "beyond the menu range (-1 to 1)" : null });
                    continue;
                }
                var vp = Array.FindIndex(VanillaPresets, p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (vp >= 0)
                {
                    if (presets == null) { unknown.Add(name + " (the preset has no vanilla presets list)"); continue; }
                    var index = VanillaPresets[vp].Index;
                    var before = presets[index]?.GetValue<long>() ?? 0;
                    presets[index] = (long)Math.Round(value);
                    changes.Add(new { slider = VanillaPresets[vp].Name, kind = "vanilla preset", previous = (double)before, value = Math.Round(value), note = (string?)null });
                    continue;
                }
                var entry = custom.OfType<JsonObject>().FirstOrDefault(e =>
                    string.Equals(e["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    custom.Add(new JsonObject { ["name"] = name, ["value"] = value });
                    unknown.Add(name);
                    changes.Add(new { slider = name, kind = "extended (added)", previous = 0.0, value,
                                      note = (string?)"not in the live face's slider list: RaceMenu ignores names it doesn't know, so check the spelling" });
                }
                else
                {
                    var before = entry["value"]?.GetValue<double>() ?? 0;
                    entry["value"] = value;
                    changes.Add(new { slider = entry["name"]!.GetValue<string>(), kind = "extended", previous = Math.Round(before, 4), value, note = (string?)null });
                }
            }
            return true;
        }, dryRun);

        if (loaded) await NotifyInGame("Face sliders changed");
        return new
        {
            applied = loaded,
            dryRun,
            changes,
            unknownSliders = unknown.Count > 0 ? unknown : null,
            backup,
            restoreWith = _sessionBackup != null ? $"LoadRaceMenuPreset('{_sessionBackup}')" : null,
            file,
            error = !dryRun && !loaded ? "CharGen.LoadCharacterPreset returned false: the face is unchanged." : null,
        };
    }

    [McpServerTool]
    [Description("List or change the player's face makeup tints (the RaceMenu tint layers: eye sockets, eyeshadow, eyeliner, lips, " +
        "cheeks, warpaint, dirt...). Without index it lists every layer with its texture, colour and alpha. With index it sets that " +
        "layer's colour ('#RRGGBB', optional) and/or alpha (0 = off to 1 = full, optional) on the live face, by the same save, edit and " +
        "load round trip as SetFaceSliders, which also rebuilds the face-tint texture. Refused while RaceMenu is open. Reports the previous value.")]
    public async Task<object> SetFaceTint(int? index = null, string? color = null, double? alpha = null, bool dryRun = false)
    {
        if (index == null)
        {
            if (await RaceMenuOpenError() is string open) return new { error = open };
            var live = await SaveLivePreset($"{WorkFolder}/_current");
            var list = (ReadJslot(live.Path)["tintInfo"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(t =>
            {
                var (_, hex, a) = Tint(t["color"]);
                return new { index = t["index"]?.GetValue<int>(), texture = t["texture"]?.GetValue<string>(), color = hex, alpha = a };
            }).ToList();
            return new { tints = list, note = "Pass index with color and/or alpha to change one layer." };
        }

        uint? rgb = null;
        if (color != null)
        {
            var hex = color.Trim().TrimStart('#');
            if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var parsed))
                return new { error = "color must be '#RRGGBB'" };
            rgb = parsed;
        }
        if (alpha is < 0 or > 1) return new { error = "alpha must be between 0 and 1" };
        if (rgb == null && alpha == null) return new { error = "pass color and/or alpha" };

        object? change = null;
        string? missing = null;
        var (loaded, backup, file) = await RoundTrip(preset =>
        {
            var layer = (preset["tintInfo"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .FirstOrDefault(t => t["index"]?.GetValue<int>() == index);
            if (layer == null) { missing = $"No tint layer with index {index}. Call SetFaceTint without index to list them."; return false; }
            var (argb, hexBefore, alphaBefore) = Tint(layer["color"]);
            var newAlpha = alpha.HasValue ? (uint)Math.Round(alpha.Value * 255) : (argb >> 24) & 0xFF;
            var newRgb = rgb ?? (argb & 0xFFFFFF);
            var newArgb = (newAlpha << 24) | newRgb;
            layer["color"] = (long)newArgb;
            change = new
            {
                index,
                texture = layer["texture"]?.GetValue<string>(),
                previous = new { color = hexBefore, alpha = alphaBefore },
                value = new { color = $"#{newRgb:X6}", alpha = Math.Round(newAlpha / 255.0, 3) },
            };
            return true;
        }, dryRun);

        if (missing != null) return new { error = missing };
        if (loaded) await NotifyInGame("Face tint changed");
        return new
        {
            applied = loaded,
            dryRun,
            change,
            backup,
            restoreWith = _sessionBackup != null ? $"LoadRaceMenuPreset('{_sessionBackup}')" : null,
            file,
            error = !dryRun && !loaded ? "CharGen.LoadCharacterPreset returned false: the face is unchanged." : null,
        };
    }
}
