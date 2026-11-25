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
public partial class RaceMenuTools : ToolBase
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

    internal async Task<JsonNode?> Papyrus(string className, string function, params JsonNode?[] args)
    {
        JsonNode? data;
        try
        {
            data = await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject
            {
                ["className"] = className,
                ["functionName"] = function,
                ["args"] = new JsonArray(args.Select(a => a?.DeepClone()).ToArray()),
            });
        }
        catch (McpException ex) when (MissingRaceMenuScript(className, ex.Message)) { throw new McpException(NoRaceMenu); }
        if (data?["error"] is JsonNode error)
        {
            if (MissingRaceMenuScript(className, error.ToString())) throw new McpException(NoRaceMenu);
            throw new McpException($"{className}.{function}: {error}");
        }
        return data?["result"];
    }

    // BUG-089: on a game without RaceMenu every RaceMenu tool (dry runs too) failed with "Function not found: CharGen.<x>. The
    // installed version of this script has no global function...", which reads like an outdated RaceMenu.
    private const string NoRaceMenu =
        "RaceMenu isn't installed (skee64.dll isn't loaded, so its CharGen and NiOverride scripts don't exist): this tool needs " +
        "it. Without RaceMenu these still work: CaptureFace, MeasureFace, MeasureProfile, MeasureBody, SetHairColor, " +
        "SetSkinTone, SetHeadPart, RemoveHeadPart, GetAppearance. GetRaceMenuStatus shows what RaceMenu provides.";

    private static bool MissingRaceMenuScript(string className, string message) =>
        (className.Equals("CharGen", StringComparison.OrdinalIgnoreCase) || className.Equals("NiOverride", StringComparison.OrdinalIgnoreCase) ||
         className.Equals("RaceMenu", StringComparison.OrdinalIgnoreCase)) &&
        (message.Contains("Function not found", StringComparison.OrdinalIgnoreCase) || message.Contains("Script class not found", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("not in the loose-source catalog", StringComparison.OrdinalIgnoreCase));

    private static bool ManagedByOBody(JsonArray names, IEnumerable<string> keys) =>
        names.Any(n => n?.GetValue<string>() == "obody_processed") || keys.Any(k => k.Equals("OBody", StringComparison.OrdinalIgnoreCase));

    // One morph's value under each key that sets it, and their sum (what the body shows).
    private async Task<(JsonObject Values, double Total)> MorphValues(string target, string name)
    {
        var keys = await Papyrus("NiOverride", "GetMorphKeys", target, name) as JsonArray ?? new JsonArray();
        var values = new JsonObject();
        double total = 0;
        foreach (var morphKey in keys.Select(k => k?.GetValue<string>()).OfType<string>())
        {
            var v = (await Papyrus("NiOverride", "GetBodyMorph", target, name, morphKey) as JsonValue)?.GetValue<float>() ?? 0f;
            values[morphKey] = Math.Round(v, 4);
            total += v;
        }
        return (values, total);
    }

    // The whole reply (result, signature, target), for diagnosing a call that returns nothing.
    private async Task<JsonNode?> PapyrusRaw(string className, string function, params JsonNode?[] args)
    {
        JsonNode? data;
        try
        {
            data = await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject
            {
                ["className"] = className,
                ["functionName"] = function,
                ["args"] = new JsonArray(args.Select(a => a?.DeepClone()).ToArray()),
            });
        }
        catch (McpException ex) when (MissingRaceMenuScript(className, ex.Message)) { throw new McpException(NoRaceMenu); }
        if (data?["error"] is JsonNode error) throw new McpException($"{className}.{function}: {error}");
        return data;
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
        "(each mod uses its own key), their values and the total (keys add up). nameContains filters by name; nonZero (default " +
        "true) leaves out morphs with no value. morph (with key, default 'SkyLinkAI') reads that one morph directly. " +
        "Bodies managed by OBody or a similar mod: change the body there and read it here. Defaults to the player; refId for an NPC.")]
    public async Task<object> GetBodyMorphs(string? refId = null, string? morph = null, string key = DefaultMorphKey,
        string? nameContains = null, bool nonZero = true)
    {
        var target = RefArg(refId);
        if (!string.IsNullOrWhiteSpace(morph))
        {
            // BUG-060: read one morph directly, in case RaceMenu's list of morph names comes back empty.
            var value = (await Papyrus("NiOverride", "GetBodyMorph", target, morph.Trim(), key) as JsonValue)?.GetValue<float>();
            var keys = await Papyrus("NiOverride", "GetMorphKeys", target, morph.Trim()) as JsonArray;
            return new { actor = target, morph = morph.Trim(), key, value, keys = keys?.Select(k => k?.ToString()).ToList() };
        }
        var names = await Papyrus("NiOverride", "GetMorphNames", target) as JsonArray ?? new JsonArray();
        var morphs = new JsonArray();
        var keysSeen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var name in names.Select(n => n?.GetValue<string>()).OfType<string>())
        {
            if (!string.IsNullOrWhiteSpace(nameContains) && !name.Contains(nameContains.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // keys describes the actor, not the filtered rows.
                foreach (var k in (await Papyrus("NiOverride", "GetMorphKeys", target, name) as JsonArray ?? new JsonArray())
                             .Select(k => k?.GetValue<string>()).OfType<string>()) keysSeen.Add(k);
                continue;
            }
            var (values, total) = await MorphValues(target, name);
            // BUG-068: 128 morphs on an OBody body, many with no value; leave those out by default.
            if (nonZero && Math.Abs(total) < 1e-6) { skipped++; continue; }
            foreach (var (k, _) in values) keysSeen.Add(k);
            if (morphs.Count < 200) morphs.Add(new JsonObject { ["morph"] = name, ["total"] = Math.Round(total, 4), ["byKey"] = values });
        }
        return DeserializeResponse(new JsonObject
        {
            ["actor"] = target,
            ["count"] = names.Count,
            ["listed"] = morphs.Count,
            ["leftOutNoValue"] = skipped,
            ["keys"] = new JsonArray(keysSeen.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            // BUG-069: OBody marks actors it handles (obody_processed) and stores under its own key; its values change whenever
            // a preset is assigned, so a read is a snapshot.
            ["managedBy"] = ManagedByOBody(names, keysSeen) ? "OBody" : null,
            ["managedNote"] = ManagedByOBody(names, keysSeen)
                ? "OBody manages this body: these values are its current preset and change when another preset is assigned. Read, don't cache; change the body through OBody."
                : null,
            ["morphs"] = morphs,
            ["note"] = morphs.Count >= 200 ? "Only the first 200 morphs are listed: narrow with nameContains." : null,
        });
    }

    [McpServerTool]
    [Description("Set a RaceMenu body morph (a BodySlide slider) on an actor. Names are case-sensitive and depend on the body: " +
        "read them first with GetBodyMorphs (e.g. 'belly', 'hips', 'Breasts' on one 3BA/OBody setup). " +
        "value: usually -1 to 1 (1 = the slider at 100). Stored under key (default 'SkyLinkAI'), so ClearBodyMorphs can undo it " +
        "without touching other mods' morphs, and saved with the game. The body updates at once. " +
        "Values from different keys add up. If OBody or a similar mod manages the body, change it there instead. " +
        "Needs RaceMenu and a body built with morph support. Defaults to the player; refId for an NPC reference.")]
    public async Task<object> SetBodyMorph(string morph, float value, string key = DefaultMorphKey, string? refId = null)
    {
        if (await SafetyBlock("face", refId) is string gateReason) return new { error = gateReason };  // FEAT-260
        if (string.IsNullOrWhiteSpace(morph)) return new { error = "morph is empty" };
        var target = RefArg(refId);
        // BUG-068: morph names are case-sensitive ("Belly" set a new morph beside OBody's "belly", which the body may not use).
        var existing = (await Papyrus("NiOverride", "GetMorphNames", target) as JsonArray ?? new JsonArray())
            .Select(n => n?.GetValue<string>()).OfType<string>().ToList();
        if (!existing.Contains(morph))
        {
            var sameLetters = existing.FirstOrDefault(n => n.Equals(morph, StringComparison.OrdinalIgnoreCase));
            if (sameLetters != null)
                return new { error = $"No morph '{morph}' on this actor, but '{sameLetters}' exists: morph names are case-sensitive. Use '{sameLetters}'." };
        }
        // A typo used twice is already in RaceMenu's list, so "in the list" isn't proof the body uses it: check whether any other
        // key gives it a value.
        var (othersBefore, _) = await MorphValues(target, morph);
        var usedElsewhere = othersBefore.Any(kv => !kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                                                    Math.Abs(kv.Value?.GetValue<double>() ?? 0) > 1e-6);
        var before = await Papyrus("NiOverride", "GetBodyMorph", target, morph, key);
        await Papyrus("NiOverride", "SetBodyMorph", target, morph, key, value);
        await Papyrus("NiOverride", "UpdateModelWeight", target);
        // BUG-060: in game a set reported success while GetBodyMorphs listed nothing; read it back so a set that didn't
        // take says so.
        var readCall = await PapyrusRaw("NiOverride", "GetBodyMorph", target, morph, key);
        var readBack = (readCall?["result"] as JsonValue)?.GetValue<float>();
        var listed = (await Papyrus("NiOverride", "GetMorphNames", target) as JsonArray)?.Count ?? 0;
        await NotifyInGame("Body morph changed");
        var took = readBack.HasValue && Math.Abs(readBack.Value - value) < 0.001;
        if (took)
        {
            var (byKey, total) = await MorphValues(target, morph);
            return new
            {
                actor = target, morph, key, previous = (before as JsonValue)?.GetValue<float>(), value, readBack, morphsListed = listed,
                total = Math.Round(total, 4), byKey,
                note = byKey.Count > 1 ? "Other keys also set this morph; the body shows the total. A body mod such as OBody may re-apply its own values." : null,
                obody = existing.Contains("obody_processed")
                    ? "OBody manages this body: this value stacks on OBody's, and the result shifts when OBody assigns another preset. Change the body through OBody's preset instead; ClearBodyMorphs undoes this." : null,
                warning = existing.Count > 0 && !usedElsewhere
                    ? $"No other mod gives '{morph}' a value on this actor: check that the body uses a morph with this exact name " +
                      "(names are case-sensitive). RaceMenu keeps the name in the actor's list even after ClearBodyMorphs." : null,
            };
        }
        // BUG-065: in game GetBodyMorph came back with no value at all (not even 0), which a float function only does when the
        // call failed in the VM; report what was called so the cause can be found (Papyrus log, or a different signature).
        var setCall = await PapyrusRaw("NiOverride", "SetBodyMorph", target, morph, key, value);
        return new
        {
            actor = target, morph, key, value,
            readBack = readBack?.ToString() ?? "none (the read returned no value)",
            morphsListed = listed,
            warning = "The morph didn't read back as set. If readBack is none, the NiOverride calls aren't completing: check " +
                      "GetScriptFunctions('NiOverride') for SetBodyMorph/GetBodyMorph and the Papyrus log for errors.",
            setSignature = setCall?["signature"]?.ToString(),
            getSignature = readCall?["signature"]?.ToString(),
            setResult = setCall?["result"]?.ToJsonString(),
        };
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
    [Description("Save the player's whole RaceMenu look (face sliders, head parts, colours, morphs, overlays) as a RaceMenu preset. " +
        "folder 'presets' (default) writes CharGen\\Presets\\<name>.jslot with CharGen.SaveCharacterPreset: the same files as RaceMenu's " +
        "Presets tab, and the kind LoadRaceMenuPreset, GetFacePreset and SetFaceSliders work with. Subfolders are allowed ('MyFolder/MyFace'). " +
        "folder 'exported' writes CharGen\\Exported with CharGen.SaveCharacter, plus its face-tint texture.")]
    public async Task<object> SaveRaceMenuPreset(string name, string folder = "presets")
    {
        var clean = CleanPresetName(name);
        if (clean == null) return new { error = "name must be a preset name, e.g. 'MyFace' or 'MyFolder/MyFace'" };
        if (await RaceMenuOpenError() is string open) return new { error = open };
        if (folder.Trim().Equals("exported", StringComparison.OrdinalIgnoreCase))
        {
            if (clean.Contains('/')) return new { error = "Exported presets can't go in subfolders" };
            var result = await Papyrus("CharGen", "SaveCharacter", clean);
            return new { saved = $"Exported/{clean}", result = result?.DeepClone(), presets = ListPresets() };
        }
        var file = await SaveLivePreset(clean);
        return new { saved = $"Presets/{file.Name}", file = file.Path };
    }

    [McpServerTool]
    [Description("Load a RaceMenu preset onto the player. Presets-tab presets (CharGen\\Presets, subfolders as 'MyFolder/MyFace') load with " +
        "CharGen.LoadCharacterPreset, RaceMenu's own preset loader. Exported presets (CharGen\\Exported) load with CharGen.LoadCharacter and " +
        "need their face-tint texture (Textures\\CharGen\\Exported\\<name>.dds), or the face turns purple. folder: 'presets', 'exported', " +
        "or empty to find the name in either (Presets first). Refused while RaceMenu is open. Changes the whole look: save first " +
        "(SaveRaceMenuPreset) if the player may want the old one back. The face can flash purple for a moment while its tint is rebuilt; " +
        "that is normal. Head parts RaceMenu's loader drops are put back (see headParts). Take a screenshot afterwards to check the face.")]
    public async Task<object> LoadRaceMenuPreset(string name, string? folder = null)
    {
        if (await SafetyBlock("face", null) is string gateReason) return new { error = gateReason };  // FEAT-260
        // Loading by script while RaceMenu is open broke the face tint in game.
        if (await RaceMenuOpenError() is string open) return new { error = open };

        var wanted = CleanPresetName(name);
        if (wanted == null) return new { error = "name must be a preset name, e.g. 'MyFace' or 'MyFolder/MyFace'" };
        var kind = folder?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "presets" => "Presets",
            "exported" => "Exported",
            _ => "?",
        };
        if (kind == "?") return new { error = "folder must be 'presets', 'exported' or empty" };

        var presets = FindPresets();
        bool Same(PresetFile p) => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase);
        bool SameLeaf(PresetFile p) => Path.GetFileName(p.Name).Equals(Path.GetFileName(wanted), StringComparison.OrdinalIgnoreCase);
        var match = presets.Where(p => kind == null || p.Kind == kind).OrderBy(p => p.Kind == "Presets" ? 0 : 1).FirstOrDefault(Same)
                    ?? presets.Where(p => kind == null || p.Kind == kind).OrderBy(p => p.Kind == "Presets" ? 0 : 1).FirstOrDefault(SameLeaf);
        if (match == null)
            return new { error = $"No preset named '{wanted}'.", presets = ListPresets() };

        var backup = await EnsureSessionBackup();
        if (match.Kind == "Presets")
        {
            var hairForm = await RaceMenuHairColorForm();
            var outcome = await LoadPresetsTabPreset(match.Name, match.Path, hairForm);
            if (outcome.Loaded) await NotifyInGame("RaceMenu preset loaded");
            return new
            {
                loaded = outcome.Loaded,
                preset = $"Presets/{match.Name}",
                file = match.Path,
                method = "CharGen.LoadCharacterPreset",
                hairColor = hairForm != null ? $"restored from the preset (RaceMenu colour form {hairForm})"
                                             : "not restored: RaceMenu's hair colour form wasn't found (set it with SetHairColor)",
                headParts = outcome.HeadParts?.DeepClone(),
                tints = outcome.Tints,
                backup,
                note = outcome.Loaded ? "Check the face with a screenshot."
                                      : "CharGen.LoadCharacterPreset returned false: the face is unchanged.",
            };
        }

        if (!HasTintTexture(match))
        {
            return new
            {
                error = $"'{wanted}' has no face-tint texture (Textures\\CharGen\\Exported\\{match.Name}.dds), so loading it would turn " +
                        "the face purple. Put the .jslot in CharGen\\Presets instead and load it from there.",
                file = match.Path,
            };
        }

        var appearance = await _pipe.SendRequestAsync("get_appearance");
        var race = appearance?["raceFormId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(race)) return new { error = "Couldn't read the player's race" };
        var result = await Papyrus("CharGen", "LoadCharacter", "player", race, match.Name);
        var loaded = result?.GetValueKind() == System.Text.Json.JsonValueKind.True;
        (JsonNode? HeadParts, string Tints)? repair = null;
        if (loaded)
        {
            await Task.Delay(300);
            repair = await RepairAfterLoad(PresetHeadParts(ReadJslot(match.Path)));
            await NotifyInGame("RaceMenu preset loaded");
        }
        return new
        {
            loaded,
            preset = $"Exported/{match.Name}",
            file = match.Path,
            method = "CharGen.LoadCharacter",
            headParts = repair?.HeadParts?.DeepClone(),
            tints = repair?.Tints,
            backup,
            note = loaded ? "Check the face (a screenshot): if the tint looks wrong, reload the player's own preset in RaceMenu before saving."
                          : "CharGen.LoadCharacter returned false.",
        };
    }

    // The tint texture SaveCharacter writes beside an exported preset, in any Data folder SkyLink searches.
    private static bool HasTintTexture(PresetFile preset)
    {
        foreach (var plugins in SkyLinkSettings.ConfigFolders())
        {
            try
            {
                var dataRoot = Path.GetFullPath(Path.Combine(plugins, "..", ".."));
                var dds = Path.Combine(dataRoot, "Textures", "CharGen", "Exported", preset.Name.Replace('/', Path.DirectorySeparatorChar) + ".dds");
                if (File.Exists(dds)) return true;
            }
            catch { /* bad path: skip */ }
        }
        return false;
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
