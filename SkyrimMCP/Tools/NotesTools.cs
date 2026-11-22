using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// Notes and goals kept per character (FEAT-224), in a JSON file per character under
/// Documents\My Games\Skyrim Special Edition\SkyLinkAI\notes. Plain files rather than the
/// SKSE co-save, so they can be read with the game closed and edited by hand.
/// </summary>
[McpServerToolType]
public class NotesTools : ToolBase
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public NotesTools(IPipeClient pipe) : base(pipe) { }

    // INFRA-027: the running game's own folder (Steam and GOG keep separate notes).
    internal static string NotesFolder => Path.Combine(SkyLinkSettings.Folder, "notes");

    [McpServerTool]
    [Description("List the notes and goals kept for this character. Read them at the start of a session and when the player asks what " +
        "they were doing. When the player is near a way to progress an open goal (a location, NPC or item it mentions), point it out. " +
        "includeDone (default false) also lists finished goals. character defaults to the current player's name " +
        "(pass it to read another character's notes, or when the game isn't running).")]
    public async Task<object> GetNotes(bool includeDone = false, string? character = null)
    {
        var who = await ResolveCharacter(character);
        if (who.Error != null) return new { error = who.Error };
        var notes = await Load(who.Name!);
        var shown = notes.Where(n => includeDone || n?["done"]?.GetValue<bool>() != true).Select(n => n!.DeepClone()).ToArray();
        return DeserializeResponse(new JsonObject
        {
            ["character"] = who.Name,
            ["file"] = FileFor(who.Name!),
            ["notes"] = new JsonArray(shown),
            ["openGoals"] = notes.Count(n => n?["kind"]?.GetValue<string>() == "goal" && n?["done"]?.GetValue<bool>() != true),
            ["total"] = notes.Count,
        });
    }

    [McpServerTool]
    [Description("Add a note or goal for this character. kind: 'note' (default) or 'goal'. " +
        "Use goals for things the player wants to achieve (\"finish the Thieves Guild\", \"find a better bow\"); " +
        "notes for things to remember (\"left the Dawnguard armor in the Breezehome chest\"). " +
        "tags (optional, comma-separated) such as a quest, location or NPC name, to match against later.")]
    public async Task<object> AddNote(string text, string kind = "note", string? tags = null, string? character = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return new { error = "text is empty" };
        kind = kind.Trim().ToLowerInvariant();
        if (kind != "note" && kind != "goal") return new { error = "kind must be 'note' or 'goal'" };

        var who = await ResolveCharacter(character);
        if (who.Error != null) return new { error = who.Error };

        JsonObject note = null!;
        await Update(who.Name!, notes =>
        {
            var nextId = notes.Select(n => n?["id"]?.GetValue<int>() ?? 0).DefaultIfEmpty(0).Max() + 1;
            note = new JsonObject
            {
                ["id"] = nextId,
                ["kind"] = kind,
                ["text"] = text.Trim(),
                ["done"] = false,
                ["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            };
            if (!string.IsNullOrWhiteSpace(tags))
                note["tags"] = new JsonArray(tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => (JsonNode)t).ToArray());
            notes.Add(note);
            return true;
        });
        return DeserializeResponse(new JsonObject { ["character"] = who.Name, ["added"] = note.DeepClone() });
    }

    [McpServerTool]
    [Description("Change a note or goal by id (from GetNotes): set done=true when a goal is achieved, or replace its text or tags. " +
        "delete=true removes it instead.")]
    public async Task<object> UpdateNote(int id, bool? done = null, string? text = null, string? tags = null, bool delete = false,
        string? character = null)
    {
        var who = await ResolveCharacter(character);
        if (who.Error != null) return new { error = who.Error };

        JsonNode? result = null;
        var found = await Update(who.Name!, notes =>
        {
            var note = notes.FirstOrDefault(n => n?["id"]?.GetValue<int>() == id) as JsonObject;
            if (note == null) return false;
            if (delete)
            {
                notes.Remove(note);
                result = new JsonObject { ["deleted"] = id };
                return true;
            }
            if (done.HasValue)
            {
                note["done"] = done.Value;
                if (done.Value) note["completed"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                else note.Remove("completed");
            }
            if (!string.IsNullOrWhiteSpace(text)) note["text"] = text.Trim();
            if (tags != null)
                note["tags"] = new JsonArray(tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => (JsonNode)t).ToArray());
            result = note.DeepClone();
            return true;
        });
        if (!found) return new { error = $"No note with id {id} for {who.Name}" };
        if (done == true) await NotifyInGame("Goal complete");
        return DeserializeResponse(new JsonObject { ["character"] = who.Name, ["note"] = result });
    }

    private async Task<(string? Name, string? Error)> ResolveCharacter(string? character)
    {
        if (!string.IsNullOrWhiteSpace(character)) return (character.Trim(), null);
        try
        {
            var info = await _pipe.SendRequestAsync("get_player_info");
            var name = info?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || name == "unknown") return (null, "Couldn't read the character's name; pass character.");
            return (name, null);
        }
        catch (Exception ex)
        {
            return (null, $"Couldn't ask the game for the character's name ({ex.Message}); pass character to use notes without the game.");
        }
    }

    internal static string FileFor(string character)
    {
        var safe = string.Concat(character.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(NotesFolder, (safe.Length == 0 ? "unnamed" : safe) + ".json");
    }

    private static async Task<JsonArray> Load(string character)
    {
        var file = FileFor(character);
        if (!File.Exists(file)) return new JsonArray();
        await FileLock.WaitAsync();
        try
        {
            return JsonNode.Parse(await File.ReadAllTextAsync(file))?["notes"]?.AsArray() ?? new JsonArray();
        }
        finally { FileLock.Release(); }
    }

    // Read, change and write the file under one lock. change returns false to skip the write.
    private static async Task<bool> Update(string character, Func<JsonArray, bool> change)
    {
        var file = FileFor(character);
        await FileLock.WaitAsync();
        try
        {
            var notes = File.Exists(file)
                ? JsonNode.Parse(await File.ReadAllTextAsync(file))?["notes"]?.AsArray() ?? new JsonArray()
                : new JsonArray();
            if (!change(notes)) return false;
            Directory.CreateDirectory(NotesFolder);
            var doc = new JsonObject { ["character"] = character, ["notes"] = notes.DeepClone() };
            var temp = file + ".tmp";
            await File.WriteAllTextAsync(temp, doc.ToJsonString(Indented));
            File.Move(temp, file, overwrite: true);
            return true;
        }
        finally { FileLock.Release(); }
    }
}
