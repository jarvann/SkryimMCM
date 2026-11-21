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
            // CharGen.SaveCharacter writes the player's look (CharGen\<name>.jslot and its tint); LoadCharacter maps it onto
            // the NPC, and RaceMenu rebuilds the head and tint, so no dark face.
            var presetName = "SkyLink_" + new string(name.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            try
            {
                await Papyrus("CharGen", "SaveCharacter", presetName);
                var loaded = await Papyrus("CharGen", "LoadCharacter", refId, spawned?["race"]?.ToString(), presetName);
                faceResult = new { preset = presetName, loaded = loaded?.ToString() };
            }
            catch (McpException ex) { faceResult = new { error = ex.Message, note = "The NPC was spawned; its face is the template's head without its FaceGen file, which can look dark." }; }
        }

        await NotifyInGame($"{name.Trim()} spawned (preview)");
        return new
        {
            spawned = DeserializeResponse(spawned),
            face = faceResult,
            lasting = false,
            note = "A preview for this session: it won't survive a save and load. GetNPCDetailedInfo(refId) shows it; RemoveSpawnedNpc removes it.",
        };
    }

    [McpServerTool]
    [Description("Remove an NPC spawned with SpawnNpc (FEAT-122): disables and deletes the reference. Only works on SkyLink's " +
        "spawned NPCs (runtime bases), never on the game's own characters.")]
    public async Task<object> RemoveSpawnedNpc(string refId)
    {
        var r = await _pipe.SendRequestAsync("remove_spawned_npc", new JsonObject { ["refId"] = refId.Trim() });
        return DeserializeResponse(r);
    }
}
