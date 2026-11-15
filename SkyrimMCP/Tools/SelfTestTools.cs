using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// A scripted smoke test against the running game (INFRA-019): calls a fixed list of actions and
/// checks each reply has the fields it should. Makes testing a new build a single call instead
/// of a manual walk through the tools.
/// </summary>
[McpServerToolType]
public class SelfTestTools : ToolBase
{
    public SelfTestTools(IPipeClient pipe) : base(pipe) { }

    // action, params, fields that must be present in the reply
    private static readonly (string Action, Func<JsonObject> Params, string[] Fields)[] ReadOnlyChecks =
    {
        ("ping", () => new(), new[] { "pluginVersion", "gameVersion" }),
        ("game_thread_ping", () => new() { ["timeoutMs"] = 3000 }, new[] { "gameThreadMs" }),
        ("get_game_safety", () => new(), new[] { "safe" }),
        ("get_player_info", () => new(), new[] { "name", "level", "healthMax", "magickaMax", "staminaMax", "healthBase" }),
        ("get_equipped_items", () => new(), new[] { "equipped" }),
        ("get_inventory", () => new(), new[] { "items" }),
        ("get_gold_count", () => new(), Array.Empty<string>()),
        ("get_skill_levels", () => new(), Array.Empty<string>()),
        ("get_active_effects", () => new(), Array.Empty<string>()),
        ("get_appearance", () => new(), new[] { "race", "headParts" }),
        ("get_colors", () => new(), new[] { "hairColor", "skinTone" }),
        ("list_hair_colors", () => new(), new[] { "colors" }),
        ("get_weather", () => new(), new[] { "current", "currentWeather" }),
        ("get_cell_info", () => new(), Array.Empty<string>()),
        ("get_game_time", () => new(), new[] { "gameHour" }),
        ("get_menu_state", () => new(), new[] { "openMenus", "allOpenMenus" }),
        ("get_quest_info", () => new(), Array.Empty<string>()),
        ("get_nearby_npcs", () => new() { ["radius"] = 2000 }, new[] { "npcs" }),
        ("get_followers", () => new(), Array.Empty<string>()),
        ("get_combat_state", () => new(), Array.Empty<string>()),
        ("get_load_order", () => new(), new[] { "mods", "regularCount" }),
        ("get_mod_formid_prefix", () => new() { ["modName"] = "Skyrim.esm" }, new[] { "prefix" }),
        ("search_forms", () => new() { ["query"] = "Iron Sword", ["type"] = "weapon", ["maxResults"] = 3 }, Array.Empty<string>()),
        ("transfer_items", () => new() { ["categories"] = new JsonArray("gold"), ["from"] = "player", ["to"] = "player", ["dryRun"] = true },
            Array.Empty<string>()),  // expected to fail: from and to are the same
    };

    [McpServerTool]
    [Description("Run SkyLink's smoke test against the running game: about 25 read-only calls, each checked for success and for the " +
        "fields its reply should have, with timings. Use it after an update to check the build in one go. " +
        "includeWrites=true also adds and removes 1 gold and checks the count, the only change it makes. " +
        "Run it outside combat and menus, not during a loading screen.")]
    public async Task<object> RunSelfTest(bool includeWrites = false)
    {
        var results = new List<object>();
        int passed = 0, failed = 0;

        foreach (var (action, makeParams, fields) in ReadOnlyChecks)
        {
            var expectFailure = action == "transfer_items";
            var sw = Stopwatch.StartNew();
            try
            {
                var data = await _pipe.SendRequestAsync(action, makeParams());
                var missing = fields.Where(f => data?[f] == null).ToArray();
                var ok = !expectFailure && missing.Length == 0;
                if (ok) passed++; else failed++;
                results.Add(new
                {
                    action, ok, ms = sw.ElapsedMilliseconds,
                    problem = expectFailure ? "expected an error (same from and to) but the call succeeded"
                            : missing.Length > 0 ? "missing fields: " + string.Join(", ", missing) : null,
                });
            }
            catch (Exception ex)
            {
                var ok = expectFailure;
                if (ok) passed++; else failed++;
                results.Add(new { action, ok, ms = sw.ElapsedMilliseconds, problem = ok ? null : ex.Message });
            }
        }

        if (includeWrites)
        {
            var sw = Stopwatch.StartNew();
            string? problem = null;
            try
            {
                int Gold(JsonNode? d) => d?["gold"]?.GetValue<int>() ?? d?["count"]?.GetValue<int>() ?? -1;
                var before = Gold(await _pipe.SendRequestAsync("get_gold_count"));
                await _pipe.SendRequestAsync("add_item", new JsonObject { ["formId"] = "0000000F", ["count"] = 1 });
                var afterAdd = Gold(await _pipe.SendRequestAsync("get_gold_count"));
                await _pipe.SendRequestAsync("remove_item", new JsonObject { ["formId"] = "0000000F", ["count"] = 1 });
                var afterRemove = Gold(await _pipe.SendRequestAsync("get_gold_count"));
                if (before < 0) problem = "get_gold_count reply has no gold/count field";
                else if (afterAdd != before + 1 || afterRemove != before)
                    problem = $"gold went {before} -> {afterAdd} -> {afterRemove}, expected {before} -> {before + 1} -> {before}";
            }
            catch (Exception ex) { problem = ex.Message; }
            if (problem == null) passed++; else failed++;
            results.Add(new { action = "add_item/remove_item (1 gold)", ok = problem == null, ms = sw.ElapsedMilliseconds, problem });
        }

        return new { passed, failed, allPassed = failed == 0, results };
    }
}
