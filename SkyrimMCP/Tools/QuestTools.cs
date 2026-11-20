using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// MCP Tools for quest operations — quest info, stages, aliases, quest items
/// </summary>
[McpServerToolType]
public class QuestTools : ToolBase
{
    public QuestTools(IPipeClient pipe) : base(pipe) { }

    // The stage after a SetStage: the game applies it on a later frame, and a final stage (255) runs its script first, so one
    // read at 500 ms said "not applied" for a quest that reached 255 (2026-11-20). Poll up to about 4 seconds.
    private async Task<int?> StageAfter(string formId, int stage)
    {
        int? now = null;
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(500);
            try { now = (await _pipe.SendRequestAsync("get_quest_stages", new JsonObject { ["formId"] = formId }))?["currentStage"]?.GetValue<int>(); }
            catch (Exception) { continue; }
            if (now >= stage) return now;
        }
        return now;
    }

    // FEAT-263: quest types whose lines hold the story together; replaying them needs allowMajor.
    static readonly HashSet<string> MajorTypes = new(StringComparer.OrdinalIgnoreCase)
        { "main", "College of Winterhold", "Thieves Guild", "Dark Brotherhood", "Companions", "Civil War", "Dawnguard", "Dragonborn" };

    [McpServerTool]
    [Description("Replay quest progress from a character blueprint (exported with stages done, 1.4+) into this game, quest by quest. " +
        "quests: comma-separated quest names or editor IDs from the blueprint, or 'list' to see which quests have stages done " +
        "there that aren't done here. For each quest it starts the quest if it hasn't started (so its aliases fill), then sets " +
        "each missing stage in order with SetQuestStage's check, stopping that quest at the first stage the game refuses. " +
        "Setting a stage runs its script, so rewards, items, world changes and follow-on quests happen as if played (unlike " +
        "CompleteQuest), but a stage that expects the player somewhere or an NPC alive can misfire. confirm=false (default) makes " +
        "a way-back save and shows the plan; confirm=true with previewSave reuses it (else saves again) and runs it, up to about 25 stages a call. Main, faction, Civil War and DLC lines " +
        "need allowMajor=true. Ask the player first, simple quests first.")]
    public async Task<object> ReplayQuests(string blueprintPath, string quests, bool confirm = false, bool allowMajor = false, string? previewSave = null)
    {
        if (!File.Exists(blueprintPath)) return new { error = $"No blueprint at {blueprintPath}." };
        JsonNode? bp;
        try { bp = JsonNode.Parse(await File.ReadAllTextAsync(blueprintPath)); }
        catch (JsonException ex) { return new { error = "The blueprint isn't valid JSON: " + ex.Message }; }
        var there = (bp?["quests"]?["completed"] as JsonArray ?? new JsonArray()).Concat(bp?["quests"]?["running"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>().Where(q => q["stagesDone"] is JsonArray).ToList();
        if (there.Count == 0)
            return new { error = "This blueprint has no stages done (exported before 1.4, or with stagesFromSave=false). Export it again from the other character." };

        // Map the blueprint's plugin + local ID to this game's form ID.
        var lo = await _pipe.SendRequestAsync("get_load_order");
        var mods = (lo?["mods"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .GroupBy(m => m["name"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        string? Resolve(JsonObject q)
        {
            if (q["formIdSource"] is JsonObject src && src["plugin"]?.ToString() is string plugin && src["localId"]?.ToString() is string local)
            {
                if (!mods.TryGetValue(plugin, out var m) || m["prefix"]?.ToString() is not string prefix) return null;
                var id = Convert.ToUInt32(local, 16);
                return prefix.Length == 5 ? $"{prefix}{id & 0xFFF:X3}" : $"{prefix}{id & 0xFFFFFF:X6}";
            }
            return q["formId"]?.ToString();
        }

        var wayBack = new SaveTools(_pipe);
        var (save, stagesHere, note) = await wayBack.QuestStagesFromSave("SkyLink_BeforeQuestReplay", reuseSave: confirm ? previewSave : null);
        if (stagesHere == null) return new { error = "Couldn't read this game's quest stages: " + note };
        HashSet<int> DoneHere(string? id) =>
            id != null && stagesHere.TryGetValue(id.ToUpperInvariant().PadLeft(8, '0'), out var st)
                ? st.Where(x => x[1] == 1).Select(x => x[0]).ToHashSet() : [];

        var rows = there.Select(q =>
        {
            var id = Resolve(q);
            var done = DoneHere(id);
            var missing = (q["stagesDone"] as JsonArray)!.Select(n => n!.GetValue<int>()).Where(x => !done.Contains(x)).OrderBy(x => x).ToList();
            return new { q, id, name = q["name"]?.ToString() ?? "", editorId = q["editorId"]?.ToString(), type = q["type"]?.ToString() ?? "",
                         doneHere = done, missing };
        }).Where(r => r.missing.Count > 0).ToList();

        if (quests.Trim().Equals("list", StringComparison.OrdinalIgnoreCase))
            return new
            {
                wayBackSave = save, note,
                candidates = rows.OrderBy(r => MajorTypes.Contains(r.type)).ThenBy(r => r.name)
                    .Select(r => new { r.name, r.editorId, r.type, major = MajorTypes.Contains(r.type) ? true : (bool?)null,
                                       notInThisGame = r.id == null ? true : (bool?)null, stagesToSet = r.missing }),
            };

        var wanted = quests.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var picked = new List<(string name, string? editorId, string type, string? id, HashSet<int> doneHere, List<int> missing)>();
        foreach (var w in wanted)
        {
            var r = rows.FirstOrDefault(x => x.name.Equals(w, StringComparison.OrdinalIgnoreCase) || string.Equals(x.editorId, w, StringComparison.OrdinalIgnoreCase));
            if (r == null) return new { error = $"'{w}' has no stages to replay (not in the blueprint, or already done here). ReplayQuests(quests: 'list') shows them." };
            if (r.id == null) return new { error = $"'{w}' comes from a plugin that isn't loaded in this game." };
            if (MajorTypes.Contains(r.type) && !allowMajor) return new { error = $"'{w}' is a {r.type} quest: replaying it can break that line if a stage misfires. Pass allowMajor=true once the player agrees." };
            picked.Add((r.name, r.editorId, r.type, r.id, r.doneHere, r.missing));
        }

        var plan = picked.Select(p => new
        {
            p.name, p.type, formId = p.id, startFirst = p.doneHere.Count == 0, stagesToSet = p.missing,
            warning = MajorTypes.Contains(p.type) ? $"{p.type} line: a misfiring stage can stall it; the way-back save is the fix." : null,
        }).ToList();
        // Each stage takes about a second (up to four for a slow one): keep one call well inside the 80-second limit.
        var total = picked.Sum(p => p.missing.Count);
        if (total > 25)
            return new { error = $"{total} stages in one call is too many to finish in time: replay fewer quests per call (up to about 25 stages).", plan };
        if (!confirm)
            return new { preview = true, wayBackSave = save, note, plan,
                         next = $"Each stage runs its script as if played. If the player agrees, call again with confirm=true and previewSave=\"{save}\" (reused within 10 minutes)." };

        if (await SafetyBlock("face") is string gateReason) return new { error = gateReason, wayBackSave = save };
        var results = new List<object>();
        foreach (var p in picked)
        {
            var log = new List<object>();
            string status = "done";
            if (p.doneHere.Count == 0)
            {
                var started = await _pipe.SendRequestAsync("start_quest", new JsonObject { ["formId"] = p.id });
                var didStart = started?["started"]?.GetValue<bool>() == true;
                log.Add(new { start = didStart });
                const string why = "Required aliases fill only when their conditions hold: some only near their location (when that area is " +
                                   "loaded), some only from the story manager (bounties, radiant jobs), when the giver offers the quest.";
                // The game refuses to start a quest whose required aliases can't fill (round 2026-11-20: start false).
                if (!didStart)
                {
                    results.Add(new { p.name, formId = p.id, status = "skipped: the game wouldn't start it, so its required aliases couldn't fill. " + why, steps = log });
                    continue;
                }
                await Task.Delay(1000);
                try
                {
                    var aliases = await _pipe.SendRequestAsync("get_quest_aliases", new JsonObject { ["formId"] = p.id });
                    var empty = (aliases?["aliases"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                        .Where(a => a["type"]?.ToString() == "reference" && a["optional"]?.GetValue<bool>() != true && a["filled"]?.GetValue<bool>() == false)
                        .Select(a => a["name"]?.ToString()).ToList();
                    if (empty.Count > 0)
                    {
                        await _pipe.SendRequestAsync("stop_quest", new JsonObject { ["formId"] = p.id });
                        results.Add(new { p.name, formId = p.id, status = "skipped: it started, but required aliases stayed empty (" + string.Join(", ", empty.Take(5)) +
                            "), so its stages couldn't run; stopped again. " + why, steps = log });
                        continue;
                    }
                }
                catch (Exception) { }
            }
            foreach (var stage in p.missing)
            {
                await _pipe.SendRequestAsync("set_quest_stage", new JsonObject { ["formId"] = p.id, ["stage"] = stage });
                var now = await StageAfter(p.id!, stage);
                // A lower stage set after a higher one doesn't move the current stage; ascending order keeps this check exact.
                var ok = now >= stage;
                log.Add(new { stage, stageNow = now, applied = ok });
                if (!ok) { status = $"stopped at stage {stage}: the game didn't apply it (aliases empty, or the stage needs something this game doesn't have)"; break; }
            }
            results.Add(new { p.name, formId = p.id, status, steps = log });
        }
        return new { wayBackSave = save, results,
                     next = "Check the journal and nearby NPCs; if something misfired, load the way-back save. Then save from the game menu." };
    }

    [McpServerTool]
    [Description("Set a quest's stage (formId, stage), then read it back and report whether the game applied it (applied, stageNow). " +
        "The game refuses a stage it can't run, for example the final stage of a quest whose aliases (quest giver, reward items) " +
        "were never filled because the quest never really started; the console reports no error in that case, so use this, not " +
        "ExecuteConsoleCommand('setstage ...'). Setting a stage runs its script: rewards, world changes, follow-on quests. Ask " +
        "the player and have them save first.")]
    public async Task<object> SetQuestStage(string formId, int stage)
    {
        if (await SafetyBlock("face") is string gateReason) return new { error = gateReason };  // no scenes, menus, combat
        var before = await _pipe.SendRequestAsync("get_quest_stages", new JsonObject { ["formId"] = formId });
        var r = await _pipe.SendRequestAsync("set_quest_stage", new JsonObject { ["formId"] = formId, ["stage"] = stage });
        var now = await StageAfter(formId, stage);
        return new
        {
            formId, requested = stage, stageBefore = before?["currentStage"]?.GetValue<int>(), stageNow = now,
            applied = now == stage,
            note = now == stage ? null : "The game didn't apply the stage (BUG-085): often the quest isn't really started, so its " +
                "aliases are empty and the stage's script can't run. Start the quest properly, or leave it.",
            console = r?.DeepClone(),
        };
    }

    [McpServerTool]
    [Description("Get information about active quests including objectives and completion status. " +
        "Note: In heavily modded games this may return a large number of quests. " +
        "For a specific quest, use ExecuteConsoleCommand with 'getqueststage <questID>' instead.")]
    public async Task<object> GetQuestInfo()
    {
        var data = await _pipe.SendRequestAsync("get_quest_info");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("List all stages of a specific quest — executed stages, waiting stages, and objectives with their state. " +
        "Use this to see quest progression history and what stages have been completed. " +
        "Get the quest FormID from SearchForms or GetQuestInfo first.")]
    public async Task<object> GetQuestStages(string formId)
    {
        var data = await _pipe.SendRequestAsync("get_quest_stages", new JsonObject { ["formId"] = formId });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get all aliases for a quest — resolves to actual NPCs/objects with their reference IDs. " +
        "Shows if each alias is essential, protected, quest object, alive/dead. " +
        "Use this to find which NPCs are involved in a quest and their current state.")]
    public async Task<object> GetQuestAliases(string formId)
    {
        var data = await _pipe.SendRequestAsync("get_quest_aliases", new JsonObject { ["formId"] = formId });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("List all quest items in the player's inventory — items flagged as quest objects that cannot normally be dropped. " +
        "Shows which items are tied to active quests.")]
    public async Task<object> GetQuestItems()
    {
        var data = await _pipe.SendRequestAsync("get_quest_items");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Read quest variables and running status via native API. " +
        "Returns current stage, running/active/completed status, and objectives. isRunning matches Papyrus Quest.IsRunning; " +
        "isActive is the journal's active-quest flag (shown or tracked), which a stopped quest can keep. " +
        "This is a convenience wrapper around GetQuestStages for quick status checks.")]
    public async Task<object> ReadQuestVariables(string questFormId)
    {
        var data = await _pipe.SendRequestAsync("get_quest_stage", new JsonObject
        {
            ["formId"] = questFormId
        });
        return DeserializeResponse(data);
    }
}
