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

    /// <summary>Counts live face edits in this server session, so a cached measurement can tell it is out of date (FEAT-248).</summary>
    public static int FaceEdits;
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

    /// <summary>
    /// Notes on what a slider does on this character's head (FEAT-243): "inert", or the measured effect. Kept so
    /// later sessions skip sliders already shown to do nothing. A null note removes the entry.
    /// </summary>
    public static JsonObject SetSliderNote(string? key, string slider, string? note)
    {
        if (key == null) return new JsonObject();
        lock (Lock)
        {
            var all = Load();
            var entry = all[key] as JsonObject ?? new JsonObject();
            var notes = entry["sliderNotes"] as JsonObject ?? new JsonObject();
            if (string.IsNullOrWhiteSpace(note)) notes.Remove(slider);
            else notes[slider] = new JsonObject { ["note"] = note.Trim(), ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            entry["sliderNotes"] = notes;
            all[key] = entry;
            Directory.CreateDirectory(SkyLinkSettings.Folder);
            File.WriteAllText(FilePath, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (JsonObject)notes.DeepClone();
        }
    }

    public static JsonObject GetSliderNotes(string? key)
    {
        if (key == null) return new JsonObject();
        lock (Lock) return Load()[key]?["sliderNotes"]?.DeepClone() as JsonObject ?? new JsonObject();
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
