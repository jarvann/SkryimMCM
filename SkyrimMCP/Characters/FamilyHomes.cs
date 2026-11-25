using System.Text.Json.Nodes;

namespace SkyrimMCP.Characters;

/// <summary>
/// FEAT-307: the family's homes, per player character (characterId): the player's own houses or SkyLink homes. Cory, 2026-11-23:
/// once the player uses the family features, "at least one home designated for the family faction/clan".
/// </summary>
public static class FamilyHomes
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(SkyLinkSettings.Folder, "family.json");

    private static JsonObject Load()
    {
        lock (Gate)
        {
            try { return File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject() : new JsonObject(); }
            catch (Exception) { return new JsonObject(); }
        }
    }

    private static void Save(JsonObject all)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(SkyLinkSettings.Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, all.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            SkyLinkSettings.ReplaceFile(temp, FilePath);
        }
    }

    /// <summary>The family homes of this player character, first is the main one.</summary>
    public static List<JsonObject> Of(string? characterId) =>
        characterId != null && Load()[characterId]?["homes"] is JsonArray a ? a.OfType<JsonObject>().Select(h => h.DeepClone().AsObject()).ToList() : [];

    public static void Add(string characterId, JsonObject home, bool main)
    {
        var all = Load();
        var mine = all[characterId] as JsonObject ?? new JsonObject();
        var homes = (mine["homes"] as JsonArray)?.OfType<JsonObject>().Where(h => h["ref"]?.ToString() != home["ref"]?.ToString())
            .Select(h => (JsonNode)h.DeepClone()).ToList() ?? [];
        if (main) homes.Insert(0, home); else homes.Add(home);
        mine["homes"] = new JsonArray(homes.ToArray());
        all[characterId] = mine;
        Save(all);
    }

    /// <summary>FEAT-308: the family story this player character told (homeland, standing, wealth...), empty when none yet.</summary>
    public static JsonObject Story(string? characterId) =>
        characterId != null && Load()[characterId]?["story"] is JsonObject s ? s.DeepClone().AsObject() : new JsonObject();

    /// <summary>Replaces the family story (null clears it).</summary>
    public static void SetStory(string characterId, JsonObject? story)
    {
        var all = Load();
        var mine = all[characterId] as JsonObject ?? new JsonObject();
        if (story == null) mine.Remove("story"); else mine["story"] = story;
        all[characterId] = mine;
        Save(all);
    }

    /// <summary>Drops a home from every player character's family homes (it was taken away). Returns how many lists it was in.</summary>
    public static int RemoveEverywhere(string reference)
    {
        var all = Load();
        var count = 0;
        foreach (var (_, mine) in all)
        {
            if (mine?["homes"] is not JsonArray homes) continue;
            var keep = homes.OfType<JsonObject>().Where(h => !string.Equals(h["ref"]?.ToString(), reference, StringComparison.OrdinalIgnoreCase))
                .Select(h => (JsonNode)h.DeepClone()).ToArray();
            if (keep.Length == homes.Count) continue;
            mine["homes"] = new JsonArray(keep);
            count++;
        }
        if (count > 0) Save(all);
        return count;
    }

    public static bool Remove(string characterId, string refOrName)
    {
        var all = Load();
        if (all[characterId]?["homes"] is not JsonArray homes) return false;
        var keep = homes.OfType<JsonObject>().Where(h => !string.Equals(h["ref"]?.ToString(), refOrName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(h["name"]?.ToString(), refOrName, StringComparison.OrdinalIgnoreCase)).Select(h => (JsonNode)h.DeepClone()).ToArray();
        if (keep.Length == homes.Count) return false;
        all[characterId]!["homes"] = new JsonArray(keep);
        Save(all);
        return true;
    }
}
