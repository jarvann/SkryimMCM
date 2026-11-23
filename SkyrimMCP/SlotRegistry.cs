using System.Text.Json.Nodes;
using SkyrimMCP.Face;

namespace SkyrimMCP;

/// <summary>
/// INFRA-024: SkyLinkAI.esp's NPC slots are shared by every save of a game, because a slot's face files sit in the game's folders.
/// The registry (slots.json in the running game's SkyLink folder) keeps a slot reserved for its character until it is removed,
/// whatever save is loaded. Faces: the mesh and the tint both, or neither; only the mesh crashed the game as soon as the camera
/// framed the character (GOG, 2026-11-22).
/// </summary>
public static class SlotRegistry
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(SkyLinkSettings.Folder, "slots.json");

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

    /// <summary>
    /// Slots not to fill: those taken, and those freed while this game has been running. The game lists the face files it finds at
    /// startup; a slot freed mid-session has had its files deleted, and a new character put in it straight away made the game load
    /// a face file that was gone, which crashed it (BUG-108, GOG 2026-11-23). Such a slot is free again from the next game start.
    /// </summary>
    public static int[] Reserved()
    {
        var taken = Load().Select(kv => int.TryParse(kv.Key, out var i) ? i : -1).Where(i => i >= 0);
        return taken.Concat(FreedThisSession()).Distinct().ToArray();
    }

    private static string FreedPath => Path.Combine(SkyLinkSettings.Folder, "slots_freed.json");

    private static IEnumerable<int> FreedThisSession()
    {
        DateTime started;
        try
        {
            using var game = System.Diagnostics.Process.GetProcessesByName("SkyrimSE").FirstOrDefault();
            if (game == null) return [];
            started = game.StartTime.ToUniversalTime();
        }
        catch (Exception) { return []; }
        JsonObject freed;
        lock (Gate)
        {
            try { freed = File.Exists(FreedPath) ? JsonNode.Parse(File.ReadAllText(FreedPath)) as JsonObject ?? new JsonObject() : new JsonObject(); }
            catch (Exception) { return []; }
        }
        return freed.Where(kv => kv.Value is JsonValue v && v.TryGetValue<DateTime>(out var when) && when.ToUniversalTime() > started)
            .Select(kv => int.TryParse(kv.Key, out var i) ? i : -1).Where(i => i >= 0).ToList();
    }

    private static void NoteFreed(int slot)
    {
        lock (Gate)
        {
            try
            {
                var freed = File.Exists(FreedPath) ? JsonNode.Parse(File.ReadAllText(FreedPath)) as JsonObject ?? new JsonObject() : new JsonObject();
                freed[slot.ToString()] = DateTime.UtcNow;
                Directory.CreateDirectory(SkyLinkSettings.Folder);
                File.WriteAllText(FreedPath, freed.ToJsonString());
            }
            catch (Exception) { }
        }
    }

    public static void Reserve(int slot, JsonObject entry)
    {
        var all = Load();
        all[slot.ToString()] = entry;
        Save(all);
    }

    /// <summary>Forgets the slot and deletes the face files made for it. Returns how many files went.</summary>
    public static int Release(int slot)
    {
        var all = Load();
        var deleted = 0;
        if (all[slot.ToString()] is JsonObject entry)
        {
            foreach (var f in entry["files"] as JsonArray ?? [])
                if (f?.ToString() is string path && path.Contains("SkyLinkAI.esp", StringComparison.OrdinalIgnoreCase))
                    try { if (File.Exists(path)) { File.Delete(path); deleted++; } } catch (Exception) { }
            all.Remove(slot.ToString());
            Save(all);
            NoteFreed(slot);
        }
        return deleted;
    }

    public static int? SlotForRef(string refId) =>
        Load().Where(kv => string.Equals(kv.Value?["refId"]?.ToString(), refId, StringComparison.OrdinalIgnoreCase))
            .Select(kv => int.TryParse(kv.Key, out var i) ? (int?)i : null).FirstOrDefault();

    /// <summary>Where a slot's face files go (the running game's Data, or MO2's overwrite).</summary>
    public static (string mesh, string tint)? FaceFiles(string slotObjectId)
    {
        if (SkyLinkSettings.WritableDataFolder() is not string root) return null;
        return (Path.Combine(root, "meshes", "actors", "character", "facegendata", "facegeom", "SkyLinkAI.esp", slotObjectId + ".nif"),
                Path.Combine(root, "textures", "actors", "character", "facegendata", "facetint", "SkyLinkAI.esp", slotObjectId + ".dds"));
    }

    /// <summary>
    /// Whether the running game already knows this slot's face files: it lists the loose files as it starts, so ones added later aren't seen
    /// until the next start, but a file it knows can be replaced (both seen on Steam, 2026-11-22).
    /// </summary>
    public static bool KnownToTheGame(string slotObjectId)
    {
        try
        {
            if (FaceFiles(slotObjectId) is not { } files || !File.Exists(files.mesh) || !File.Exists(files.tint)) return false;
            using var game = System.Diagnostics.Process.GetProcessesByName("SkyrimSE").FirstOrDefault();
            return game != null && File.GetCreationTime(files.mesh) < game.StartTime && File.GetCreationTime(files.tint) < game.StartTime;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Writes both files for a slot, or neither. Null when done, else why not.</summary>
    public static string? WriteFace(string slotObjectId, byte[]? mesh, byte[]? tint, out List<string> written)
    {
        written = [];
        if (mesh is not { Length: > 0 }) return "the face mesh wasn't found";
        if (tint is not { Length: > 0 }) return "the face's skin texture (tint) wasn't found, and a mesh without it can crash the game";
        if (FaceFiles(slotObjectId) is not { } files) return "the game's Data folder wasn't found";
        // The mesh names its skin texture by path: an exported head's temporary one, or the template's. Make it this slot's.
        try
        {
            mesh = NifTextures.Replace(mesh, path => IsFaceTint(path) ? $@"data\textures\actors\character\facegendata\facetint\SkyLinkAI.esp\{slotObjectId}.dds" : null).Bytes;
        }
        catch (Exception ex) { return $"the face mesh couldn't be read ({ex.Message})"; }
        try
        {
            foreach (var (path, bytes) in new[] { (files.mesh, mesh), (files.tint, tint) })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                written.Add(path);
            }
            return null;
        }
        catch (Exception ex)
        {
            foreach (var w in written) try { File.Delete(w); } catch (Exception) { }
            written.Clear();
            return $"couldn't write the face files: {ex.Message}";
        }
    }

    // A FaceGen mesh's tint: RaceMenu's export names ...\SKSE\Plugins\CharGen\<name>.dds, the game's own ...\FaceTint\<plugin>\<id>.dds.
    private static bool IsFaceTint(string path)
    {
        var p = path.Replace('/', '\\').ToLowerInvariant();
        return p.Contains(@"\facegendata\facetint\") || p.Contains(@"\skse\plugins\chargen\");
    }

    /// <summary>The face files of an existing NPC, loose or packed: FaceGeom\plugin\id.nif and FaceTint\plugin\id.dds.</summary>
    public static (byte[]? mesh, byte[]? tint) ReadFace(string plugin, string objectId)
    {
        var mesh = MeshFiles.Read($@"actors\character\facegendata\facegeom\{plugin}\{objectId}.nif");
        var tint = MeshFiles.ReadTexture($@"textures\actors\character\facegendata\facetint\{plugin}\{objectId}.dds", plugin);
        return (mesh, tint);
    }
}
