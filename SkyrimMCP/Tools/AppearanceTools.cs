using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// Hair colour and skin tone with the game's own data, so they work without RaceMenu (FEAT-226).
/// </summary>
[McpServerToolType]
public class AppearanceTools : ToolBase
{
    public AppearanceTools(IPipeClient pipe) : base(pipe) { }

    [McpServerTool]
    [Description("Get an actor's hair colour (the colour form, with RGB) and skin tone (RGB). Defaults to the player; pass refId for an NPC. " +
        "For the player, skinToneTintMasks shows the face's skin-tone layers.")]
    public async Task<object> GetHairAndSkinColor(string? refId = null)
    {
        var parms = new JsonObject();
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        return DeserializeResponse(await _pipe.SendRequestAsync("get_colors", parms));
    }

    [McpServerTool]
    [Description("List the hair colours in the load order (colour forms) with FormID, name or editor ID, RGB, onScreen (the game draws hair " +
        "and brows at twice the stored colour, so onScreen is what you see) and source mod. " +
        "Playable colours only unless includeNonPlayable is true. Paged: page (default 1), pageSize (default 50).")]
    public async Task<object> ListHairColors(bool includeNonPlayable = false, int page = 1, int pageSize = 50)
    {
        var data = await _pipe.SendRequestAsync("list_hair_colors", new JsonObject { ["includeNonPlayable"] = includeNonPlayable });
        return PageResponse(data, "colors", page, pageSize);
    }

