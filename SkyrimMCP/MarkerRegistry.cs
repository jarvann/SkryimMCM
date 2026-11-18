using System.Text.Json.Nodes;

namespace SkyrimMCP;

/// <summary>
/// The run-time markers SkyLink placed (studio trips, Teleport starting spots), so a list of XMarkers in the world can
/// say which are SkyLink's and which belong to other mods (BUG-074: one marker in the list moved between calls).
/// </summary>
public static class MarkerRegistry
{
    private static string FilePath => Path.Combine(SkyLinkSettings.Folder, "markers_created.json");
    private static readonly object Lock = new();

    public static void Add(string? markerId, string purpose)
    {
        if (string.IsNullOrWhiteSpace(markerId)) return;
        lock (Lock)
        {
            var all = Load();
            all[markerId.Trim().ToUpperInvariant()] = new JsonObject { ["purpose"] = purpose, ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            Save(all);
        }
    }

    public static void Remove(string? markerId)
    {
        if (string.IsNullOrWhiteSpace(markerId)) return;
        lock (Lock)
        {
            var all = Load();
            if (all.Remove(markerId.Trim().ToUpperInvariant())) Save(all);
        }
    }

    public static JsonObject? Get(string? markerId)
    {
        if (string.IsNullOrWhiteSpace(markerId)) return null;
        lock (Lock) return Load()[markerId.Trim().ToUpperInvariant()]?.DeepClone() as JsonObject;
    }

    private static JsonObject Load()
    {
        try { if (File.Exists(FilePath)) return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject(); }
        catch (Exception) { }
        return new JsonObject();
    }

    private static void Save(JsonObject all)
    {
        try
        {
            Directory.CreateDirectory(SkyLinkSettings.Folder);
            File.WriteAllText(FilePath, all.ToJsonString());
        }
        catch (Exception) { }
    }
}
