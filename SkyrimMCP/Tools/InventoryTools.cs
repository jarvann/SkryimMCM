using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// MCP Tools for inventory and item operations — get inventory, add/remove items, equip, bulk operations
/// </summary>
[McpServerToolType]
public class InventoryTools : ToolBase
{
    public InventoryTools(IPipeClient pipe) : base(pipe) { }

    [McpServerTool]
    [Description("Get an actor's inventory. Defaults to the player if no refId is given. " +
        "Pass a refId to read any NPC's inventory. Supports paging: page (default 1), pageSize (default 50). " +
        "Set page=0 for summary only. Includes item names, counts, types, weights, values, display names, enchantments.")]
    public async Task<object> GetInventory(string? refId = null, int page = 1, int pageSize = 50)
    {
        var parms = new JsonObject();
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        var data = await _pipe.SendRequestAsync("get_inventory", parms);
        return PageResponse(data, "items", page, pageSize);
    }

    [McpServerTool]
    [Description("Get all currently equipped items including armor, weapons, jewelry, and shield. " +
        "Shows which slot each item occupies (head, body, hands, feet, leftHand, rightHand, etc.). " +
        "leftHand and rightHand are always listed: an empty hand has empty=true; otherwise type is weapon, spell, shield, torch or scroll.")]
    public async Task<object> GetEquippedItems()
    {
        var data = await _pipe.SendRequestAsync("get_equipped_items");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Equip one or more items, spells or shouts in one call, on the player or (with refId) an NPC. " +
        "items: a JSON array whose entries are FormID strings or {\"formId\": \"...\", \"hand\": \"left|right|both\"}, " +
        "or a comma-separated list of FormIDs or known item names. " +
        "hand applies to one-handed weapons and spells (spells default to the right hand; 'both' is for spells only). " +
        "Items must already be in the actor's inventory; the copy it holds is equipped, so enchantments and tempering are kept. " +
        "Spells equip even if the actor has not learned them. Each entry reports ok or an error; " +
        "the game applies equips on its next frame, so confirm with GetEquippedItems. " +
        "NPCs may switch back to their outfit when their cell reloads.")]
    public async Task<object> EquipItems(string items, string? refId = null)
    {
        return await SendEquipItems(items, refId, unequip: false);
    }

    [McpServerTool]
    [Description("Unequip one or more items, on the player or (with refId) an NPC. " +
        "items: a JSON array of FormID strings or {\"formId\": \"...\"}, or a comma-separated list of FormIDs or known item names. " +
        "Weapons, armor, ammo and torches can be unequipped; spells and shouts cannot yet (equip something else instead). " +
        "Each entry reports ok or an error; confirm with GetEquippedItems.")]
    public async Task<object> UnequipItems(string items, string? refId = null)
    {
        return await SendEquipItems(items, refId, unequip: true);
    }

    private async Task<object> SendEquipItems(string items, string? refId, bool unequip)
    {
        JsonArray list;
        try
        {
            list = ParseItemList(items);
        }
        catch (Exception ex)
        {
            return new { error = $"Could not read items: {ex.Message}" };
        }
        if (list.Count == 0) return new { error = "No items given" };

        var parms = new JsonObject { ["items"] = list, ["unequip"] = unequip };
        if (!string.IsNullOrEmpty(refId)) parms["refId"] = refId;
        var data = await _pipe.SendRequestAsync("equip_items", parms);

        var succeeded = data?["succeeded"]?.GetValue<int>() ?? 0;
        if (succeeded > 0) await NotifyInGame($"{(unequip ? "Unequipped" : "Equipped")} {succeeded} item{(succeeded == 1 ? "" : "s")}");
        return DeserializeResponse(data);
    }

    // Accepts a JSON array (strings or {formId, hand}) or "a, b, c". Known item names are
    // resolved through SkyrimOffsets; anything else is passed on as a FormID.
    internal static JsonArray ParseItemList(string items)
    {
        var text = items.Trim();
        var list = new JsonArray();
        if (text.StartsWith('['))
        {
            var parsed = JsonNode.Parse(text)?.AsArray() ?? throw new FormatException("expected a JSON array");
            foreach (var node in parsed)
            {
                if (node is JsonValue v && v.TryGetValue<string>(out var id))
                {
                    list.Add(SkyrimOffsets.GetItemFormId(id) ?? id);
                }
                else if (node is JsonObject o)
                {
                    var entry = new JsonObject();
                    var formId = o["formId"]?.GetValue<string>() ?? o["name"]?.GetValue<string>()
                        ?? throw new FormatException("each object needs a formId");
                    entry["formId"] = SkyrimOffsets.GetItemFormId(formId) ?? formId;
                    if (o["hand"] is JsonNode hand) entry["hand"] = hand.GetValue<string>().ToLowerInvariant();
                    list.Add(entry);
                }
                else
                {
                    throw new FormatException("entries must be FormID strings or objects");
                }
            }
            return list;
        }

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(SkyrimOffsets.GetItemFormId(part) ?? part);
        return list;
    }

    private const string CategoryList = "all, weapons, armor, jewelry, ammo, potions, poisons, food, ingredients, books, scrolls, " +
        "soulgems, gems, smithing (ores, ingots, hides), keys, misc, gold, valuables (worth at least minValue, default 100, each)";

    [McpServerTool]
    [Description("Move every item of some categories from one container or actor to another in one call. " +
        "from and to: a refId, 'player', 'crosshair' (what the player is looking at, or the open container while a container menu is up), " +
        "or 'container' (the open container only); defaults from=player, to=crosshair. " +
        "Example: store smithing materials: categories='smithing', from='player', to='crosshair'. " +
        "categories (comma-separated): " + CategoryList + ". " +
        "items (instead of categories, for specific things): a JSON list of FormIDs or names with optional counts, " +
        "e.g. '[\"Ebony Sword\", {\"item\": \"0001397E\", \"count\": 2}]' or a comma-separated list; names match what the source holds. " +
        "This is a real move: enchantments, tempering and custom names go with the item (never add a copy and remove the original). " +
        "Each moved item reports destinationBefore and destinationAfter, so a top-up of an existing pile is visible; " +
        "destinationAlreadyHadSome counts those. Run a dry run first for sorting jobs. " +
        "minValue skips cheaper items. Equipped items, favorites and quest items stay unless includeEquipped / includeFavorites / " +
        "includeQuestItems is true. Enchanted and tempered items keep their properties. " +
        "Taking from something another NPC owns, or from a living NPC, is stealing and is refused unless allowStealing=true " +
        "(ask the player first; the game's normal crime rules apply). dryRun=true lists what would move without moving it.")]
    public async Task<object> TransferItems(string? categories = null, string from = "player", string to = "crosshair", int minValue = 0,
        bool includeEquipped = false, bool includeFavorites = false, bool includeQuestItems = false,
        bool allowStealing = false, bool dryRun = false, string? items = null)
    {
        if (string.IsNullOrWhiteSpace(categories) && string.IsNullOrWhiteSpace(items))
            return new { error = "Pass categories (e.g. 'smithing') or items (FormIDs or names)" };
        var parms = new JsonObject
        {
            ["categories"] = string.IsNullOrWhiteSpace(categories) ? new JsonArray() : SplitList(categories),
            ["from"] = from,
            ["to"] = to,
            ["minValue"] = minValue,
            ["includeEquipped"] = includeEquipped,
            ["includeFavorites"] = includeFavorites,
            ["includeQuestItems"] = includeQuestItems,
            ["allowStealing"] = allowStealing,
            ["dryRun"] = dryRun,
        };
        if (!string.IsNullOrWhiteSpace(items))
        {
            JsonNode? list = null;
            var trimmed = items.Trim();
            if (trimmed.StartsWith('[')) { try { list = JsonNode.Parse(trimmed); } catch { /* fall back to a plain list */ } }
            parms["items"] = list ?? new JsonArray(trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => (JsonNode)t).ToArray());
        }
        var data = await _pipe.SendRequestAsync("transfer_items", parms);
        var stacks = data?["stacks"]?.GetValue<int>() ?? 0;
        if (!dryRun && stacks > 0) await NotifyInGame($"Moved {stacks} item stack{(stacks == 1 ? "" : "s")}");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("After a fight, take loot from dead bodies near the player in one call. " +
        "radius in game units (default 2048, about 30 m). categories (comma-separated, default 'valuables'): " + CategoryList + ". " +
        "Gold is included unless includeGold=false. minValue skips cheaper items. Followers are skipped. " +
        "What the bodies wear and wield is included (marked equipped) unless includeEquipped=false. " +
        "Hidden items the game's loot menu doesn't show are never taken. " +
        "dryRun=true lists what would be taken. Reports bodiesInRange, bodiesWithLoot, bodiesLooted (0 on a dry run), " +
        "each body's items and the total value.")]
    public async Task<object> LootBodies(float radius = 2048, string categories = "valuables", int minValue = 0,
        bool includeGold = true, bool includeEquipped = true, bool dryRun = false)
    {
        var parms = new JsonObject
        {
            ["radius"] = radius,
            ["categories"] = SplitList(categories),
            ["minValue"] = minValue,
            ["includeGold"] = includeGold,
            ["includeEquipped"] = includeEquipped,
            ["dryRun"] = dryRun,
        };
        var data = await _pipe.SendRequestAsync("loot_bodies", parms);
        var value = data?["totalValue"]?.GetValue<int>() ?? 0;
        if (!dryRun && (data?["stacks"]?.GetValue<int>() ?? 0) > 0) await NotifyInGame($"Looted items worth {value} gold");
        return DeserializeResponse(data);
    }

    private static JsonArray SplitList(string text) =>
        new(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => (JsonNode)t.ToLowerInvariant()).ToArray());

    [McpServerTool]
    [Description("Add an item to player inventory by name or FormID. " +
        "CAUTION: Invalid FormIDs that don't exist in the current load order will crash the game. " +
        "Use ListKnownItems for safe FormIDs, or verify the FormID exists before adding.")]
    public async Task<object> AddItem(string item, int count = 1)
    {
        // Resolve friendly name to FormID
        var formId = SkyrimOffsets.GetItemFormId(item) ?? item;

        var data = await _pipe.SendRequestAsync("add_item", new JsonObject
        {
            ["formId"] = formId,
            ["count"] = count
        });

        var name = data?["name"]?.GetValue<string>() ?? item;
        await NotifyInGame($"Added {count}x {name}");
        return new { success = true, message = $"Added {count}x {name} (FormID: {formId})" };
    }

    [McpServerTool]
    [Description("Remove an item from player inventory by name or FormID. " +
        "CAUTION: Removing quest items can break quest progression and may be irreversible. " +
        "Confirm with the user before removing items you're not sure about.")]
    public async Task<object> RemoveItem(string item, int count = 1)
    {
        var formId = SkyrimOffsets.GetItemFormId(item) ?? item;

        var data = await _pipe.SendRequestAsync("remove_item", new JsonObject
        {
            ["formId"] = formId,
            ["count"] = count
        });

        var name = data?["name"]?.GetValue<string>() ?? item;
        await NotifyInGame($"Removed {count}x {name}");
        return new { success = true, message = $"Removed {count}x {name} (FormID: {formId})" };
    }

    [McpServerTool]
    [Description("Add multiple items to player inventory in a single call. " +
        "Pass a JSON array of items, each with 'formId' (or 'name') and optional 'count' (default 1). " +
        "Example: [{\"formId\":\"0000000F\",\"count\":1000},{\"name\":\"iron_sword\",\"count\":5}] " +
        "Items that fail are skipped and reported in the errors list. " +
        "Use this for bulk operations like applying a character blueprint's inventory.")]
    public async Task<object> BulkAddItems(string items)
    {
        List<object> results = new();
        List<object> errors = new();
        int successCount = 0;

        try
        {
            var itemArray = JsonNode.Parse(items)?.AsArray();
            if (itemArray == null) return new { error = "Invalid JSON array" };

            foreach (var item in itemArray)
            {
                try
                {
                    var formId = item?["formId"]?.GetValue<string>();
                    var name = item?["name"]?.GetValue<string>();
                    var count = item?["count"]?.GetValue<int>() ?? 1;

                    var resolvedId = formId ?? SkyrimOffsets.GetItemFormId(name ?? "") ?? name;
                    if (string.IsNullOrEmpty(resolvedId))
                    {
                        errors.Add(new { item = name ?? formId, error = "Could not resolve FormID" });
                        continue;
                    }

                    await _pipe.SendRequestAsync("add_item", new JsonObject
                    {
                        ["formId"] = resolvedId,
                        ["count"] = count
                    });
                    successCount++;
                }
                catch (Exception ex)
                {
                    errors.Add(new { item = item?.ToJsonString(), error = ex.Message });
                }
            }
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to parse items: {ex.Message}" };
        }

        await NotifyInGame($"Added {successCount} items");
        return new { success = true, added = successCount, failed = errors.Count, errors };
    }

    [McpServerTool]
    [Description("List all known item FormIDs and location names that can be used with other commands")]
    public object ListKnownItems()
    {
        return new
        {
            items = SkyrimOffsets.Items,
            locations = SkyrimOffsets.Locations,
            message = "Use these names with add_item, remove_item, and teleport commands, or provide FormIDs directly"
        };
    }
}
