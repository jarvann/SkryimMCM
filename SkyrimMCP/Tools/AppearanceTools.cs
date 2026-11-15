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
    [Description("List the hair colours in the load order (colour forms) with FormID, name or editor ID, RGB and source mod. " +
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
        "An RGB value or word picks the closest existing hair colour, so the change survives saving and loading; " +
        "the reply shows what was requested and what was used. Reports the previous colour so it can be put back. " +
        "If RaceMenu is installed, its own hair colour setting may override this.")]
    public async Task<object> SetHairColor(string color, string? refId = null)
    {
        var parms = new JsonObject { ["color"] = color };
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        var data = await _pipe.SendRequestAsync("set_hair_color", parms);
        await NotifyInGame("Hair colour changed");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Set an actor's skin tone without RaceMenu. Defaults to the player; pass refId for an NPC. " +
        "color: #RRGGBB, 'r,g,b', or pale, fair, tan, olive, dark, ebony. The body changes at once. " +
        "The player's face may keep the old tone until the 3D is rebuilt: refresh=true rebuilds it (a brief flicker), " +
        "and saving then loading always applies it. NPC faces use pre-baked textures and may not match. " +
        "Reports the previous tone so it can be put back. If RaceMenu is installed, its skin tint may override this.")]
    public async Task<object> SetSkinTone(string color, string? refId = null, bool refresh = false)
    {
        var parms = new JsonObject { ["color"] = color, ["refresh"] = refresh };
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        var data = await _pipe.SendRequestAsync("set_skin_tone", parms);
        await NotifyInGame("Skin tone changed");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("List the head parts the character menu offers for one type (hair, eyes, eyebrows, facialHair, scar, face, misc) " +
        "for this race and sex. The default order (editorId: case-sensitive editor ID) is RaceMenu's slider order, so index = slider number; " +
        "editorIdNoCase, name and loadOrder are also available. start and count (default 21, max 200) page through it; " +
        "aroundFormId centres the page on one part. Use it to answer 'which hair is number N' or to pick a part to apply.")]
    public async Task<object> ListHeadPartChoices(string type = "hair", string? order = null, int start = 0, int count = 21,
        string? aroundFormId = null, string? refId = null)
    {
        var parms = new JsonObject { ["type"] = type, ["start"] = start, ["count"] = count };
        if (!string.IsNullOrEmpty(order)) parms["order"] = order;
        if (!string.IsNullOrEmpty(aroundFormId)) parms["aroundFormId"] = aroundFormId;
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        return DeserializeResponse(await _pipe.SendRequestAsync("list_head_part_choices", parms));
    }
}
