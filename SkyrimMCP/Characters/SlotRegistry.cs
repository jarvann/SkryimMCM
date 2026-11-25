using System.Text.Json.Nodes;
using SkyrimMCP.Face;

namespace SkyrimMCP.Characters;

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
            SkyLinkSettings.ReplaceFile(temp, FilePath);
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

    /// <summary>
    /// Forgets the slot and puts stand-ins over the face files made for it (FEAT-305): the game keeps knowing the files, so the slot's
    /// next character shows its face at once, and a save that still holds this character finds a file rather than none (BUG-108).
    /// Returns how many files were cleared.
    /// </summary>
    public static int Release(int slot)
    {
        var all = Load();
        var deleted = 0;
        if (all[slot.ToString()] is JsonObject entry)
        {
            foreach (var f in entry["files"] as JsonArray ?? [])
                if (f?.ToString() is string path && path.Contains("SkyLinkAI.esp", StringComparison.OrdinalIgnoreCase))
                    try
                    {
                        if (!File.Exists(path)) continue;
                        File.WriteAllBytes(path, path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? PlaceholderFace.Tint() : PlaceholderFace.Mesh());
                        deleted++;
                    }
                    catch (Exception) { }
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
    public static (string mesh, string tint)? FaceFiles(string slotObjectId) =>
        SkyLinkSettings.WritableDataFolder() is string root ? FaceFiles(slotObjectId, root) : null;

    /// <summary>The same, in a folder already found: finding it looks at the running processes, so a pass over many slots finds it once.</summary>
    private static (string mesh, string tint) FaceFiles(string slotObjectId, string root)
    {
        return (Path.Combine(root, "meshes", "actors", "character", "facegendata", "facegeom", "SkyLinkAI.esp", slotObjectId + ".nif"),
                Path.Combine(root, "textures", "actors", "character", "facegendata", "facetint", "SkyLinkAI.esp", slotObjectId + ".dds"));
    }

    /// <summary>
    /// Whether the running game already knows this slot's face files: it lists the loose files as it starts, so ones added later aren't seen
    /// until the next start, but a file it knows can be replaced (both seen on Steam, 2026-11-22).
    /// </summary>
    public static bool KnownToTheGame(string slotObjectId) => KnownToTheGame(slotObjectId, GameStarted(), SkyLinkSettings.WritableDataFolder());

    private static bool KnownToTheGame(string slotObjectId, DateTime? started, string? root)
    {
        try
        {
            if (started is not DateTime start || root == null) return false;
            var files = FaceFiles(slotObjectId, root);
            if (!File.Exists(files.mesh) || !File.Exists(files.tint)) return false;
            return File.GetCreationTime(files.mesh) < start && File.GetCreationTime(files.tint) < start;
        }
        catch (Exception) { return false; }
    }

    private static DateTime? GameStarted()
    {
        try
        {
            using var game = System.Diagnostics.Process.GetProcessesByName("SkyrimSE").FirstOrDefault();
            return game?.StartTime;
        }
        catch (Exception) { return null; }
    }

    // SkyLinkAI.esp: 500 slots from object ID 0x800, in blocks of 25 per race and sex (10 races, women then men).
    public const int SlotCount = 500, SlotsPerBlock = 25;
    public static string SlotObjectId(int slot) => (0x800 + slot).ToString("X8");

    /// <summary>
    /// Stand-ins kept ready per race and sex. Two let only two new characters of a kind show their faces at once in a session: the
    /// third (a family of three Nord women) waited for a restart (BUG-124, GOG 2026-11-25). Five files of each kind are a few KB.
    /// </summary>
    public const int StandInsPerBlock = 5;

    /// <summary>
    /// FEAT-305: makes sure the first few free slots of each race and sex have face files, stand-ins where none exist, so that from
    /// the next game start a new character's face shows at once. Returns the files written, or why none could be.
    /// </summary>
    public static (int Written, string? Problem) PrepareFaces(int perBlock = StandInsPerBlock)
    {
        if (SkyLinkSettings.WritableDataFolder() is not string root) return (0, "the game's Data folder wasn't found");
        if (!SkyLinkSettings.GameFileFolders().Any(f => File.Exists(Path.Combine(f, "SkyLinkAI.esp")))) return (0, "SkyLinkAI.esp isn't installed");
        var taken = Reserved().ToHashSet();
        var written = 0;
        try
        {
            for (var block = 0; block < SlotCount / SlotsPerBlock; block++)
            {
                var ready = 0;
                for (var slot = block * SlotsPerBlock; slot < (block + 1) * SlotsPerBlock && ready < perBlock; slot++)
                {
                    if (taken.Contains(slot)) continue;
                    ready++;
                    var (mesh, tint) = FaceFiles(SlotObjectId(slot), root);
                    foreach (var (path, bytes) in new[] { (mesh, (Func<byte[]>)PlaceholderFace.Mesh), (tint, PlaceholderFace.Tint) })
                    {
                        if (File.Exists(path)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllBytes(path, bytes());
                        written++;
                    }
                }
            }
            return (written, null);
        }
        catch (Exception ex) { return (written, ex.Message); }
    }

    /// <summary>Free slots whose face files the running game listed when it started: a character put in one shows its face at once.</summary>
    public static int[] KnownFreeSlots()
    {
        if (GameStarted() is not DateTime started || SkyLinkSettings.WritableDataFolder() is not string root) return [];
        var taken = Reserved().ToHashSet();
        return Enumerable.Range(0, SlotCount).Where(i => !taken.Contains(i) && KnownToTheGame(SlotObjectId(i), started, root)).ToArray();
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
