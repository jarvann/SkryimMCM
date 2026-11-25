using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SkyrimMCP.Characters;

namespace SkyrimMCP.Tools;

/// <summary>
/// FEAT-301: finishing a character the player made, by one of two routes the player picks (Cory, 2026-11-22: "work both routes
/// and let the user choose"). Live: it stays in this save as a SkyLink character, which SkyLink looks after on every load.
/// Standalone: its own mod (ExportCharacter), in every save and needing no SkyLink, but a new person in each.
/// </summary>
public partial class CharacterTools
{
    // A stand-in face file (FEAT-305) is this size: the slot has no face of its own yet.
    private const long StandInMeshBytes = 332;

    [McpServerTool]
    [Description("Finalize a character the player made (FEAT-301): the player picks the route. Without how: this character's state and, for " +
        "each route, what it needs, keeps and loses; ASK THE PLAYER which, then call again. how 'live': it stays in this save as a SkyLink " +
        "character (needs SkyLink AI installed as long as it's played; SkyLink keeps its face, hair colour, voice and home after every load); " +
        "recorded, with anything still to decide. how 'standalone': its own mod made with ExportCharacter (which asks its own questions), " +
        "in every save and new game with no SkyLink needed, but a new person there: this save's bond (marriage, follower state, what it " +
        "carries) stays with the live one, which is removed afterwards (RemoveSpawnedNpc) so there aren't two. refId: the character.")]
    public async Task<object> FinalizeCharacter(string refId, string? how = null)
    {
        refId = refId.Trim();
        how = how?.Trim().ToLowerInvariant();
        if (how is not (null or "live" or "standalone")) return new { error = "how must be 'live' or 'standalone' (or left out to see both)." };
        JsonNode? data;
        try { data = await _pipe.SendRequestAsync("character_forms", new JsonObject { ["refId"] = refId }); }
        catch (McpException ex) { return new { error = ex.Message }; }
        if (data?["error"] != null) return DeserializeResponse(data);
        if (data!["slot"] is not JsonValue slotValue || SlotRegistry.Load()[slotValue.ToString()] is not JsonObject entry)
            return new { error = "Only characters in SkyLink's NPC slots can be finalized. This one is a preview (gone on the next load) or not SkyLink's: make it again with SpawnNpc." };
        var slot = slotValue.GetValue<int>();
        var name = data["name"]?.ToString() ?? "The character";

        // The face: a slot whose files are still the stand-ins (or missing) has no face of its own to keep or export.
        var faceFiles = SlotRegistry.FaceFiles(entry["objectId"]?.ToString() ?? "");
        var faceOk = faceFiles is { } ff && File.Exists(ff.mesh) && File.Exists(ff.tint) && new FileInfo(ff.mesh).Length > StandInMeshBytes;

        var forms = (data["forms"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        bool InFaction(string id) => forms.Any(f => f["kind"]?.ToString() == "faction" && f["stableId"]?.ToString() == id);
        var follower = InFaction("Skyrim.esm|05C84D");
        var marryable = InFaction("Skyrim.esm|019809");
        var family = entry["family"]?.GetValue<string?>();
        var attraction = entry["attraction"]?.GetValue<string?>();
        var home = entry["home"] switch
        {
            JsonObject h => new { name = h["name"]?.ToString(), kind = h["kind"]?.ToString(), reference = h["ref"]?.ToString() },
            // SetSpouseHome keeps only the home's reference.
            JsonValue v => new { name = HomeRegistry.Load()[v.ToString()]?["name"]?.ToString() ?? "the spouse's home", kind = (string?)"skylink", reference = v.ToString() },
            _ => null,
        };
        var spouse = await IsSpouse(refId);
        var carried = forms.Count(f => f["kind"]?.ToString() == "carried");
        var morphs = await BodyMorphCount(refId);

        var now = new
        {
            slot, follower, marryable, spouse, family, attraction, home, faceFileInPlace = faceOk,
            finalized = entry["finalized"]?.DeepClone(),
        };
        if (!faceOk)
            return new
            {
                character = name, now,
                error = "This character's face file isn't in place yet (the slot still has its stand-in), so neither route would keep the face. " +
                        "Restart the game once so it reads the face, check it looks right, then finalize.",
            };

        if (how == null)
        {
            var live = new
            {
                how = "live",
                what = $"{name} stays in this save as a SkyLink character.",
                needs = new List<string?>
                {
                    "SkyLink AI installed for as long as they're played: SkyLinkAI.esp holds them, so removing it removes them (and what they carry) from saves.",
                    morphs > 0 ? $"RaceMenu, for the {morphs} body morphs set on them (the face itself is in its own file and needs no RaceMenu)." : null,
                }.OfType<string>(),
                keeps = new List<string?>
                {
                    "Everything in this save: face, hair colour, voice and body tint (SkyLink puts them back after each load).",
                    spouse ? "The marriage." : null,
                    follower ? "Their follower state and whatever you gave them." : carried > 0 ? $"What they carry ({carried} kinds of item)." : null,
                    family != null ? $"Their family role ({family})." : null,
                    home != null ? $"Their home ({home.name})." : null,
                }.OfType<string>(),
                loses = new List<string>
                {
                    "Only in this save and the saves after it: not in other saves, another character's game or a new game, and not shareable. Exporting later is still possible: an export doesn't change the live one.",
                    $"Keeps slot {slot} of SkyLinkAI.esp (25 per race and sex).",
                },
            };
            var standalone = new
            {
                how = "standalone",
                what = $"{name} becomes their own mod (a light plugin with their face files), made with ExportCharacter.",
                needs = new List<string?>
                {
                    "The mods their race, hair, outfit and textures come from (CharacterRequirements lists them; the export's README too).",
                    family != null ? $"SkyLink AI as well, only if their family role ({family}) is kept (the export asks)." : null,
                }.OfType<string>(),
                keeps = new List<string>
                {
                    "Face, hair colour, voice, body tint, height and weight; the outfit they wear (if chosen); follower and marriage as the export asks.",
                    "Works in every save and new game, with no SkyLink, and can be shared (credit the authors whose meshes are in the face file).",
                },
                loses = new List<string?>
                {
                    "It's a new person in each save: this save's bond stays with the live one, which you then remove (RemoveSpawnedNpc) so there aren't two.",
                    spouse ? $"The marriage: the exported {name} isn't married to you (marry them again, or keep the live route)." : null,
                    carried > 0 || follower ? "What they carry beyond their outfit, and their follower history." : null,
                    home is { kind: "skylink" } ? $"Their SkyLink-built home ({home.name}): it exists only in this save. The export places them at a base game marker where they stand." : null,
                    home is { kind: not "skylink" } ? $"Their home as such: the export places them at a base game marker where they stand (stand them at {home.name} first to start them there)." : null,
                    morphs > 0 ? $"The {morphs} body morphs set on them: the exported character gets the body OBody or the race gives any NPC." : null,
                    "SkyLink's later care: face rebuilds and hair changes then happen by editing the mod.",
                }.OfType<string>(),
            };
            return new
            {
                character = name, now, needsChoice = true,
                question = $"How should {name} be finalized? Live: they stay in this save as they are, with SkyLink. Standalone: their own mod, in every save without SkyLink, as a new person.",
                choices = new object[] { live, standalone },
                next = "Ask the player, then FinalizeCharacter with how 'live' or 'standalone'.",
            };
        }

        var copy = entry.DeepClone().AsObject();
        copy["finalized"] = new JsonObject { ["how"] = how, ["at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") };
        SlotRegistry.Reserve(slot, copy);
        if (how == "live")
        {
            var toDecide = new List<string?>
            {
                home == null ? "No home: they stay where they're left. Ask the player where they live (SetHome)." : null,
            }.OfType<string>().ToList();
            return new
            {
                character = name, finalized = "live", now,
                stillToDecide = toDecide.Count > 0 ? toDecide : null,
                note = $"{name} is a SkyLink character in this save: keep SkyLink AI installed, and save the game to keep them. Slot {slot} stays theirs until RemoveSpawnedNpc.",
            };
        }
        return new
        {
            character = name, finalized = "standalone", now,
            next = new[]
            {
                $"ExportCharacter refId {refId} with a modName: it asks follower, marryable{(family != null ? " and family" : "")}; dry run first, then dryRun false.",
                "Install the zip with the mod manager.",
                $"In the game: RemoveSpawnedNpc {refId}, so there aren't two (this frees slot {slot} from the next game start).",
                "Start the game and load a save: the exported character appears at the place the export chose.",
            },
            note = spouse ? $"{name} is married to the player: the exported character isn't. The live one keeps the marriage until removed." : null,
        };
    }

    /// <summary>Whether the character is the player's spouse (the marriage quest's LoveInterest after the wedding).</summary>
    private async Task<bool> IsSpouse(string refId)
    {
        try
        {
            var spouse = (await SpouseAlias("LoveInterest", "GetActorRef"))?["result"]?.ToString();
            static uint? Hex(string? s) => uint.TryParse(s?.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : null;
            return Hex(spouse) is uint a && a == Hex(refId);
        }
        catch (McpException) { return false; }
    }

    /// <summary>How many body morphs RaceMenu holds for the character, or 0 (none, or no RaceMenu).</summary>
    private async Task<int> BodyMorphCount(string refId)
    {
        try { return (await RaceMenu.Papyrus("NiOverride", "GetMorphNames", refId) as JsonArray)?.Count ?? 0; }
        catch (McpException) { return 0; }
    }
}
