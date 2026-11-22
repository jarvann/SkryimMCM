using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

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
    /// Exports the player's head (CharGen.ExportHead writes CharGen\<name>.dds and .nif) and puts the tint where LoadCharacter
    /// looks, Textures\CharGen\Exported\<name>.dds. Null when done, else what went wrong. Works in MO2 too: the game writes into
    /// its virtual folders, which SkyLink finds in overwrite.
    /// </summary>
    private async Task<string?> ExportTintFor(string preset)
    {
        await Papyrus("CharGen", "ExportHead", preset);
        long last = -1;
        for (var i = 0; i < 60; i++)
        {
            foreach (var plugins in SkyLinkSettings.ConfigFolders())
            {
                var dds = Path.Combine(plugins, "CharGen", preset + ".dds");
                if (!File.Exists(dds)) continue;
                var size = new FileInfo(dds).Length;
                if (size > 128 && size == last)
                {
                    var dataRoot = Path.GetFullPath(Path.Combine(plugins, "..", ".."));
                    var target = Path.Combine(dataRoot, "Textures", "CharGen", "Exported", preset + ".dds");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(dds, target, overwrite: true);
                    // The mesh isn't needed for a preview (FEAT-283 exports its own).
                    try { File.Delete(dds); File.Delete(Path.ChangeExtension(dds, ".nif")); } catch (Exception) { }
                    return null;
                }
                last = size;
            }
            await Task.Delay(250);
        }
        return "RaceMenu's ExportHead didn't write the skin texture (CharGen\\" + preset + ".dds) within 15 s, so the face would turn purple. Nothing was spawned.";
    }

    [McpServerTool]
    [Description("Spawn a new NPC next to the player (FEAT-122, 1.6). name: what the game calls them. race: editor ID or form ID " +
        "(default the player's race); female: true or false (default the player's sex); template: an NPC to copy stats, " +
        "outfit and voice from (default: a plain NPC of that race and sex). face: 'template' (the template's own face, works " +
        "everywhere) or 'player' (the player's current face, e.g. one built with the face builder; needs RaceMenu). " +
        "essential: can't be killed. A PREVIEW for this session: a runtime NPC isn't kept when the game saves and loads; " +
        "exporting the character into the player's own plugin (coming in 1.6) makes it lasting. Remove it with RemoveSpawnedNpc. " +
        "dryRun (default true) says what would be spawned.")]
    public async Task<object> SpawnNpc(string name, string? race = null, bool? female = null, string? template = null,
        string face = "template", bool essential = false, bool dryRun = true)
    {
        face = face.Trim().ToLowerInvariant();
        if (face is not ("template" or "player")) return new { error = "face must be 'template' or 'player'" };
        if (string.IsNullOrWhiteSpace(name)) return new { error = "name is required" };
        if (await SafetyBlock("move") is string blocked) return new { error = "Not now: " + blocked };
        if (face == "player" && await RaceMenuOpenError() is string open) return new { error = open };

        if (dryRun)
            return new
            {
                dryRun = true,
                name,
                race = race ?? "the player's race",
                sex = female is bool f ? (f ? "female" : "male") : "the player's sex",
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

        var p = new JsonObject { ["name"] = name.Trim(), ["face"] = face, ["essential"] = essential };
        if (!string.IsNullOrWhiteSpace(race)) p["race"] = race.Trim();
        if (female is bool sex) p["female"] = sex;
        if (!string.IsNullOrWhiteSpace(template)) p["template"] = template.Trim();
        var spawned = await _pipe.SendRequestAsync("spawn_npc", p);
        if (spawned?["error"] != null) return DeserializeResponse(spawned);
        var refId = spawned?["refId"]?.ToString();

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
                faceResult = new { preset = presetName, loaded = loaded?.ToString(), hairColor = "the player's", bodyTone = "the player's" };
            }
            catch (McpException ex) { faceResult = new { error = ex.Message, note = "The NPC was spawned, but its face may be wrong." }; }
        }

        var inSlot = spawned?["slot"] is JsonValue;
        await NotifyInGame($"{name.Trim()} spawned" + (inSlot ? "" : " (preview)"));
        return new
        {
            spawned = DeserializeResponse(spawned),
            face = faceResult,
            lasting = inSlot,
            note = inSlot
                ? "Uses one of SkyLinkAI.esl's slots, so the game keeps this character in saves. RemoveSpawnedNpc removes it and frees the slot."
                : "A preview for this session: it won't survive a save and load" +
                  (spawned?["slotPluginLoaded"]?.GetValue<bool>() == true ? " (every slot of that sex in SkyLinkAI.esl is in use)." : " (SkyLinkAI.esl, the optional plugin with NPC slots, isn't in the load order).") +
                  " GetNPCDetailedInfo(refId) shows it; RemoveSpawnedNpc removes it.",
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
        return DeserializeResponse(r);
    }
}
