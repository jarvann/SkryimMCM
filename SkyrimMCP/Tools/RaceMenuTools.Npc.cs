using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SkyrimMCP.Face;

namespace SkyrimMCP.Tools;

/// <summary>
/// FEAT-122 / INFRA-024 (1.6, batch A): spawn a new character next to the player. The NPC base is a runtime copy of a
/// plain NPC of the chosen race and sex; its face is either that template's (it has FaceGen files, so no dark face) or the
/// player's current face through RaceMenu's CharGen (SaveCharacter, then LoadCharacter onto the NPC), which is how a face
/// built with the face builder becomes an NPC. Runtime forms aren't kept by saves: without SkyLink's NPC slot plugin a
/// spawn is a preview for this session, and the lasting result is the player's own plugin (FEAT-283).
/// </summary>
public partial class RaceMenuTools
{
    // refId -> preset, so removing a spawned NPC removes the face files made for it. Kept in a file: the spike's server restarted
    // between calls and left the files behind.
    private static string SpawnFile => Path.Combine(SkyLinkSettings.Folder, "spawned_npcs.json");

    private static JsonObject LoadSpawns()
    {
        try { return File.Exists(SpawnFile) ? JsonNode.Parse(File.ReadAllText(SpawnFile)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch (Exception) { return new JsonObject(); }
    }

    private static void SaveSpawns(JsonObject spawns)
    {
        try { Directory.CreateDirectory(SkyLinkSettings.Folder); File.WriteAllText(SpawnFile, spawns.ToJsonString()); } catch (Exception) { }
    }

    /// <summary>
    /// Exports the player's head where LoadCharacter looks for the tint, Textures\CharGen\Exported\<name>.dds (and .nif), by giving
    /// ExportHead a name that leads there from its own CharGen folder. The game writes the files itself, so under MO2 they land in
    /// overwrite AND the running game sees them: a copy SkyLink made there was invisible to the game until a restart, and the face
    /// showed purple (BUG-106, 2026-11-23). Null when done, else what went wrong.
    /// </summary>
    private async Task<string?> ExportTintFor(string preset)
    {
        foreach (var file in ExportedFiles(preset)) { TryDelete(file.dds); TryDelete(file.nif); }   // never take an old export
        await Papyrus("CharGen", "ExportHead", @"..\..\..\Textures\CharGen\Exported\" + preset);
        long last = -1;
        for (var i = 0; i < 60; i++)
        {
            foreach (var (dds, _) in ExportedFiles(preset))
            {
                if (!File.Exists(dds)) continue;
                var size = new FileInfo(dds).Length;
                if (size > 128 && size == last)
                {
                    // No skin-tone layers on the player: a flat grey tint. The skin colour instead, as the game's own tints are.
                    var bytes = await File.ReadAllBytesAsync(dds);
                    if (Face.TintTexture.IsFlat(bytes))
                    {
                        try
                        {
                            var skin = (await _pipe.SendRequestAsync("head_materials", new JsonObject { ["refId"] = "00000014" }))?["bodyTint"]?.ToString();
                            if (skin != null) await File.WriteAllBytesAsync(dds, Face.TintTexture.Fill(bytes, skin));
                        }
                        catch (McpException) { }   // left grey: still a face, not a purple one
                    }
                    // Kept until SpawnNpc has used them: ExportedHead reads both and deletes the mesh; LoadCharacter needs the tint.
                    return null;
                }
                last = size;
            }
            await Task.Delay(250);
        }
        return "RaceMenu's ExportHead didn't write the skin texture (Textures\\CharGen\\Exported\\" + preset + ".dds) within 15 s, so the face would turn purple. Nothing was spawned.";
    }

    /// <summary>Where an export of this name lands, in each folder the game's Data may be (MO2's overwrite among them).</summary>
    private static IEnumerable<(string dds, string nif)> ExportedFiles(string preset) =>
        SkyLinkSettings.ConfigFolders().Select(plugins =>
        {
            var dds = Path.Combine(Path.GetFullPath(Path.Combine(plugins, "..", "..")), "Textures", "CharGen", "Exported", preset + ".dds");
            return (dds, Path.ChangeExtension(dds, ".nif"));
        });

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }

    [McpServerTool]
    [Description("Spawn a new NPC next to the player (FEAT-122, 1.6). name: what the game calls them. race: editor ID or form ID " +
        "(default the template's, or the player's); female: true or false (likewise); template: an NPC to copy stats, " +
        "outfit and voice from (default: a plain NPC of that race and sex). face: 'template' (the template's own face, works " +
        "everywhere) or 'player' (the player's current face, e.g. one built with the face builder; needs RaceMenu). " +
        "essential: can't be killed. The character stays around where it stands and belongs to no faction; keepSchedule true keeps " +
        "the template's daily schedule, job, factions and temper (a court wizard walks home). voice: a voice type's editor ID or form ID (e.g. " +
        "FemaleYoungEager; SearchForms type voicetype lists them), default the template's. face 'player' also copies the player's OBody " +
        "preset. outfit: 'template' or 'player' (copies of what the player wears; default with face 'player'). marryable (default true): can be " +
        "asked to marry, adults only. family: a role in the Player Family faction (Father, Mother, Brother, Sister, Cousin...; SetFamilyRole " +
        "without a role lists them), any race, adults only, never marryable. resemblance 0-1 (default 0.5 with family, else 0; needs RaceMenu and face 'template'): how much the " +
        "face and hair colour take after the player's, in any race or sex. follower: can be recruited with the game's own dialogue (give it a voice " +
        "that has follower lines, such as FemaleYoungEager, FemaleEvenToned or MaleEvenToned; without one it gets the even-toned voice). With SkyLinkAI.esp (25 slots of each race and sex) the character is kept by saves; without it, a PREVIEW for this session: a runtime NPC isn't kept when the game saves and loads; " +
        "exporting the character into the player's own plugin (coming in 1.6) makes it lasting. Remove it with RemoveSpawnedNpc. " +
        "dryRun (default true) says what would be spawned.")]
    public async Task<object> SpawnNpc(string name, string? race = null, bool? female = null, string? template = null,
        string face = "template", bool essential = false, bool keepSchedule = false, string? voice = null, string? outfit = null, bool marryable = true, string? family = null, double? resemblance = null, bool follower = false, bool dryRun = true)
    {
        face = face.Trim().ToLowerInvariant();
        if (face is not ("template" or "player")) return new { error = "face must be 'template' or 'player'" };
        if (string.IsNullOrWhiteSpace(name)) return new { error = "name is required" };
        if (await SafetyBlock("move") is string blocked) return new { error = "Not now: " + blocked };
        if (face == "player" && await RaceMenuOpenError() is string open) return new { error = open };
        var likeness = (float)Math.Clamp(resemblance ?? (string.IsNullOrWhiteSpace(family) || face == "player" ? 0 : 0.5), 0, 1);
        var likenessNote = face == "player" && resemblance > 0 ? "resemblance isn't used with face 'player': it is the player's face" : null;
        if (face == "player") likeness = 0;
        var dress = string.IsNullOrWhiteSpace(outfit) ? face : outfit.Trim().ToLowerInvariant();
        if (dress is not ("template" or "player")) return new { error = "outfit must be 'template' or 'player'" };

        // A role such as Sister says the sex; the plugin checks it against the character's.
        string? familyRole = null;
        if (!string.IsNullOrWhiteSpace(family))
        {
            JsonNode? roles;
            try { roles = await _pipe.SendRequestAsync("family_roles"); }
            catch (McpException ex) { return new { error = ex.Message }; }
            var match = (roles?["roles"] as JsonArray ?? []).FirstOrDefault(r => string.Equals(r?["role"]?.ToString(), family.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null) return new { error = $"No family role called '{family}'.", roles = (roles?["roles"] as JsonArray ?? []).Select(r => r?["role"]?.ToString()) };
            familyRole = match["role"]!.ToString();
            if (match["sex"]?.ToString() is string roleSex)
            {
                if (female is bool asked && asked != (roleSex == "female")) return new { error = $"{familyRole} is a {roleSex} role." };
                female = roleSex == "female";
            }
            marryable = false;
        }

        if (dryRun)
            return new
            {
                dryRun = true,
                name,
                family = familyRole,
                resemblance = likeness,
                race = race ?? (template != null ? "the template's race" : "the player's race"),
                sex = female is bool f ? (f ? "female" : "male") : template != null ? "the template's sex" : "the player's sex",
                template = template ?? "a plain NPC of that race and sex",
                face = face == "player" ? "the player's current face (RaceMenu CharGen)" : "the template's own face",
                lasting = false,
                note = "Spawns next to the player as a preview for this session. Pass dryRun: false to spawn.",
            };

        // The face files first: if the export fails nothing has been spawned. SaveCharacter writes the preset (.jslot) but, seen
        // on Steam, no tint texture, and a missing tint shows purple; ExportHead does write it (with the head's mesh).
        var presetName = "SkyLink_" + new string(name.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (face == "player")
        {
            await Papyrus("CharGen", "SaveCharacter", presetName);
            if (await ExportTintFor(presetName) is string tintError) return new { error = tintError };
        }

        var p = new JsonObject { ["name"] = name.Trim(), ["face"] = face, ["essential"] = essential, ["keepSchedule"] = keepSchedule, ["marryable"] = marryable };
        if (!string.IsNullOrWhiteSpace(race)) p["race"] = race.Trim();
        if (female is bool sex) p["female"] = sex;
        if (!string.IsNullOrWhiteSpace(template)) p["template"] = template.Trim();
        p["avoidSlots"] = new JsonArray(SlotRegistry.Reserved().Select(i => (JsonNode)i).ToArray());
        var spawned = await _pipe.SendRequestAsync("spawn_npc", p);
        if (spawned?["error"] != null)
        {
            if (face == "player") ExportedHead(presetName);   // the exported head's files aren't needed
            return DeserializeResponse(spawned);
        }
        var refId = spawned?["refId"]?.ToString();

        object? voiceResult = null;
        if (!string.IsNullOrWhiteSpace(voice) && refId != null)
        {
            try
            {
                var set = await _pipe.SendRequestAsync("set_npc_voice", new JsonObject { ["refId"] = refId, ["voice"] = voice.Trim() });
                voiceResult = DeserializeResponse(set);
                if (set?["voiceStableId"] is JsonNode stable && spawned?["faceData"] is JsonObject fd) fd["voice"] = stable.DeepClone();
            }
            catch (McpException ex) { voiceResult = new { error = ex.Message, note = "Spawned with the template's voice." }; }
        }

        object? faceResult = null;
        if (face == "player" && refId != null)
        {
            try
            {
                var loaded = await Papyrus("CharGen", "LoadCharacter", refId, spawned?["race"]?.ToString(), presetName);
                // The 3D is rebuilt from the changed base: off, then on again a moment later.
                await _pipe.SendRequestAsync("set_spawned_npc_enabled", new JsonObject { ["refId"] = refId, ["enable"] = false });
                await Task.Delay(1500);
                await _pipe.SendRequestAsync("set_spawned_npc_enabled", new JsonObject { ["refId"] = refId, ["enable"] = true });
                var spawns = LoadSpawns();
                spawns[refId] = presetName;
                SaveSpawns(spawns);
                // The player's body shape, when OBody is installed (its script function isn't there otherwise).
                string? body = null;
                try
                {
                    body = (await Papyrus("OBodyNative", "GetPresetAssignedToActor", "00000014"))?.ToString();
                    if (!string.IsNullOrEmpty(body)) await Papyrus("OBodyNative", "ApplyPresetByName", refId, body);
                }
                catch (McpException) { body = null; }
                faceResult = new { bodyPreset = string.IsNullOrEmpty(body) ? "none (OBody isn't installed, or gave none)" : body, preset = presetName, loaded = loaded?.ToString(), hairColor = "the player's", bodyTone = "the player's" };
            }
            catch (McpException ex) { faceResult = new { error = ex.Message, note = "The NPC was spawned, but its face may be wrong." }; }
        }

        object? familyResult = null;
        if (familyRole != null && refId != null)
        {
            try { familyResult = (await SetFamily(refId, familyRole)).Reply; }
            catch (McpException ex) { familyResult = new { error = ex.Message, note = "Spawned, but not in the family." }; }
        }

        object? followerResult = null;
        if (follower && refId != null)
        {
            try { followerResult = (await SetFollower(refId, true, chooseVoice: string.IsNullOrWhiteSpace(voice), spawned)).Reply; }
            catch (McpException ex) { followerResult = new { error = ex.Message, note = "Spawned, but not as a follower." }; }
        }

        object? outfitResult = null;
        if (dress == "player" && refId != null)
        {
            try { outfitResult = DeserializeResponse(await _pipe.SendRequestAsync("copy_outfit", new JsonObject { ["refId"] = refId })); }
            catch (McpException ex) { outfitResult = new { error = ex.Message, note = "Spawned in the template's outfit." }; }
        }

        var inSlot = spawned?["slot"] is JsonValue;
        object? slotFace = null;
        if (inSlot && refId != null)
        {
            // The slot's own face files, so the face survives a restart (the pointer to the template's isn't saved).
            var objectId = spawned!["slotObjectId"]!.ToString();
            byte[]? mesh = null, tint = null;
            var slotIndex = spawned["slot"]!.GetValue<int>();
            HeadRepair.Result? repair = null;
            object? likenessResult = likenessNote;
            if (face == "player" && ExportedHead(presetName) is { } exported)
            {
                (mesh, tint) = (exported.mesh, exported.tint);
                try
                {
                    var look = await _pipe.SendRequestAsync("get_appearance", new JsonObject { ["refId"] = refId });
                    var parts = (look?["headParts"] as JsonArray ?? []).Where(h => h?["editorId"] != null)
                        .Select(h => (h!["editorId"]!.ToString(), h["model"]?.ToString())).ToList();
                    repair = HeadRepair.Repair(mesh, parts, MeshFiles.Read);
                    mesh = repair.Bytes;
                }
                catch (Exception ex) when (ex is McpException or InvalidDataException or IOException)
                {
                    repair = new HeadRepair.Result(mesh, [], [], [], ["The exported head wasn't checked: " + ex.Message]);
                }
            }
            else if (face == "template" && spawned["faceSource"] is JsonObject src && src["plugin"]?.ToString() is string facePlugin)
            {
                (mesh, tint) = SlotRegistry.ReadFace(facePlugin, src["objectId"]!.ToString());
                if (likeness > 0 && mesh != null) (mesh, likenessResult) = await FamilyFace(mesh, refId, likeness, spawned);
            }
            var known = SlotRegistry.KnownToTheGame(objectId);
            var problem = SlotRegistry.WriteFace(objectId, mesh, tint, out var files);
            string? characterId = null;
            try { characterId = (await _pipe.SendRequestAsync("get_appearance"))?["characterId"]?.ToString(); } catch (Exception) { }
            SlotRegistry.Reserve(slotIndex, new JsonObject
            {
                ["name"] = name.Trim(), ["refId"] = refId, ["baseId"] = spawned["baseId"]?.ToString(), ["objectId"] = objectId,
                ["face"] = face, ["family"] = familyRole, ["template"] = spawned["template"]?.ToString(),
                // What saves don't keep: the plugin puts it back at start-up and after loads (ReapplySlots).
                ["raceId"] = spawned["raceId"]?.DeepClone(), ["faceData"] = spawned["faceData"]?.DeepClone(), ["preset"] = face == "player" ? presetName : null, ["characterId"] = characterId,
                ["created"] = DateTime.Now.ToString("s"), ["files"] = new JsonArray(files.Select(f => (JsonNode)f).ToArray()),
            });
            // A template's character shows the template's file until told otherwise; loose files a mod manager doesn't virtualize are seen at once.
            if (problem == null && known && face == "template" && SkyLinkSettings.Mo2Instance().Item1 == null)
            {
                try
                {
                    await _pipe.SendRequestAsync("use_own_face", new JsonObject { ["refId"] = refId });
                    await _pipe.SendRequestAsync("set_spawned_npc_enabled", new JsonObject { ["refId"] = refId, ["enable"] = false });
                    await Task.Delay(1500);
                    await _pipe.SendRequestAsync("set_spawned_npc_enabled", new JsonObject { ["refId"] = refId, ["enable"] = true });
                }
                catch (McpException) { }
            }
            slotFace = problem == null
                ? new { written = files, resemblance = likenessResult, repairs = repair == null ? null : new { textures = repair.Textures, standIns = repair.StandIns, physicsBones = repair.Bones, notes = repair.Problems }, note = known ? "Seen at once." : "Their own face is seen after the next game start" + (face == "template" ? "; until then they show the template's." : ".") }
                : (object)new { problem, note = "Until the face files exist, the face turns dark after the game restarts." };
        }
        else if (face == "player") ExportedHead(presetName);   // a preview keeps nothing
        await NotifyInGame($"{name.Trim()} spawned" + (inSlot ? "" : " (preview)"));
        return new
        {
            spawned = DeserializeResponse(spawned),
            face = faceResult,
            voice = voiceResult,
            family = familyResult,
            follower = followerResult,
            outfit = outfitResult,
            slotFace,
            lasting = inSlot,
            note = inSlot
                ? "Uses one of SkyLinkAI.esp's slots, so the game keeps this character in saves. RemoveSpawnedNpc removes it and frees the slot."
                : "A preview for this session: it won't survive a save and load" +
                  (spawned?["slotPluginLoaded"]?.GetValue<bool>() == true ? " (every slot of that sex in SkyLinkAI.esp is in use)." : " (SkyLinkAI.esp, the optional plugin with NPC slots, isn't in the load order).") +
                  " GetNPCDetailedInfo(refId) shows it; RemoveSpawnedNpc removes it.",
        };
    }

    /// <summary>FEAT-304: the relative's face file with part of the player's features, and the player's hair colour on the character.</summary>
    private async Task<(byte[] Mesh, object Result)> FamilyFace(byte[] mesh, string refId, float amount, JsonNode spawned)
    {
        const string export = "SkyLink_FamilyHead";
        try
        {
            if (await RaceMenuOpenError() is string open) return (mesh, new { problem = open + " The relative has their own face." });
            if (await ExportTintFor(export) is string exportError) return (mesh, new { problem = exportError });
            var head = ExportedHead(export);
            foreach (var (dds, _) in ExportedFiles(export)) TryDelete(dds);   // the tint is for LoadCharacter, not used here
            if (head is not { } playerHead) return (mesh, new { problem = "The player's head wasn't exported." });

            var player = await _pipe.SendRequestAsync("get_appearance");
            var theirs = await _pipe.SendRequestAsync("get_appearance", new JsonObject { ["refId"] = refId });
            static string? Model(JsonNode? look, Func<JsonNode, bool> which) =>
                (look?["headParts"] as JsonArray ?? []).FirstOrDefault(h => h != null && which(h))?["model"]?.ToString();
            var plainPath = Model(player, h => h["typeName"]?.ToString() == "face");
            if (plainPath == null || MeshFiles.Read(plainPath) is not byte[] plain) return (mesh, new { problem = "The player's plain head mesh wasn't found." });
            var blend = FaceBlend.Blend(mesh, playerHead.mesh, plain,
                part => Model(theirs, h => string.Equals(h["editorId"]?.ToString(), part, StringComparison.OrdinalIgnoreCase)) is string m ? MeshFiles.Read(m) : null, amount);
            string? hair = null;
            if (player?["hairColor"]?.ToString() is { Length: 7 } colour)
            {
                var set = await _pipe.SendRequestAsync("set_face_data", new JsonObject { ["refId"] = refId, ["hairColor"] = colour });
                if (set?["hairColor"]?.ToString() is string now) { hair = now; if (spawned["faceData"] is JsonObject fd) fd["hairColor"] = now; }
            }
            var bytes = blend.Bytes;
            if (hair is { Length: 7 })
                bytes = FaceBlend.HairColour(bytes, Convert.ToByte(hair[1..3], 16), Convert.ToByte(hair[3..5], 16), Convert.ToByte(hair[5..7], 16));
            return (bytes, new
            {
                amount, hairColor = hair, headVertices = blend.HeadVertices, averageMove = Math.Round(blend.AverageMove, 3), largestMove = Math.Round(blend.LargestMove, 2),
                ownFeaturesReduced = blend.OwnFeaturesReduced,
            });
        }
        catch (Exception ex) when (ex is McpException or InvalidDataException or IOException or ArgumentException)
        {
            return (mesh, new { problem = "The family look couldn't be made (" + ex.Message + "). The relative has their own face." });
        }
    }

    private async Task<(bool Ok, object Reply)> SetFollower(string refId, bool follower, bool chooseVoice, JsonNode? spawned = null)
    {
        var set = await _pipe.SendRequestAsync("set_follower", new JsonObject { ["refId"] = refId, ["follower"] = follower });
        if (set?["error"] != null) return (false, DeserializeResponse(set));
        string? note = null;
        if (follower && set?["voiceHasFollowerLines"]?.GetValue<bool>() == false)
        {
            if (chooseVoice)
            {
                // FemaleEvenToned and MaleEvenToned: in every list of follower voices.
                var female = (await _pipe.SendRequestAsync("get_appearance", new JsonObject { ["refId"] = refId }))?["sex"]?.ToString()?.StartsWith("f", StringComparison.OrdinalIgnoreCase)
                    ?? spawned?["female"]?.GetValue<bool>() ?? false;
                var changed = await _pipe.SendRequestAsync("set_npc_voice", new JsonObject { ["refId"] = refId, ["voice"] = female ? "00013ADD" : "00013AD2" });
                if (changed?["voiceStableId"] is JsonNode stable && spawned?["faceData"] is JsonObject fd) fd["voice"] = stable.DeepClone();
                note = $"Its voice had no follower lines, so it now has {changed?["voice"] ?? changed?["voiceId"]}.";
            }
            else note = "This voice has no follower lines, so the recruit dialogue won't appear. Give it one of followerVoices.";
        }
        try
        {
            if (follower)
                await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject
                {
                    ["className"] = "Actor", ["functionName"] = "SetRelationshipRank", ["target"] = refId, ["args"] = new JsonArray("00000014", 3),
                });
        }
        catch (McpException ex) { note = (note + " The relationship rank wasn't set: " + ex.Message).Trim(); }
        return (true, new { result = DeserializeResponse(set), note });
    }

    [McpServerTool]
    [Description("Make a spawned character recruitable with the game's own follower dialogue (FEAT-282), or not (follower false). Sets the follower " +
        "factions, ally of the player, brave and helpful, protected, level with the player, and checks that the voice has follower lines " +
        "(followerVoices lists those that do). Follower frameworks such as Nether's pick it up when it's recruited. Adults only.")]
    public async Task<object> MakeFollower(string refId, bool follower = true) => (await SetFollower(refId.Trim(), follower, chooseVoice: false)).Reply;

    private async Task<(bool Ok, object Reply)> SetFamily(string refId, string role)
    {
        var set = await _pipe.SendRequestAsync("set_family", new JsonObject { ["refId"] = refId, ["role"] = role });
        if (set?["error"] != null) return (false, DeserializeResponse(set));
        string? liking = null;
        try
        {
            // Ally (3) for family, acquaintance (0) when taken out: how the game decides whether someone likes the player.
            await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject
            {
                ["className"] = "Actor", ["functionName"] = "SetRelationshipRank", ["target"] = refId,
                ["args"] = new JsonArray("00000014", set?["role"] is JsonValue ? 3 : 0),
            });
        }
        catch (McpException ex) { liking = "The relationship rank wasn't set: " + ex.Message; }
        return (true, new { member = DeserializeResponse(set), relationship = liking ?? (set?["role"] is JsonValue ? "ally" : "acquaintance") });
    }

    [McpServerTool]
    [Description("Player Family (FEAT-304): give a spawned character a role in the player's family (a rank of the Player Family faction in " +
        "SkyLinkAI.esp), so dialogue and other mods can ask for it. Without role: the list of roles (Father, Mother, Brother, Sister, Son, " +
        "Daughter, Cousin, step and in-law roles and more). role 'none' takes the character out. Any race, adults only; family members can't be married.")]
    public async Task<object> SetFamilyRole(string? refId = null, string? role = null)
    {
        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(refId)) return DeserializeResponse(await _pipe.SendRequestAsync("family_roles"));
        var (ok, reply) = await SetFamily(refId.Trim(), role.Trim());
        if (ok && SlotRegistry.SlotForRef(refId.Trim()) is int slot && SlotRegistry.Load()[slot.ToString()] is JsonObject entry)
        {
            var copy = entry.DeepClone().AsObject();
            copy["family"] = role.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? null : role.Trim();
            SlotRegistry.Reserve(slot, copy);
        }
        return reply;
    }

    /// <summary>The head RaceMenu exported for a preset (mesh and tint). Deletes the mesh; the tint stays for LoadCharacter.</summary>
    private static (byte[] mesh, byte[] tint)? ExportedHead(string preset)
    {
        foreach (var (dds, nif) in ExportedFiles(preset))
        {
            if (!File.Exists(dds) || !File.Exists(nif)) continue;
            try
            {
                var result = (File.ReadAllBytes(nif), File.ReadAllBytes(dds));
                TryDelete(nif);
                return result;
            }
            catch (Exception) { }
        }
        return null;
    }

    [McpServerTool]
    [Description("The characters kept in SkyLinkAI.esp's NPC slots (INFRA-024): slot, name, how the face was made, which character " +
        "(save) made it, and whether its face files are in place. Slots are shared by all saves of the game, so a slot stays taken " +
        "until RemoveSpawnedNpc (or freeSlot: the number of a slot whose character is gone from every save).")]
    public async Task<object> ListNpcSlots(int? freeSlot = null)
    {
        if (freeSlot is int f)
        {
            if (SlotRegistry.Load()[f.ToString()] == null) return new { error = $"Slot {f} isn't reserved." };
            return new { freed = f, filesDeleted = SlotRegistry.Release(f), note = "If a save still holds this character it keeps its place but loses its face files." };
        }
        JsonNode? state = null;
        try { state = await _pipe.SendRequestAsync("get_session_state"); } catch (Exception) { }
        var all = SlotRegistry.Load();
        return new
        {
            registry = all.Select(kv => new
            {
                slot = kv.Key, name = kv.Value?["name"]?.ToString(), family = kv.Value?["family"]?.ToString(), face = kv.Value?["face"]?.ToString(), characterId = kv.Value?["characterId"]?.ToString(),
                created = kv.Value?["created"]?.ToString(), filesInPlace = (kv.Value?["files"] as JsonArray)?.All(x => File.Exists(x?.ToString() ?? "")),
            }),
            taken = all.Count,
            note = all.Count == 0 ? "No slots taken." : "25 slots of each race and sex; a free slot is needed for a lasting character.",
        };
    }

    [McpServerTool]
    [Description("Remove an NPC spawned with SpawnNpc (FEAT-122): disables and deletes the reference. Only works on SkyLink's " +
        "spawned NPCs (runtime bases), never on the game's own characters.")]
    public async Task<object> RemoveSpawnedNpc(string refId)
    {
        var id = refId.Trim();
        var r = await _pipe.SendRequestAsync("remove_spawned_npc", new JsonObject { ["refId"] = id });
        var spawns = LoadSpawns();
        if (r?["error"] == null && spawns[id]?.ToString() is string preset && preset.StartsWith("SkyLink_", StringComparison.Ordinal))
        {
            spawns.Remove(id);
            SaveSpawns(spawns);
            // Only the files SpawnNpc made for this NPC (named SkyLink_...).
            foreach (var plugins in SkyLinkSettings.ConfigFolders())
            {
                var dataRoot = Path.GetFullPath(Path.Combine(plugins, "..", ".."));
                foreach (var file in new[] { Path.Combine(plugins, "CharGen", "Exported", preset + ".jslot"), Path.Combine(dataRoot, "Textures", "CharGen", "Exported", preset + ".dds") })
                    try { if (File.Exists(file)) File.Delete(file); } catch (Exception) { }
            }
        }
        if (r?["error"] == null && SlotRegistry.SlotForRef(id) is int slotIndex) SlotRegistry.Release(slotIndex);
        return DeserializeResponse(r);
    }
}
