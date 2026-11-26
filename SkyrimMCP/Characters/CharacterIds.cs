using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkyrimMCP.Characters;

/// <summary>
/// BUG-149: the game gives the player a new character ID when the race menu closes, so one character's saves carry two IDs
/// (GOG, 2026-11-25: 0698A672 through the tests, DB5A9A2C after the race menu) and SkyLink took her own saves for another
/// character's. This keeps which IDs are one character (character_ids.json: each later ID points at the first), learned when
/// the ID changes in a running game with no load in between, or told with LinkCharacterIds.
/// </summary>
public static class CharacterIds
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(SkyLinkSettings.Folder, "character_ids.json");

    // The last settled ID seen in this game process and load, to notice a change with no load in between.
    private static (int Process, DateTime Started, long Loads, string Id, string? Name)? _last;
    private static (int Process, long Loads, DateTime FirstSeen)? _loadSeen;

    // Right after a load the game can still hold the previous character's ID, then 0, until it settles (BUG-091): IDs seen in
    // the first seconds after a load are not used.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    private static JsonObject Load()
    {
        try { return File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch (Exception) { return new JsonObject(); }
    }

    /// <summary>The first ID of the character this ID belongs to (the ID itself when it was never linked).</summary>
    public static string? Root(string? id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        var key = id.ToUpperInvariant();
        lock (Gate)
        {
            var links = Load()["links"] as JsonObject;
            for (var i = 0; i < 16 && links?[key]?["sameAs"]?.GetValue<string>() is string next; i++) key = next;
        }
        return key;
    }

    /// <summary>Whether two IDs are the same character.</summary>
    public static bool Same(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(Root(a), Root(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Every ID of this character (for matching save file names).</summary>
    public static List<string> All(string? id)
    {
        if (string.IsNullOrEmpty(id)) return [];
        var root = Root(id)!;
        var ids = new List<string> { root };
        lock (Gate)
        {
            if (Load()["links"] is JsonObject links)
                foreach (var (other, _) in links)
                    if (!ids.Contains(other, StringComparer.OrdinalIgnoreCase) && Same(other, root)) ids.Add(other);
        }
        return ids;
    }

    /// <summary>
    /// Records that newId is the same character as oldId, and moves per-character data kept under newId to the first ID where
    /// that has none. Returns false when they already were.
    /// </summary>
    public static bool Link(string newId, string oldId, string name, string why)
    {
        newId = newId.ToUpperInvariant(); oldId = oldId.ToUpperInvariant();
        if (newId == oldId || newId == "00000000" || oldId == "00000000" || Same(newId, oldId)) return false;
        lock (Gate)
        {
            var all = Load();
            var links = all["links"] as JsonObject ?? new JsonObject();
            all["links"] = links;
            // Point the new ID's root at the old ID's root, so a chain of race menus stays one character.
            links[Root(newId)!] = new JsonObject
            {
                ["sameAs"] = Root(oldId), ["name"] = name, ["why"] = why, ["linked"] = DateTime.Now.ToString("s"),
                ["id"] = newId,
            };
            Directory.CreateDirectory(SkyLinkSettings.Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            SkyLinkSettings.ReplaceFile(temp, FilePath);
        }
        FamilyHomes.MergeInto(Root(oldId)!, newId);
        SkyrimMCP.Face.FaceState.MergeInto(Root(oldId)!, newId);
        return true;
    }

    /// <summary>
    /// Called with each get_appearance reply: when the character ID differs from the last one seen in the same game process and
    /// load, with the same player name, both settled, the race menu gave the character a new ID: link them.
    /// </summary>
    public static void Observe(JsonNode? appearance, long? gameLoads)
    {
        var id = appearance?["characterId"]?.GetValue<string>()?.ToUpperInvariant();
        var name = appearance?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id) || id == "00000000" || gameLoads == null || appearance?["characterIdLost"]?.GetValue<bool>() == true) return;
        var game = Process.GetProcessesByName("SkyrimSE").FirstOrDefault();
        if (game == null) return;
        DateTime started;
        try { started = game.StartTime; } catch (Exception) { return; }
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (_loadSeen is not { } seen || seen.Process != game.Id || seen.Loads != gameLoads)
            {
                _loadSeen = (game.Id, gameLoads.Value, now);
                return;   // a new load (or the first look): wait for the ID to settle
            }
            if (now - seen.FirstSeen < Settle) return;
            var last = _last;
            _last = (game.Id, started, gameLoads.Value, id, name);
            if (last is not { } l || l.Process != game.Id || l.Started != started || l.Loads != gameLoads || l.Id == id) return;
            if (!string.Equals(l.Name, name, StringComparison.Ordinal)) return;
            Link(id, l.Id, name ?? "", $"the ID changed from {l.Id} to {id} in a running game with no load in between (the race menu)");
        }
    }
}
