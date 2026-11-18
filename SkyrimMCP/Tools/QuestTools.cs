using System.ComponentModel;
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
        await Task.Delay(500);
        var after = await _pipe.SendRequestAsync("get_quest_stages", new JsonObject { ["formId"] = formId });
        var now = after?["currentStage"]?.GetValue<int>();
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
        "Returns current stage, running/active/completed status, and objectives. " +
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
