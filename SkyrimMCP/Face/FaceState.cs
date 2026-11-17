using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkyrimMCP.Face;

/// <summary>
/// Face settings SkyLink keeps per character (FEAT-241), in Documents\My Games\Skyrim Special Edition\SkyLinkAI\face_state.json.
/// The brow colour lives here because nothing in the game keeps it: Game.UpdateHairColor and every 3D reload (a cell
/// change, a save load) put the hair colour back on the brows, and RaceMenu's node override doesn't survive that.
/// </summary>
public static class FaceState
{
    private static string FilePath => Path.Combine(SkyLinkSettings.Folder, "face_state.json");
    private static readonly object Lock = new();

    /// <summary>The character key: name and race, from get_appearance.</summary>
    public static string? Key(JsonNode? appearance)
    {
        var name = appearance?["name"]?.GetValue<string>();
        var race = appearance?["race"]?.GetValue<string>();
        return string.IsNullOrEmpty(name) ? null : $"{name}|{race}";
    }

    public static string? GetBrowColor(string? key)
    {
        if (key == null) return null;
        lock (Lock) return Load()[key]?["browColor"]?.GetValue<string>();
    }

    public static void SetBrowColor(string? key, string? color)
    {
        if (key == null) return;
        lock (Lock)
        {
            var all = Load();
            var entry = all[key] as JsonObject ?? new JsonObject();
            if (color == null) entry.Remove("browColor");
            else entry["browColor"] = color;
            all[key] = entry;
            Directory.CreateDirectory(SkyLinkSettings.Folder);
            File.WriteAllText(FilePath, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Puts the character's stored brow colour back on the loaded head. Returns the colour, or null if none is stored.</summary>
    public static async Task<string?> ReapplyBrowColor(IPipeClient pipe)
    {
        try
        {
            var color = GetBrowColor(Key(await pipe.SendRequestAsync("get_appearance")));
            if (color != null) await pipe.SendRequestAsync("head_part_tint", new JsonObject { ["type"] = 6, ["color"] = color });
            return color;
        }
        catch (Exception) { return null; /* cosmetic: never fail a face tool over it */ }
    }

    private static JsonObject Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject();
        }
        catch (Exception) { /* unreadable: start again */ }
        return new JsonObject();
    }
}
