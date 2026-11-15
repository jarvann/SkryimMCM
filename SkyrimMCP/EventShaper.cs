using System.Text.Json.Nodes;

namespace SkyrimMCP;

/// <summary>
/// Keeps poll_events replies small (INFRA-015). One poll once returned 500 events and about
/// 59,000 characters, 365 of them quests starting and stopping, 250 from a single quest.
///
/// The plugin hands over its whole queue on each poll, so events that don't fit in one reply
/// wait here for the next poll instead of being lost. Quests that start and stop over and over
/// in one batch are collapsed into a single quest_churn event.
/// </summary>
public sealed class EventShaper
{
    /// <summary>A quest with at least this many start/stop events in one batch is collapsed.</summary>
    public const int ChurnThreshold = 3;

    /// <summary>Events held back from earlier polls are capped so a long session can't grow it forever.</summary>
    public const int MaxBacklog = 2000;

    private readonly object _lock = new();
    private readonly List<JsonNode> _backlog = new();
    private int _droppedFromBacklog;

    /// <summary>Process-wide instance: tool classes are created per call.</summary>
    public static EventShaper Shared { get; } = new();

    /// <summary>
    /// Adds newly drained events to the backlog and returns the oldest <paramref name="maxEvents"/>,
    /// in order, after collapsing quest churn.
    /// </summary>
    public (JsonArray Events, int Remaining, JsonArray Collapsed, int Dropped) Take(JsonArray? drained, int maxEvents, bool collapseQuestChurn)
    {
        lock (_lock)
        {
            if (drained != null)
            {
                foreach (var e in drained)
                    if (e != null) _backlog.Add(e.DeepClone());
            }

            var collapsed = new JsonArray();
            if (collapseQuestChurn) CollapseChurn(_backlog, collapsed);

            if (_backlog.Count > MaxBacklog)
            {
                var excess = _backlog.Count - MaxBacklog;
                _backlog.RemoveRange(0, excess);
                _droppedFromBacklog += excess;
            }

            var take = maxEvents <= 0 ? _backlog.Count : Math.Min(maxEvents, _backlog.Count);
            var events = new JsonArray();
            foreach (var e in _backlog.Take(take)) events.Add(e.DeepClone());
            _backlog.RemoveRange(0, take);

            var dropped = _droppedFromBacklog;
            _droppedFromBacklog = 0;
            return (events, _backlog.Count, collapsed, dropped);
        }
    }

    /// <summary>
    /// Replaces each churning quest's start/stop events with one quest_churn event, placed where
    /// its last start/stop was so the order of everything else is kept.
    /// </summary>
    internal static void CollapseChurn(List<JsonNode> events, JsonArray collapsedSummary)
    {
        var byQuest = new Dictionary<string, List<int>>();
        for (var i = 0; i < events.Count; i++)
        {
            if (EventName(events[i]) != "quest_start_stop") continue;
            var quest = events[i]["data"]?["questFormId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(quest)) continue;
            if (!byQuest.TryGetValue(quest, out var list)) byQuest[quest] = list = new List<int>();
            list.Add(i);
        }

        var remove = new HashSet<int>();
        var replaceAt = new Dictionary<int, JsonNode>();
        foreach (var (quest, indexes) in byQuest)
        {
            if (indexes.Count < ChurnThreshold) continue;

            int starts = 0, stops = 0;
            foreach (var i in indexes)
            {
                if (events[i]["data"]?["started"]?.GetValue<bool>() == true) starts++;
                else stops++;
            }
            var last = events[indexes[^1]];
            var name = last["data"]?["questName"]?.GetValue<string>() ?? quest;
            var running = last["data"]?["started"]?.GetValue<bool>() == true;

            var summary = new JsonObject
            {
                ["type"] = "event",
                ["event"] = "quest_churn",
                ["data"] = new JsonObject
                {
                    ["questFormId"] = quest,
                    ["questName"] = name,
                    ["starts"] = starts,
                    ["stops"] = stops,
                    ["runningNow"] = running,
                },
            };
            foreach (var i in indexes) remove.Add(i);
            replaceAt[indexes[^1]] = summary;
            collapsedSummary.Add(new JsonObject { ["questFormId"] = quest, ["questName"] = name, ["events"] = indexes.Count });
        }

        if (remove.Count == 0) return;

        var result = new List<JsonNode>(events.Count - remove.Count + replaceAt.Count);
        for (var i = 0; i < events.Count; i++)
        {
            if (replaceAt.TryGetValue(i, out var summary)) result.Add(summary);
            else if (!remove.Contains(i)) result.Add(events[i]);
        }
        events.Clear();
        events.AddRange(result);
    }

    private static string? EventName(JsonNode? e) =>
        e?["event"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
