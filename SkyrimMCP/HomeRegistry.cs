using System.Text.Json.Nodes;

namespace SkyrimMCP;

/// <summary>
/// FEAT-307: the pieces of each home SkyLink built, by its centre marker. Saving or removing a home uses this list, never "anything
/// placed at run time nearby": the game makes objects of its own around a home (critters, 22 copies of a vanilla static seen on GOG,
/// 2026-11-23), and RemoveHomeLayout would have deleted them. References are a save's own: a home missing from an older save is skipped.
/// </summary>
public static class HomeRegistry
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(SkyLinkSettings.Folder, "homes.json");

    public static JsonObject Load()
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
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    public static void Record(string center, string name, string? characterId, IEnumerable<string> pieces)
    {
        var all = Load();
        all[center.ToUpperInvariant()] = new JsonObject
        {
            ["name"] = name, ["characterId"] = characterId, ["recorded"] = DateTime.Now.ToString("s"),
            ["pieces"] = new JsonArray(pieces.Select(p => (JsonNode)p.ToUpperInvariant()).Distinct().ToArray()),
        };
        Save(all);
    }

    public static HashSet<string>? Pieces(string center) =>
        Load()[center.Trim().ToUpperInvariant()]?["pieces"] is JsonArray a
            ? a.Select(p => p?.ToString() ?? "").Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

    public static void Forget(string center)
    {
        var all = Load();
        if (all.Remove(center.Trim().ToUpperInvariant())) Save(all);
    }
}
