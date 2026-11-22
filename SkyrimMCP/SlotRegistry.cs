using System.Text.Json.Nodes;
using SkyrimMCP.Face;

namespace SkyrimMCP;

/// <summary>
/// INFRA-024: SkyLinkAI.esl's NPC slots are shared by every save of a game, because a slot's face files sit in the game's folders.
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

    public static int[] Reserved() => Load().Select(kv => int.TryParse(kv.Key, out var i) ? i : -1).Where(i => i >= 0).ToArray();

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
                if (f?.ToString() is string path && path.Contains("SkyLinkAI.esl", StringComparison.OrdinalIgnoreCase))
                    try { if (File.Exists(path)) { File.Delete(path); deleted++; } } catch (Exception) { }
            all.Remove(slot.ToString());
            Save(all);
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
        return (Path.Combine(root, "meshes", "actors", "character", "facegendata", "facegeom", "SkyLinkAI.esl", slotObjectId + ".nif"),
                Path.Combine(root, "textures", "actors", "character", "facegendata", "facetint", "SkyLinkAI.esl", slotObjectId + ".dds"));
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
            mesh = NifTextures.Replace(mesh, path => IsFaceTint(path) ? $@"data\textures\actors\character\facegendata\facetint\SkyLinkAI.esl\{slotObjectId}.dds" : null).Bytes;
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