    [McpServerTool]
    [Description("Set an actor's hair colour without RaceMenu. Defaults to the player; pass refId for an NPC. " +
        "color: a hair colour FormID or name from ListHairColors, #RRGGBB, 'r,g,b', or a word (black, dark brown, brown, light brown, " +
        "auburn, red, ginger, copper, blonde, dark blonde, strawberry blonde, platinum, grey, silver, white, blue, green, purple, pink). " +
        "An RGB value or word is the colour wanted ON SCREEN: the game draws hair at twice the stored colour, so the closest existing " +
        "hair colour to half of it is used (matched by eye-like colour difference), which survives saving and loading; match says how " +
        "close it is and alternatives lists the next three; " +
        "the reply shows what was requested and what was used. Reports the previous colour so it can be put back. " +
        "If RaceMenu is installed, its own hair colour setting may override this. exact=true (player only, needs RaceMenu): the exact " +
        "colour asked for, kept in RaceMenu's own hair colour form the way RaceMenu's colour picker does, instead of the nearest existing one.")]
    public async Task<object> SetHairColor(string color, string? refId = null, bool exact = false)
    {
        var parms = new JsonObject { ["color"] = color, ["exact"] = exact };
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        var data = await _pipe.SendRequestAsync("set_hair_color", parms);
        if (string.IsNullOrEmpty(refId) && data?["error"] == null)
        {
            // Hair and brows as the game draws them (twice the stored colour), then the stored brow colour back.
            try
            {
                await _pipe.SendRequestAsync("head_part_tint", new JsonObject { ["type"] = 3, ["fromHairColor"] = true });
                await _pipe.SendRequestAsync("head_part_tint", new JsonObject { ["type"] = 6, ["fromHairColor"] = true });
                await SkyrimMCP.Face.FaceState.ReapplyBrowColor(_pipe);
            }
            catch (Exception) { /* cosmetic */ }
            // RaceMenu would put its stored hair colour back on the next load (BUG-046).
            await SkyrimMCP.Face.FaceState.RaceMenuSaveTints(_pipe);
        }
        await NotifyInGame("Hair colour changed");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Set an actor's skin tone without RaceMenu. Defaults to the player; pass refId for an NPC. " +
        "color: #RRGGBB, 'r,g,b', or pale, fair, tan, olive, dark, ebony. The body changes at once. " +
        "The player's face may keep the old tone until the 3D is rebuilt: refresh=true rebuilds it (a brief flicker), " +
        "and saving then loading always applies it. NPC faces use pre-baked textures and may not match. " +
        "Reports the previous tone so it can be put back. " +
        "With RaceMenu installed the change does NOT survive a full reload: RaceMenu keeps its own copy of the skin tint in the SKSE " +
        "co-save and restores it when the face is rebuilt (seen in game; a quick reload in the same session can hide this). " +
        "Tell the player to set skin tone in RaceMenu for a lasting change; this tool is for a quick look or for games without RaceMenu.")]
    public async Task<object> SetSkinTone(string color, string? refId = null, bool refresh = false)
    {
        var parms = new JsonObject { ["color"] = color, ["refresh"] = refresh };
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        var data = await _pipe.SendRequestAsync("set_skin_tone", parms);
        // The player's skin tone is also tint layer 0, which RaceMenu would put back on the next load (BUG-046).
        if (string.IsNullOrEmpty(refId) && data?["error"] == null) await SkyrimMCP.Face.FaceState.RaceMenuSaveTints(_pipe);
        await NotifyInGame("Skin tone changed");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("List the head parts the character menu offers for one type (hair, eyes, eyebrows, facialHair, scar, face, misc) " +
        "for this race and sex. The default order (editorId: case-sensitive editor ID) is RaceMenu's slider order, so index = slider number; " +
        "editorIdNoCase, name and loadOrder are also available. start and count (default 21, max 200) page through it; " +
        "aroundFormId centres the page on one part. Use it to answer 'which hair is number N' or to pick a part to apply. " +
        "showExcluded=true also lists the parts of this type the list leaves out (reasons: extraPart, notPlayable, otherSex, " +
        "raceNotInValidRaces; parts with no sex flag count for both sexes, as in RaceMenu), fewest failed rules first, with the index each would take; page with excludedStart " +
        "and excludedCount (max 200), filter with excludedRule. explain=<editorId or FormID> says why one part is left out. " +
        "Use these when RaceMenu shows more entries than this list.")]
    public async Task<object> ListHeadPartChoices(string type = "hair", string? order = null, int start = 0, int count = 21,
        string? aroundFormId = null, string? refId = null, bool showExcluded = false, int excludedStart = 0, int excludedCount = 60,
        string? excludedRule = null, string? explain = null)
    {
        var parms = new JsonObject
        {
            ["type"] = type, ["start"] = start, ["count"] = count, ["showExcluded"] = showExcluded,
            ["excludedStart"] = excludedStart, ["excludedCount"] = excludedCount,
        };
        if (!string.IsNullOrEmpty(excludedRule)) parms["excludedRule"] = excludedRule;
        if (!string.IsNullOrEmpty(explain)) parms["explain"] = explain;
        if (!string.IsNullOrEmpty(order)) parms["order"] = order;
        if (!string.IsNullOrEmpty(aroundFormId)) parms["aroundFormId"] = aroundFormId;
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        return DeserializeResponse(await _pipe.SendRequestAsync("list_head_part_choices", parms));
    }

    [McpServerTool]
    [Description("Change a head part (hair, eyes, eyebrows, facialHair, scar or misc such as horns) without RaceMenu. Defaults to the player. " +
        "Give part (a FormID or editor ID), or type plus index (the number from ListHeadPartChoices, the same as RaceMenu's slider). " +
        "Hair, eyes, eyebrows, facial hair and scars replace the current part; misc parts are added, since several can be worn " +
        "(remove one with RemoveHeadPart). The face part can't be changed. Reports the previous part so it can be put back. " +
        "If the change doesn't show, refresh=true rebuilds the 3D (a brief flicker), and saving then loading always applies it. " +
        "On an NPC this changes the base record (every copy of that NPC) and is saved with the game. On an NPC with a pre-built " +
        "face (most named NPCs and followers) it is refused unless force=true: it can look right in the session, but after the " +
        "game is restarted the head is built from the changed record and the face breaks (seen in game). force=true with " +
        "refresh=true is the repair: putting the NPC's original parts back. GetAppearance(refId) reads an NPC's current parts.")]
    public async Task<object> SetHeadPart(string? part = null, string? type = null, int index = -1, string? refId = null, bool refresh = false,
        bool force = false)
    {
        if (!force && await PrebuiltFace(refId) is string blocked) return new { error = blocked };
        var parms = new JsonObject { ["index"] = index, ["refresh"] = refresh };
        if (!string.IsNullOrEmpty(part)) parms["part"] = part;
        if (!string.IsNullOrEmpty(type)) parms["type"] = type;
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        var data = await _pipe.SendRequestAsync("set_head_part", parms);
        if (data?["error"] == null) await NotifyInGame("Head part changed");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Remove a head part, for example unwanted horns, a scar, a beard or the eyebrows. Defaults to the player. " +
        "part: the FormID or editor ID of the part (GetAppearance lists the current ones), or type for the current part of that type " +
        "(misc needs part, since several can be worn). The face and eyes can't be removed, only replaced. " +
        "The part stays visible until the 3D is rebuilt: refresh=true does it now, or save and load. " +
        "Reports the removed part so SetHeadPart can put it back. Refused on an NPC with a pre-built face unless force=true (see SetHeadPart).")]
    public async Task<object> RemoveHeadPart(string? part = null, string? type = null, string? refId = null, bool refresh = false,
        bool force = false)
    {
        if (!force && await PrebuiltFace(refId) is string blocked) return new { error = blocked };
        var parms = new JsonObject { ["refresh"] = refresh };
        if (!string.IsNullOrEmpty(part)) parms["part"] = part;
        if (!string.IsNullOrEmpty(type)) parms["type"] = type;
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        var data = await _pipe.SendRequestAsync("remove_head_part", parms);
        if (data?["error"] == null) await NotifyInGame("Head part removed");
        return DeserializeResponse(data);
    }

    // BUG-064/065/067: on an NPC with a FaceGen mesh a head part change gives a broken face: at once with a rebuild, and after
    // the next game restart without one (the change is saved; seen on a follower). Returns the refusal, or null when there is
    // no pre-built face.
    private async Task<string?> PrebuiltFace(string? refId)
    {
        if (string.IsNullOrWhiteSpace(refId) || refId.Trim().Equals("player", StringComparison.OrdinalIgnoreCase)) return null;
        var appearance = await _pipe.SendRequestAsync("get_appearance", new JsonObject { ["refId"] = refId.Trim() });
        if (appearance?["faceGenMesh"]?.GetValue<string>() is not string mesh) return null;
        var source = SkyrimMCP.Face.MeshFiles.Source(mesh);
        if (source == null) return null;
        return $"{appearance["name"]} has a pre-built face ({mesh}, {source}). A head part change is saved with the game, and after a " +
               "restart the head is built from the changed record, which no longer matches the face file: the face breaks. Use a wig " +
               "(an equippable hair item) instead. force=true (with refresh=true) is for putting the NPC's original parts back.";
    }
}
