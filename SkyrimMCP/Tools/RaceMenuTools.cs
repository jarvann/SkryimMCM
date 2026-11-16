using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// RaceMenu integration (FEAT-227): body morphs, overlays (tattoos, warpaint) and character presets
/// through RaceMenu's own Papyrus API (skee64.dll: the NiOverride and CharGen scripts), called with
/// the Papyrus bridge. Without RaceMenu, the native tools (SetHairColor, SetSkinTone, SetHeadPart) still work.
/// </summary>
[McpServerToolType]
public class RaceMenuTools : ToolBase
{
    public RaceMenuTools(IPipeClient pipe) : base(pipe) { }

    // Morphs SkyLink sets are stored under this key, so they can be cleared without touching
    // morphs set by other mods (each mod uses its own key).
    private const string DefaultMorphKey = "SkyLinkAI";

    // NiOverride node override keys (RaceMenu's overlay format).
    private const int KeyTintColor = 7, KeyAlpha = 8, KeyTexture = 9;
    private const string DefaultOverlayTexture = "actors\\character\\overlays\\default.dds";

    private static readonly Dictionary<string, (string Node, string CountFunction)> Areas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["body"] = ("Body", "GetNumBodyOverlays"),
        ["hands"] = ("Hands", "GetNumHandOverlays"),
        ["feet"] = ("Feet", "GetNumFeetOverlays"),
        ["face"] = ("Face", "GetNumFaceOverlays"),
    };

    private async Task<JsonNode?> Papyrus(string className, string function, params JsonNode?[] args)
    {
        var data = await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject
        {
            ["className"] = className,
            ["functionName"] = function,
            ["args"] = new JsonArray(args.Select(a => a?.DeepClone()).ToArray()),
        });
        if (data?["error"] is JsonNode error) throw new McpException($"{className}.{function}: {error}");
        return data?["result"];
    }

    private static string RefArg(string? refId) => string.IsNullOrWhiteSpace(refId) ? "player" : refId.Trim();

    private async Task<bool> IsFemale(string? refId, bool? female)
    {
        if (female.HasValue) return female.Value;
        if (RefArg(refId) != "player")
            throw new McpException("Pass female=true or false for an NPC (overlays are stored per sex).");
        var appearance = await _pipe.SendRequestAsync("get_appearance");
        return appearance?["sex"]?.GetValue<string>() == "female";
    }

    [McpServerTool]
    [Description("Check whether RaceMenu is installed and which of its script APIs SkyLink can use (NiOverride for body morphs " +
        "and overlays, CharGen for presets). Call this before the other RaceMenu tools. Without RaceMenu, use the native " +
        "appearance tools (SetHairColor, SetSkinTone, SetHeadPart).")]
    public async Task<object> GetRaceMenuStatus()
    {
        var plugins = await _pipe.SendRequestAsync("get_loaded_skse_plugins");
        var skee = (plugins?.ToJsonString() ?? "").Contains("skee", StringComparison.OrdinalIgnoreCase);

        async Task<object> Script(string name)
        {
            try
            {
                var functions = await _pipe.SendRequestAsync("get_script_functions", new JsonObject { ["className"] = name });
                return new { available = true, functions = functions?["globalFunctions"]?.AsArray().Count ?? functions?["functions"]?.AsArray().Count };
            }
            catch (McpException ex)
            {
                return new { available = false, reason = ex.Message };
            }
        }

        return new
        {
            raceMenuPluginLoaded = skee,
            niOverride = await Script("NiOverride"),
            charGen = await Script("CharGen"),
            note = skee
                ? "RaceMenu is loaded. Body morphs need a body with morph support (BodySlide-built CBBE/3BA or BHUNP)."
                : "RaceMenu (skee64.dll) isn't loaded: the RaceMenu tools won't work; the native appearance tools still do.",
        };
    }

    [McpServerTool]
    [Description("List an actor's RaceMenu body morphs (BodySlide sliders set at runtime): each morph with the keys that set it " +
        "(each mod uses its own key) and their values. Defaults to the player; refId for an NPC reference.")]
    public async Task<object> GetBodyMorphs(string? refId = null)
    {
        var target = RefArg(refId);
        var names = await Papyrus("NiOverride", "GetMorphNames", target) as JsonArray ?? new JsonArray();
        var morphs = new JsonArray();
        foreach (var name in names.Take(150).Select(n => n?.GetValue<string>()).OfType<string>())
        {
            var keys = await Papyrus("NiOverride", "GetMorphKeys", target, name) as JsonArray ?? new JsonArray();
            var values = new JsonObject();
            foreach (var key in keys.Select(k => k?.GetValue<string>()).OfType<string>())
                values[key] = (await Papyrus("NiOverride", "GetBodyMorph", target, name, key))?.DeepClone();
            morphs.Add(new JsonObject { ["morph"] = name, ["byKey"] = values });
        }
        return DeserializeResponse(new JsonObject
        {
            ["actor"] = target,
            ["count"] = names.Count,
            ["morphs"] = morphs,
            ["note"] = names.Count > 150 ? "Only the first 150 morphs are listed." : null,
        });
    }

    [McpServerTool]
    [Description("Set a RaceMenu body morph (a BodySlide slider such as Breasts, Butt, Belly, Thighs, Arms, Waist, Muscle) on an actor. " +
        "value: usually -1 to 1 (1 = the slider at 100). Stored under key (default 'SkyLinkAI'), so ClearBodyMorphs can undo it " +
        "without touching other mods' morphs, and saved with the game. The body updates at once. " +
        "Needs RaceMenu and a body built with morph support. Defaults to the player; refId for an NPC reference.")]
    public async Task<object> SetBodyMorph(string morph, float value, string key = DefaultMorphKey, string? refId = null)
    {
        if (string.IsNullOrWhiteSpace(morph)) return new { error = "morph is empty" };
        var target = RefArg(refId);
        var before = await Papyrus("NiOverride", "GetBodyMorph", target, morph, key);
        await Papyrus("NiOverride", "SetBodyMorph", target, morph, key, value);
        await Papyrus("NiOverride", "UpdateModelWeight", target);
        await NotifyInGame("Body morph changed");
        return new { actor = target, morph, key, previous = before?.GetValue<float>(), value };
    }

    [McpServerTool]
    [Description("Undo body morphs set under one key (default 'SkyLinkAI', the ones SkyLink set). morph (optional) clears only that " +
        "morph; otherwise every morph under the key. Defaults to the player.")]
    public async Task<object> ClearBodyMorphs(string? morph = null, string key = DefaultMorphKey, string? refId = null)
    {
        var target = RefArg(refId);
        if (string.IsNullOrWhiteSpace(morph)) await Papyrus("NiOverride", "ClearBodyMorphKeys", target, key);
        else await Papyrus("NiOverride", "ClearBodyMorph", target, morph, key);
        await Papyrus("NiOverride", "UpdateModelWeight", target);
        return new { actor = target, key, cleared = morph ?? "all morphs under this key" };
    }

    [McpServerTool]
    [Description("List the RaceMenu overlay slots (tattoos, warpaint, dirt, body paint) on one area: body, hands, feet or face. " +
        "Shows each used slot's texture, colour and alpha; empty slots are counted. Defaults to the player " +
        "(for an NPC pass refId and female).")]
    public async Task<object> GetOverlays(string area = "body", string? refId = null, bool? female = null)
    {
        if (!Areas.TryGetValue(area, out var a)) return new { error = "area must be body, hands, feet or face" };
        var target = RefArg(refId);
        var isFemale = await IsFemale(refId, female);
        var hasOverlays = (await Papyrus("NiOverride", "HasOverlays", target))?.GetValue<bool>() ?? false;
        var count = (await Papyrus("NiOverride", a.CountFunction))?.GetValue<int>() ?? 0;
        var used = new JsonArray();
        var empty = 0;
        for (var i = 0; i < count; i++)
        {
            var node = $"{a.Node} [Ovl{i}]";
            var texture = (await Papyrus("NiOverride", "GetNodeOverrideString", target, isFemale, node, KeyTexture, 0))?.GetValue<string>();
            if (string.IsNullOrEmpty(texture) || texture.Equals(DefaultOverlayTexture, StringComparison.OrdinalIgnoreCase)) { empty++; continue; }
            var color = (await Papyrus("NiOverride", "GetNodeOverrideInt", target, isFemale, node, KeyTintColor, -1))?.GetValue<int>() ?? 0;
            var alpha = (await Papyrus("NiOverride", "GetNodeOverrideFloat", target, isFemale, node, KeyAlpha, -1))?.GetValue<float>();
            used.Add(new JsonObject { ["slot"] = i, ["node"] = node, ["texture"] = texture, ["color"] = $"#{color & 0xFFFFFF:X6}", ["alpha"] = alpha });
        }
        return DeserializeResponse(new JsonObject
        {
            ["actor"] = target, ["area"] = area, ["female"] = isFemale, ["overlaysEnabled"] = hasOverlays,
            ["slots"] = count, ["emptySlots"] = empty, ["used"] = used,
        });
    }

    [McpServerTool]
    [Description("Put a texture in a RaceMenu overlay slot: a tattoo, warpaint or body paint. texture: a path under Data\\Textures " +
        "(as RaceMenu and SlaveTats use them, e.g. 'actors\\character\\overlays\\...\\tattoo.dds'). area: body, hands, feet or face. " +
        "slot: the slot number, or -1 (default) for the first empty one. color #RRGGBB (default white, the texture's own colours), " +
        "alpha 0 to 1 (default 1). Saved with the game, and RaceMenu shows it in its overlay menu. " +
        "Defaults to the player (for an NPC pass refId and female).")]
    public async Task<object> SetOverlay(string texture, string area = "body", int slot = -1, string color = "#FFFFFF", float alpha = 1,
        string? refId = null, bool? female = null)
    {
        if (!Areas.TryGetValue(area, out var a)) return new { error = "area must be body, hands, feet or face" };
        if (string.IsNullOrWhiteSpace(texture)) return new { error = "texture is empty" };
        var hex = color.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return new { error = "color must be #RRGGBB" };
        var target = RefArg(refId);
        var isFemale = await IsFemale(refId, female);

        if (!((await Papyrus("NiOverride", "HasOverlays", target))?.GetValue<bool>() ?? false))
            await Papyrus("NiOverride", "AddOverlays", target);
        var count = (await Papyrus("NiOverride", a.CountFunction))?.GetValue<int>() ?? 0;
        if (slot < 0)
        {
            for (var i = 0; i < count && slot < 0; i++)
            {
                var current = (await Papyrus("NiOverride", "GetNodeOverrideString", target, isFemale, $"{a.Node} [Ovl{i}]", KeyTexture, 0))?.GetValue<string>();
                if (string.IsNullOrEmpty(current) || current.Equals(DefaultOverlayTexture, StringComparison.OrdinalIgnoreCase)) slot = i;
            }
            if (slot < 0) return new { error = $"All {count} {area} overlay slots are in use; pass slot to replace one, or ClearOverlay first" };
        }
        if (slot >= count) return new { error = $"slot must be 0 to {count - 1} for {area}" };

        var node = $"{a.Node} [Ovl{slot}]";
        await Papyrus("NiOverride", "AddNodeOverrideString", target, isFemale, node, KeyTexture, 0, texture.Trim(), true);
        await Papyrus("NiOverride", "AddNodeOverrideInt", target, isFemale, node, KeyTintColor, -1, rgb, true);
        await Papyrus("NiOverride", "AddNodeOverrideFloat", target, isFemale, node, KeyAlpha, -1, Math.Clamp(alpha, 0f, 1f), true);
        await NotifyInGame("Overlay applied");
        return new { actor = target, area, slot, node, texture = texture.Trim(), color = "#" + hex.ToUpperInvariant(), alpha };
    }

    [McpServerTool]
    [Description("Empty a RaceMenu overlay slot (remove a tattoo or paint): area body, hands, feet or face, and the slot from GetOverlays. " +
        "Defaults to the player (for an NPC pass refId and female).")]
    public async Task<object> ClearOverlay(int slot, string area = "body", string? refId = null, bool? female = null)
    {
        if (!Areas.TryGetValue(area, out var a)) return new { error = "area must be body, hands, feet or face" };
        var target = RefArg(refId);
        var isFemale = await IsFemale(refId, female);
        var node = $"{a.Node} [Ovl{slot}]";
        await Papyrus("NiOverride", "RemoveNodeOverride", target, isFemale, node, KeyTexture, 0);
        await Papyrus("NiOverride", "RemoveNodeOverride", target, isFemale, node, KeyTintColor, -1);
        await Papyrus("NiOverride", "RemoveNodeOverride", target, isFemale, node, KeyAlpha, -1);
        return new { actor = target, area, slot, cleared = true };
    }

    [McpServerTool]
    [Description("Save the player's whole RaceMenu look (face sliders, head parts, colours, morphs, overlays) as a RaceMenu character " +
        "preset with CharGen.SaveCharacter, so it can be loaded later or on another character. name: the preset name, letters and numbers.")]
    public async Task<object> SaveRaceMenuPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new { error = "name must be a plain file name" };
        var result = await Papyrus("CharGen", "SaveCharacter", name.Trim());
        return new { saved = name.Trim(), result = result?.DeepClone(), presets = ListPresets() };
    }

    [McpServerTool]
    [Description("Load a RaceMenu character preset (from SaveRaceMenuPreset, or one exported in RaceMenu) onto the player with " +
        "CharGen.LoadCharacter. Changes the whole look; save first if the player may want the old one back (SaveRaceMenuPreset). " +
        "Lists the presets found when the name doesn't match.")]
    public async Task<object> LoadRaceMenuPreset(string name)
    {
        var appearance = await _pipe.SendRequestAsync("get_appearance");
        var race = appearance?["raceFormId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(race)) return new { error = "Couldn't read the player's race" };
        var presets = FindPresets();
        var match = presets.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)
                                             || Path.GetFileName(p.Name).Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        var result = await Papyrus("CharGen", "LoadCharacter", "player", race, match?.Name ?? name.Trim());
        var ok = result?.GetValueKind() == System.Text.Json.JsonValueKind.True;
        if (ok)
        {
            await NotifyInGame("RaceMenu preset loaded");
            return new { loaded = true, name = name.Trim(), note = "If the face looks unchanged, open and close RaceMenu or save and load." };
        }

        // Not loaded: say where the file is and what this RaceMenu offers, instead of guessing.
        object? loaders = null;
        try
        {
            var fns = await _pipe.SendRequestAsync("get_script_functions", new JsonObject { ["className"] = "CharGen" });
            loaders = fns?.ToJsonString().Split('"').Where(t => t.Contains("Load", StringComparison.OrdinalIgnoreCase) && t.Length < 120).Distinct().ToArray();
        }
        catch (McpException) { }
        return new
        {
            loaded = false,
            name = name.Trim(),
            foundAt = match?.Path,
            folderKind = match?.Kind,
            note = match == null
                ? "No preset with that name was found; see presets."
                : match.Kind == "Presets"
                    ? "This preset is in CharGen\\Presets (RaceMenu's Presets tab). CharGen.LoadCharacter didn't load it; it may only read " +
                      "presets exported with SaveCharacter (CharGen\\Exported). Load it from RaceMenu's Presets tab instead."
                    : "CharGen.LoadCharacter returned false for this exported preset.",
            charGenLoadFunctions = loaders,
            presets = presets.Select(p => $"{p.Kind}/{p.Name}").ToArray(),
        };
    }

    private sealed record PresetFile(string Name, string Kind, string Path);

    // .jslot files RaceMenu writes, in the folders SkyLink knows about (MO2 overwrite first),
    // including subfolders; Name is relative to Exported or Presets, without the extension.
    private static List<PresetFile> FindPresets()
    {
        var found = new List<PresetFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugins in SkyLinkSettings.ConfigFolders())
        {
            foreach (var kind in new[] { "Exported", "Presets" })
            {
                var folder = Path.Combine(plugins, "CharGen", kind);
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (var f in Directory.EnumerateFiles(folder, "*.jslot", SearchOption.AllDirectories))
                    {
                        var rel = Path.ChangeExtension(Path.GetRelativePath(folder, f), null)!.Replace('\\', '/');
                        if (seen.Add(kind + "/" + rel)) found.Add(new PresetFile(rel, kind, f));
                    }
                }
                catch { /* unreadable folder: skip */ }
            }
        }
        return found.OrderBy(p => p.Kind).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string[] ListPresets() => FindPresets().Select(p => $"{p.Kind}/{p.Name}").ToArray();
}
