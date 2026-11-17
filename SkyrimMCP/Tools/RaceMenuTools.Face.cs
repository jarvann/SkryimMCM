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

    /// <summary>
    /// Applies a Presets-tab preset to the player with RaceMenu's own preset loader, then repairs what that
    /// loader leaves undone outside the menu (both seen in game 2026-11-16, explained by skee's source):
    /// ApplyPresetData resets the head parts to the race defaults and only re-adds parts with a sex flag and a
    /// valid-race list, so others (an ears part) are dropped; and the menu rebuilds the face-tint texture
    /// itself afterwards, which a script call doesn't, so the face shows purple.
    /// </summary>
    private async Task<LoadOutcome> LoadPresetsTabPreset(string clean, string path, string? hairColorForm = null)
    {
        var parts = PresetHeadParts(ReadJslot(path));
        // Face edits pass no hair colour form: skee writes the preset's hair RGB into the form it is given, and the
        // player's hair colour form can be shared with NPCs. Loading a saved look passes RaceMenu's own custom
        // hair colour form (BUG-043), so the look's hair colour comes back too.
        var result = await Papyrus("CharGen", "LoadCharacterPreset", "player", PapyrusPresetName(clean),
            hairColorForm == null ? null : JsonValue.Create(hairColorForm));
        if (result?.GetValueKind() != JsonValueKind.True) return new LoadOutcome(false, null, null);
        // Rebuild the tint at once to shorten the purple flash, then again after the head parts are back
        // (their 3D reset drops the tint again).
        await RefreshTints();
        var (restored, tints) = await RepairAfterLoad(parts);
        return new LoadOutcome(true, restored, tints);
    }

    /// <summary>
    /// RaceMenu's own custom hair colour form (from its plugin), which skee writes a preset's hair colour into. Null when
    /// it can't be found.
    /// </summary>
    private async Task<string?> RaceMenuHairColorForm()
    {
        try
        {
            var data = await _pipe.SendRequestAsync("list_hair_colors", new JsonObject { ["includeNonPlayable"] = true });
            return (data?["colors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .FirstOrDefault(c => c["mod"]?.GetValue<string>()?.Contains("RaceMenu", StringComparison.OrdinalIgnoreCase) == true)
                ?["formId"]?.GetValue<string>();
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// BUG-045: saves the live face as a work preset before a cell change (studio trip), so its makeup can be checked
    /// and put back afterwards: once, after a trip, four tints came back as the character's old makeup.
    /// </summary>
    internal async Task<string> SnapshotFace(string name)
    {
        var clean = $"{WorkFolder}/{name}";
        await SaveLivePreset(clean);
        return clean;
    }

    /// <summary>Compares the live tints with a snapshot and, if any changed, loads the snapshot back. Returns what changed.</summary>
    internal async Task<JsonObject> RestoreTintsIfChanged(string snapshot)
    {
        var path = PresetPath(snapshot);
        if (path == null || !File.Exists(path)) return new JsonObject { ["checked"] = false, ["note"] = "no snapshot" };
        var before = TintMap(ReadJslot(path));
        var live = await SaveLivePreset($"{WorkFolder}/_current");
        var now = TintMap(ReadJslot(live.Path));
        var changed = new JsonArray();
        foreach (var (index, color) in before)
        {
            now.TryGetValue(index, out var current);
            if (current != color) changed.Add(new JsonObject { ["index"] = index, ["before"] = $"{color:X8}", ["after"] = $"{current:X8}" });
        }
        if (changed.Count == 0) return new JsonObject { ["checked"] = true, ["changed"] = 0 };
        var outcome = await LoadPresetsTabPreset(snapshot, path);
        return new JsonObject
        {
            ["checked"] = true,
            ["changed"] = changed.Count,
            ["tints"] = changed,
            ["restored"] = outcome.Loaded,
            ["note"] = "Makeup tints changed across the cell change; the face from before the move was loaded back.",
        };
    }

    private static Dictionary<int, uint> TintMap(JsonObject preset) =>
        (preset["tintInfo"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(t => t["index"] != null)
            .GroupBy(t => t["index"]!.GetValue<int>())
            .ToDictionary(g => g.Key, g => (uint)(g.First()["color"]?.GetValue<long>() ?? 0));

    private static string? PresetPath(string clean)
    {
        var (game, _) = SkyLinkSettings.SkyrimPath();
        return game == null ? null
            : Path.Combine(game, "Data", "SKSE", "Plugins", "CharGen", "Presets", clean.Replace('/', Path.DirectorySeparatorChar) + ".jslot");
    }

    private sealed record LoadOutcome(bool Loaded, JsonNode? HeadParts, string Tints);

    private static JsonArray PresetHeadParts(JsonObject preset) => new((preset["headParts"] as JsonArray ?? new JsonArray())
        .OfType<JsonObject>()
        .Select(p => p["formIdentifier"]?.GetValue<string>())
        .Where(id => !string.IsNullOrEmpty(id))
        .Select(id => (JsonNode?)JsonValue.Create(id))
        .ToArray());

    /// <summary>Re-adds the preset's head parts the character is missing, then rebuilds the face tint.</summary>
    private async Task<(JsonNode? HeadParts, string Tints)> RepairAfterLoad(JsonArray? parts)
    {
        JsonNode? restored = null;
        if (parts != null && parts.Count > 0)
        {
            restored = await _pipe.SendRequestAsync("restore_head_parts", new JsonObject { ["parts"] = parts, ["refresh"] = true });
            if (restored?["added"] is JsonArray added && added.Count > 0) await Task.Delay(250);
        }
        return (restored, await RefreshTints());
    }

    /// <summary>SKSE's Game.UpdateTintMaskColors rebuilds the player's face-tint texture from the tint layers.</summary>
    private async Task<string> RefreshTints()
    {
        try
        {
            await Papyrus("Game", "UpdateTintMaskColors");
            // BUG-039: twice in game the makeup was missing after an edit (pale lips, no blush) until RepairFace ran,
            // as if the first rebuild came before RaceMenu had finished applying the layers. Rebuild again shortly after.
            await Task.Delay(400);
            await Papyrus("Game", "UpdateTintMaskColors");
            // BUG-034: not Game.UpdateHairColor, which looks to leave the hair rendering at the stored colour (half as bright
            // as the game draws it: the hair went brown until the next cell change). Set the hair and brow shaders
            // to what the game itself uses, then put back a brow colour set with SetBrowColor.
            await _pipe.SendRequestAsync("head_part_tint", new JsonObject { ["type"] = 3, ["fromHairColor"] = true });
            await _pipe.SendRequestAsync("head_part_tint", new JsonObject { ["type"] = 6, ["fromHairColor"] = true });
            await ReapplyBrowColor();
            var store = await SkyrimMCP.Face.FaceState.RaceMenuSaveTints(_pipe);
            return "Face tint rebuilt (Game.UpdateTintMaskColors; hair and brow shaders set as the game draws them). " + store + ".";
        }
        catch (McpException ex)
        {
            return "Couldn't rebuild the face tint: " + ex.Message + ". If the face is purple, reload the preset from RaceMenu's Presets tab.";
        }
    }

    /// <summary>Saves the live face once per session before the first change or load, so it can be put back.</summary>
    private async Task<string?> EnsureSessionBackup()
    {
        if (_sessionBackup != null) return null;
        var name = $"{WorkFolder}/backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        await SaveLivePreset(name);
        return _sessionBackup = name;
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

    private static object Describe(PresetFile file, JsonObject preset, bool allSliders, double minValue)
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
                                          && (allSliders || Math.Abs(kv.Value) >= minValue))
                             .OrderByDescending(kv => Math.Abs(kv.Value))
                             .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 4)),
            slidersNote = allSliders ? null : $"Extended sliders below {minValue} (either way) aren't listed; lower minValue or pass allSliders=true for every one.",
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
        "with name it reads that preset file (Presets or Exported, subfolders as 'MyFolder/Name'). Extended sliders nearer zero than " +
        "minValue (default 0.02) are left out; allSliders lists every one. " +
        "A face with a full-head sculpt reacts to sliders less predictably: change one thing at a time.")]
    public async Task<object> GetFacePreset(string? name = null, bool allSliders = false, double minValue = 0.02)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            if (await RaceMenuOpenError() is string open) return new { error = open };
            var live = await SaveLivePreset($"{WorkFolder}/_current");
            return Describe(live, ReadJslot(live.Path), allSliders, minValue);
        }
        var file = ResolvePreset(name);
        return Describe(file, ReadJslot(file.Path), allSliders, minValue);
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
    private async Task<(LoadOutcome? Outcome, string? Backup, string Edited)> RoundTrip(Func<JsonObject, bool> edit, bool dryRun)
    {
        if (await RaceMenuOpenError() is string open) throw new McpException(open);
        var backupMade = dryRun ? null : await EnsureSessionBackup();

        var live = await SaveLivePreset($"{WorkFolder}/_current");
        var preset = ReadJslot(live.Path);
        if (!edit(preset) || dryRun) return (null, backupMade, live.Path);

        var editedPath = Path.Combine(Path.GetDirectoryName(live.Path)!, "_edit.jslot");
        WriteJslot(editedPath, preset);
        var outcome = await LoadPresetsTabPreset($"{WorkFolder}/_edit", editedPath);
        Interlocked.Increment(ref SkyrimMCP.Face.FaceState.FaceEdits);
        return (outcome, backupMade, editedPath);
    }

    private static HashSet<string>? _knownSliders;

    /// <summary>
    /// Slider names the loose RaceMenu slider files define (meshes\actors\character\FaceGenMorphs\*\sliders\*.ini:
    /// "Name = 128, Slider, MorphDown, MorphUp"), plus every extended-slider name saved in a CharGen preset.
    /// </summary>
    private static HashSet<string> KnownSliderNames()
    {
        if (_knownSliders != null) return _knownSliders;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (game, _) = SkyLinkSettings.SkyrimPath();
        var root = game == null ? null : Path.Combine(game, "Data", "meshes", "actors", "character", "FaceGenMorphs");
        try
        {
            if (root != null && Directory.Exists(root))
            {
                foreach (var ini in Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories))
                {
                    if (!Path.GetDirectoryName(ini)!.EndsWith("sliders", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var line in File.ReadLines(ini))
                    {
                        var eq = line.IndexOf('=');
                        if (eq <= 0 || line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith(';')) continue;
                        var name = line[..eq].Trim();
                        if (name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_')) names.Add(name);
                    }
                }
            }
        }
        catch { /* unreadable folder: fewer known names */ }

        // Sliders packed in BSAs (CME_, NSK_...) have no loose ini, but every preset that set one names it
        // (BUG-031): collect the extended-slider names from all .jslot files under CharGen.
        var chargen = game == null ? null : Path.Combine(game, "Data", "SKSE", "Plugins", "CharGen");
        try
        {
            if (chargen != null && Directory.Exists(chargen))
            {
                var work = Path.DirectorySeparatorChar + WorkFolder + Path.DirectorySeparatorChar;
                foreach (var jslot in Directory.EnumerateFiles(chargen, "*.jslot", SearchOption.AllDirectories).Take(2000))
                {
                    // SkyLink's own working presets can hold a mistyped name sent to SetFaceSliders.
                    if (jslot.Contains(work, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (JsonNode.Parse(File.ReadAllText(jslot))?["morphs"]?["custom"] is not JsonArray custom) continue;
                        foreach (var entry in custom.OfType<JsonObject>())
                            if (entry["name"]?.GetValue<string>() is { Length: > 0 } name) names.Add(name);
                    }
                    catch { /* a broken preset: skip it */ }
                }
            }
        }
        catch { /* unreadable folder: fewer known names */ }
        return _knownSliders = names;
    }

    [McpServerTool]
    [Description("Change face sliders on the player's live face, several at once: sliders is a map of slider name to value, " +
        "e.g. {\"NSK_EyelidOpen\": -0.3, \"eyesInOut\": 0.2}. Names: the vanilla morphs (noseLongShort ... eyesForwardBack, -1 to 1), " +
        "presetNose/presetEyes/presetMouth (whole numbers), or any RaceMenu extended slider by its name as GetFacePreset lists it. " +
        "Values beyond a slider's menu range are accepted. How it works: RaceMenu has no script call for one slider, so the live face " +
        "is saved as a preset, edited, and loaded back with RaceMenu's preset loader (about a second; the face can flash purple for a " +
        "moment before its tint is rebuilt, which is normal). The first change in a session " +
        "first saves a backup (CharGen\\Presets\\_SkyLink\\backup_<time>; restore with LoadRaceMenuPreset). Refused while RaceMenu is open. " +
        "Reports each slider's previous value. dryRun=true shows the changes without applying them. Take a screenshot to judge the result.")]
    public async Task<object> SetFaceSliders(Dictionary<string, double> sliders, bool dryRun = false)
    {
        if (sliders == null || sliders.Count == 0) return new { error = "sliders is empty: pass a map of slider name to value" };
        var changes = new List<object>();
        var unknown = new List<string>();

        var (outcome, backup, file) = await RoundTrip(preset => ApplySliders(preset, sliders, changes, unknown), dryRun);

        var loaded = outcome?.Loaded == true;
        if (loaded) await NotifyInGame("Face sliders changed");
        return new
        {
            applied = loaded,
            headParts = outcome?.HeadParts?.DeepClone(),
            tints = outcome?.Tints,
            dryRun,
            changes,
            notInSliderFiles = unknown.Count > 0 ? unknown : null,
            backup,
            restoreWith = _sessionBackup != null ? $"LoadRaceMenuPreset('{_sessionBackup}')" : null,
            file,
            error = !dryRun && !loaded ? "CharGen.LoadCharacterPreset returned false: the face is unchanged." : null,
        };
    }

    [McpServerTool]
    [Description("Change several things on the player's face at once, with a single preset load (one purple flash, not one per " +
        "change): sliders as SetFaceSliders (a map of slider name to value), tints as a map of tint index to '#RRGGBB', " +
        "'#RRGGBB@alpha' or '@alpha' (alpha 0-1; SetFaceTint without index lists the layers), and headParts as a map of type " +
        "(hair, eyes, eyebrows, facialHair, scar) to a part's editor ID or FormID, applied right after the load. dryRun shows the " +
        "slider and tint changes without applying them. Same session backup and restore as the other face tools.")]
    public async Task<object> SetFace(Dictionary<string, double>? sliders = null, Dictionary<string, string>? tints = null,
        Dictionary<string, string>? headParts = null, bool dryRun = false)
    {
        if ((sliders == null || sliders.Count == 0) && (tints == null || tints.Count == 0) && (headParts == null || headParts.Count == 0))
            return new { error = "Nothing to change: pass sliders, tints and/or headParts." };
        var changes = new List<object>();
        var unknown = new List<string>();
        var tintChanges = new List<object>();
        var problems = new List<string>();

        // Parse the tints first, so a typo doesn't half-apply the face.
        var tintEdits = new List<(int Index, uint? Rgb, double? Alpha)>();
        foreach (var (key, value) in tints ?? new Dictionary<string, string>())
        {
            if (!int.TryParse(key.Trim(), out var index)) return new { error = $"tints: '{key}' is not a tint index" };
            var text = value.Trim();
            uint? rgb = null;
            double? alpha = null;
            var at = text.IndexOf('@');
            var colorPart = at >= 0 ? text[..at] : text;
            if (at >= 0)
            {
                if (!double.TryParse(text[(at + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) || a < 0 || a > 1)
                    return new { error = $"tints[{key}]: alpha after @ must be 0 to 1" };
                alpha = a;
            }
            if (colorPart.Length > 0)
            {
                var hex = colorPart.TrimStart('#');
                if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var parsed))
                    return new { error = $"tints[{key}]: colour must be #RRGGBB" };
                rgb = parsed;
            }
            if (rgb == null && alpha == null) return new { error = $"tints[{key}]: give #RRGGBB, @alpha or both" };
            tintEdits.Add((index, rgb, alpha));
        }

        LoadOutcome? outcome = null;
        string? backup = null, file = null;
        if ((sliders?.Count ?? 0) > 0 || tintEdits.Count > 0)
        {
            (outcome, backup, file) = await RoundTrip(preset =>
            {
                if (sliders != null && sliders.Count > 0) ApplySliders(preset, sliders, changes, unknown);
                foreach (var (index, rgb, alpha) in tintEdits)
                {
                    var layer = (preset["tintInfo"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                        .FirstOrDefault(t => t["index"]?.GetValue<int>() == index);
                    if (layer == null) { problems.Add($"No tint layer {index}: skipped."); continue; }
                    var (argb, hexBefore, alphaBefore) = Tint(layer["color"]);
                    var newAlpha = alpha.HasValue ? (uint)Math.Round(alpha.Value * 255) : (argb >> 24) & 0xFF;
                    var newRgb = rgb ?? (argb & 0xFFFFFF);
                    layer["color"] = (long)((newAlpha << 24) | newRgb);
                    tintChanges.Add(new
                    {
                        index,
                        texture = layer["texture"]?.GetValue<string>(),
                        previous = new { color = hexBefore, alpha = alphaBefore },
                        value = new { color = $"#{newRgb:X6}", alpha = Math.Round(newAlpha / 255.0, 3) },
                    });
                }
                return true;
            }, dryRun);
        }

        var parts = new List<object>();
        if (!dryRun)
        {
            foreach (var (type, part) in headParts ?? new Dictionary<string, string>())
            {
                var reply = await _pipe.SendRequestAsync("set_head_part", new JsonObject { ["part"] = part.Trim(), ["type"] = type.Trim(), ["index"] = -1, ["refresh"] = false });
                parts.Add(new { type, part, result = reply?.DeepClone() });
            }
            if (parts.Count > 0) await RefreshTints();
        }
        var loaded = outcome?.Loaded;
        if (loaded == true || parts.Count > 0) await NotifyInGame("Face changed");
        return new
        {
            applied = dryRun ? (bool?)null : (loaded ?? true),
            dryRun,
            sliders = changes.Count > 0 ? changes : null,
            notInSliderFiles = unknown.Count > 0 ? unknown : null,
            tints = tintChanges.Count > 0 ? tintChanges : null,
            headParts = parts.Count > 0 ? parts : null,
            problems = problems.Count > 0 ? problems : null,
            tintRefresh = outcome?.Tints,
            backup,
            restoreWith = _sessionBackup != null ? $"LoadRaceMenuPreset('{_sessionBackup}')" : null,
            file,
            error = !dryRun && loaded == false ? "CharGen.LoadCharacterPreset returned false: the face is unchanged." : null,
        };
    }

    /// <summary>The slider edits of SetFaceSliders, on a preset (also used by SetFace).</summary>
    private static bool ApplySliders(JsonObject preset, Dictionary<string, double> sliders, List<object> changes, List<string> unknown)
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
                // RaceMenu doesn't save sliders at 0, so a missing name is usually a slider at 0. Only a
                // name no slider file and no saved preset knows is suspect.
                custom.Add(new JsonObject { ["name"] = name, ["value"] = value });
                var known = KnownSliderNames();
                var defined = known.Contains(name);
                if (!defined && known.Count > 0) unknown.Add(name);
                changes.Add(new { slider = name, kind = "extended", previous = 0.0, value,
                                  note = defined ? null
                                       : (string?)"Unknown name: no loose slider file and no saved RaceMenu preset has it. It may still " +
                                         "be a real slider at 0 from a BSA that no preset has used yet; RaceMenu ignores names it doesn't " +
                                         "know, so check the spelling if nothing changes." });
            }
            else
            {
                var before = entry["value"]?.GetValue<double>() ?? 0;
                entry["value"] = value;
                changes.Add(new { slider = entry["name"]!.GetValue<string>(), kind = "extended", previous = Math.Round(before, 4), value, note = (string?)null });
            }
        }
        return true;
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
        var (outcome, backup, file) = await RoundTrip(preset =>
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
        var loaded = outcome?.Loaded == true;
        if (loaded) await NotifyInGame("Face tint changed");
        return new
        {
            applied = loaded,
            headParts = outcome?.HeadParts?.DeepClone(),
            tints = outcome?.Tints,
            dryRun,
            change,
            backup,
            restoreWith = _sessionBackup != null ? $"LoadRaceMenuPreset('{_sessionBackup}')" : null,
            file,
            error = !dryRun && !loaded ? "CharGen.LoadCharacterPreset returned false: the face is unchanged." : null,
        };
    }

    [McpServerTool]
    [Description("Repair the player's face after a preset load went wrong: rebuilds the face-tint texture (fixes a purple face) and, " +
        "given a preset name, puts back any head parts from that preset the player is missing (RaceMenu's script loader drops parts " +
        "with no sex flag or valid-race list, such as some ears). Refused while RaceMenu is open.")]
    public async Task<object> RepairFace(string? preset = null)
    {
        if (await RaceMenuOpenError() is string open) return new { error = open };
        JsonArray? parts = null;
        string? source = null;
        if (!string.IsNullOrWhiteSpace(preset))
        {
            var file = ResolvePreset(preset);
            parts = PresetHeadParts(ReadJslot(file.Path));
            source = $"{file.Kind}/{file.Name}";
        }
        var (restored, tints) = await RepairAfterLoad(parts);
        return new { headPartsFrom = source, headParts = restored?.DeepClone(), tints, note = "Check the face with a screenshot." };
    }

    // The brow colour is stored per character (FaceState, FEAT-241) and re-applied after every tint refresh and
    // before captures, since Game.UpdateHairColor and 3D reloads put the hair colour back on the brows.

    [McpServerTool]
    [Description("Colour the player's eyebrows separately from the hair (by default the brows take the hair colour, so darkening " +
        "them otherwise means darkening the hair). color #RRGGBB, or 'hair' to go back to the hair colour. Sets the brow head " +
        "part's shader tint directly, and also stores it as a RaceMenu (NiOverride) override so it can survive a save and load. " +
        "SkyLink stores it for this character and re-applies it after each face edit and before each capture (the game puts the " +
        "hair colour back on the brows on every cell change or load, so in normal play it can revert until the next face tool " +
        "call). Changing the brow head part (SetHeadPart) needs it set again. Reports " +
        "each brow mesh's material: only hair-tint materials can be coloured; a brow whose colour is baked into its texture " +
        "can't, and a different brow head part is the way to change it. Very light or thin brow textures stay faint even with a " +
        "dark colour. Check with CaptureFace(hideHair: true).")]
    public async Task<object> SetBrowColor(string color)
    {
        var reset = color.Trim().Equals("hair", StringComparison.OrdinalIgnoreCase);
        var hex = color.Trim().TrimStart('#').ToUpperInvariant();
        var rgb = 0;
        if (!reset && (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out rgb)))
            return new { error = "color must be #RRGGBB or 'hair'" };

        var appearance = await _pipe.SendRequestAsync("get_appearance");
        var isFemale = appearance?["sex"]?.GetValue<string>() == "female";
        var brows = (appearance?["headParts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(p => p["type"]?.GetValue<int>() == 6)
            .Select(p => p["editorId"]?.GetValue<string>())
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToList();

        // RaceMenu's override, for persistence across save and load (in game it didn't change the colour by itself).
        var persisted = new List<object>();
        foreach (var node in brows)
        {
            try
            {
                if (reset) await Papyrus("NiOverride", "RemoveNodeOverride", "player", isFemale, node, KeyTintColor, -1);
                else await Papyrus("NiOverride", "AddNodeOverrideInt", "player", isFemale, node, KeyTintColor, -1, rgb, true);
                var stored = reset ? null
                    : (await Papyrus("NiOverride", "GetNodeOverrideInt", "player", isFemale, node, KeyTintColor, -1))?.GetValue<int>();
                persisted.Add(new { node, stored = stored.HasValue ? $"#{stored.Value & 0xFFFFFF:X6}" : null });
            }
            catch (McpException ex) { persisted.Add(new { node, error = ex.Message }); }
        }

        JsonNode? applied;
        var key = SkyrimMCP.Face.FaceState.Key(appearance);
        var browColor = reset ? null : "#" + hex;
        SkyrimMCP.Face.FaceState.SetBrowColor(key, browColor);
        if (reset)
        {
            applied = await _pipe.SendRequestAsync("head_part_tint", new JsonObject { ["type"] = 6, ["fromHairColor"] = true });
        }
        else
        {
            applied = await _pipe.SendRequestAsync("head_part_tint", new JsonObject { ["type"] = 6, ["color"] = browColor });
        }
        if (applied?["error"] is JsonNode error) return new { error = error.ToString(), persisted };
        await NotifyInGame(reset ? "Brows follow the hair colour again" : "Brow colour set");
        return new
        {
            color = reset ? "hair" : browColor,
            storedFor = key,
            applied = applied?.DeepClone(),
            niOverride = persisted,
            next = "CaptureFace(hideHair: true) shows the brows clearly.",
        };
    }

    [McpServerTool]
    [Description("Start from a neutral head (FEAT-245): sets every face slider back to its default (the 19 vanilla morphs to 0, the " +
        "vanilla nose/eyes/mouth presets to 0, every RaceMenu extended slider removed) and removes the sculpt, so slider responses " +
        "are predictable when building a new face. Keeps head parts (hair, eyes, brows), makeup tints and colours. keepSculpt=true " +
        "keeps the sculpt; keepSliders=true keeps the sliders (with keepSculpt=false: remove only the sculpt). keep: comma-separated words; " +
        "extended sliders whose name contains one stay (default 'scar': scar mods hide their scar with a slider). Backs up first like " +
        "every face edit; save a named preset before if you want to come back to this face. dryRun shows what would change.")]
    public async Task<object> ResetFace(bool keepSculpt = false, bool keepSliders = false, bool dryRun = false, string keep = "scar")
    {
        if (keepSculpt && keepSliders) return new { error = "Nothing to reset: keepSculpt and keepSliders are both true." };
        object? reset = null;
        string? keptNames = null;
        var (outcome, backup, file) = await RoundTrip(preset =>
        {
            var morphs = preset["morphs"] as JsonObject ?? new JsonObject();
            preset["morphs"] = morphs;
            var vanillaChanged = 0;
            var extendedRemoved = 0;
            var sculptHosts = 0;
            if (!keepSliders)
            {
                var def = morphs["default"] as JsonObject ?? new JsonObject();
                morphs["default"] = def;
                var vanilla = new JsonArray();
                var old = def["morphs"] as JsonArray ?? new JsonArray();
                for (var i = 0; i < 19; i++)
                {
                    if (i < old.Count && Math.Abs(old[i]?.GetValue<double>() ?? 0) > 1e-6) vanillaChanged++;
                    vanilla.Add(0.0);
                }
                def["morphs"] = vanilla;
                if (def["presets"] is JsonArray presets)
                    for (var i = 0; i < presets.Count; i++) presets[i] = 0L;
                // BUG-044: some sliders switch features off rather than shape the face (a scar mod's slider hides its
                // scar); extended sliders whose name contains any of the comma-separated 'keep' words stay.
                var keepWords = keep.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var kept = new JsonArray();
                foreach (var entry in (morphs["custom"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var name = entry["name"]?.GetValue<string>() ?? "";
                    if (keepWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) kept.Add(entry.DeepClone());
                    else extendedRemoved++;
                }
                morphs["custom"] = kept;
                keptNames = string.Join(", ", kept.OfType<JsonObject>().Select(e => e["name"]?.GetValue<string>()));
            }
            if (!keepSculpt)
            {
                sculptHosts = (morphs["sculpt"] as JsonArray)?.Count ?? 0;
                morphs["sculpt"] = new JsonArray();
            }
            reset = new { vanillaMorphsChanged = vanillaChanged, extendedSlidersRemoved = extendedRemoved, sculptedMeshesCleared = sculptHosts,
                          extendedSlidersKept = string.IsNullOrEmpty(keptNames) ? null : keptNames };
            return true;
        }, dryRun);

        var loaded = outcome?.Loaded == true;
        if (loaded) await NotifyInGame("Face reset to a neutral head");
        return new
        {
            applied = loaded,
            reset,
            headParts = outcome?.HeadParts?.DeepClone(),
            tints = outcome?.Tints,
            dryRun,
            backup,
            restoreWith = _sessionBackup != null ? $"LoadRaceMenuPreset('{_sessionBackup}')" : null,
            file,
            error = !dryRun && !loaded ? "CharGen.LoadCharacterPreset returned false: the face is unchanged." : null,
        };
    }

    [McpServerTool]
    [Description("Record what a face slider does on this character's head, so later sessions don't probe it again: note 'inert' " +
        "(no measurable effect; 'inert (detail)' also counts), or the effect found (e.g. 'nose length -8% at 0.7'). Several at once: notes is a map of slider " +
        "name to note; an empty note removes the entry. Without notes, lists what is recorded. Stored per character (name and " +
        "race) in SkyLinkAI\\face_state.json.")]
    public async Task<object> SliderNotes(Dictionary<string, string>? notes = null)
    {
        var key = SkyrimMCP.Face.FaceState.Key(await _pipe.SendRequestAsync("get_appearance"));
        if (key == null) return new { error = "Couldn't read the player's name for the character key." };
        JsonObject result = SkyrimMCP.Face.FaceState.GetSliderNotes(key);
        foreach (var (slider, note) in notes ?? new Dictionary<string, string>())
            result = SkyrimMCP.Face.FaceState.SetSliderNote(key, slider.Trim(), note);
        var inert = result.Where(kv => kv.Value?["note"]?.GetValue<string>()?.Trim().StartsWith("inert", StringComparison.OrdinalIgnoreCase) == true)
            .Select(kv => kv.Key).ToList();
        return DeserializeResponse(new JsonObject
        {
            ["character"] = key,
            ["inert"] = new JsonArray(inert.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["notes"] = result,
        });
    }

    [McpServerTool]
    [Description("Read the colour the hair and brow meshes are rendering with right now (their hair-tint shader colour), next to " +
        "the hair colour stored on the character (get_appearance hairColorFormId). Use it when the hair looks a different colour " +
        "than before: the shader colour is what shows; the stored form is what the game puts back on a cell change or load. " +
        "Read-only.")]
    public async Task<object> GetHairTint()
    {
        var hair = await _pipe.SendRequestAsync("head_part_tint", new JsonObject { ["type"] = 3 });
        var brows = await _pipe.SendRequestAsync("head_part_tint", new JsonObject { ["type"] = 6 });
        var appearance = await _pipe.SendRequestAsync("get_appearance");
        return DeserializeResponse(new JsonObject
        {
            ["hairShader"] = hair?.DeepClone(),
            ["browShader"] = brows?.DeepClone(),
            ["storedHairColorFormId"] = appearance?["hairColorFormId"]?.DeepClone(),
            ["storedHairColor"] = appearance?["hairColor"]?.DeepClone(),
            ["storedBrowColor"] = SkyrimMCP.Face.FaceState.GetBrowColor(SkyrimMCP.Face.FaceState.Key(appearance)),
        });
    }

    /// <summary>Puts the character's stored brow colour back after Game.UpdateHairColor reset it.</summary>
    private Task ReapplyBrowColor() => SkyrimMCP.Face.FaceState.ReapplyBrowColor(_pipe);
}
