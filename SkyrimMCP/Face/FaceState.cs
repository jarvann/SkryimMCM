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

    /// <summary>
    /// BUG-046: RaceMenu keeps its own copy of the player's tint layers and hair colour in its quest script and puts
    /// them back on every game load and every time her 3D loads (OnGameReload, On3DLoaded: LoadTints), so makeup set
    /// outside its menu reverted. Its script listens for the mod event RSM_RequestTintSave (SaveHair, SaveTints), which
    /// copies the live tints and hair colour into that store. Sent through SKSE's ModEvent script.
    /// </summary>
    public static async Task<string> RaceMenuSaveTints(IPipeClient pipe)
    {
        // BUG-048: the mod event didn't update the store (save 94 still loaded the old makeup), so call RaceMenu's own
        // SaveHair and SaveTints on its quest script directly, and wait for them to finish.
        var quest = await RaceMenuQuest(pipe);
        if (quest != null)
        {
            try
            {
                foreach (var fn in new[] { "SaveHair", "SaveTints" })
                {
                    var r = await pipe.SendRequestAsync("call_papyrus_function", new JsonObject
                    {
                        ["className"] = "RaceMenu", ["functionName"] = fn, ["args"] = new JsonArray(), ["target"] = quest,
                    });
                    if (r?["error"] is JsonNode error) throw new InvalidOperationException($"RaceMenu.{fn}: {error}");
                }
                return $"RaceMenu's own tint and hair store updated (RaceMenu.SaveHair and SaveTints on quest {quest})";
            }
            catch (Exception ex) { return "RaceMenu tint store not updated: " + ex.Message; }
        }
        return "RaceMenu tint store not updated: RaceMenu's quest wasn't found (is RaceMenu installed?)";
    }

    private static string? _raceMenuQuest;

    /// <summary>The form ID of the quest running RaceMenu's script, found once per server session.</summary>
    private static async Task<string?> RaceMenuQuest(IPipeClient pipe)
    {
        if (_raceMenuQuest != null) return _raceMenuQuest;
        foreach (var editorId in new[] { "RaceMenu", "RaceMenuQuest", "RaceMenuBase" })
        {
            try
            {
                // A harmless member call tells whether this form carries the RaceMenu script.
                var r = await pipe.SendRequestAsync("call_papyrus_function", new JsonObject
                {
                    ["className"] = "RaceMenu", ["functionName"] = "SaveHair", ["args"] = new JsonArray(), ["target"] = editorId,
                });
                if (r?["error"] == null)
                {
                    _raceMenuQuest = r?["target"]?.GetValue<string>() ?? editorId;
                    return _raceMenuQuest;
                }
            }
            catch (Exception) { }
        }
        return null;
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
