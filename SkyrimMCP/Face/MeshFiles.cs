namespace SkyrimMCP.Face;

/// <summary>
/// Finds a mesh (a path relative to Data\meshes): a loose file (MO2's overwrite and mods by priority, then Data), else
/// inside a BSA archive (FEAT-254).
/// </summary>
public static class MeshFiles
{
    private static List<BsaArchive>? _archives;
    private static string? _archivesFor;
    private static readonly object ArchiveLock = new();

    /// <summary>The mesh's bytes, from a loose file or a BSA; null when it isn't found.</summary>
    public static byte[]? Read(string meshPath)
    {
        if (Find(meshPath) is string loose) return File.ReadAllBytes(loose);
        var inArchive = Path.Combine("meshes", Relative(meshPath));
        foreach (var bsa in Archives())
            if (bsa.Contains(inArchive)) return bsa.Read(inArchive);
        return null;
    }

    /// <summary>Where a mesh comes from: "loose", "bsa" or null.</summary>
    public static string? Source(string meshPath)
    {
        if (Find(meshPath) != null) return "loose";
        var inArchive = Path.Combine("meshes", Relative(meshPath));
        return Archives().Any(b => b.Contains(inArchive)) ? "bsa" : null;
    }

    /// <summary>
    /// Any file inside the archives SkyLink reads (mesh and script archives, not textures or sounds), by its path relative to
    /// Data (BUG-100: GetScriptSource missed RaceMenu's CharGen.psc, packed in RaceMenu.bsa). Returns the bytes and the archive.
    /// </summary>
    public static (byte[] Bytes, string Archive)? ReadFromArchives(string dataRelativePath)
    {
        var p = dataRelativePath.Replace('/', '\\').TrimStart('\\');
        foreach (var bsa in Archives())
            if (bsa.Contains(p) && bsa.Read(p) is byte[] bytes) return (bytes, bsa.Path);
        return null;
    }

    /// <summary>
    /// A texture by its path relative to Data: loose first, then the archives of the plugin it belongs to (its own "... - Textures"
    /// archives, or the base game's). Only those archives are opened: a big load order has hundreds.
    /// </summary>
    public static byte[]? ReadTexture(string dataRelativePath, string plugin)
    {
        var p = dataRelativePath.Replace('/', '\\').TrimStart('\\');
        var folders = SkyLinkSettings.GameFileFolders();
        folders.Reverse();
        foreach (var f in folders)
        {
            var loose = Path.Combine(f, p);
            if (File.Exists(loose)) return File.ReadAllBytes(loose);
        }
        var stem = Path.GetFileNameWithoutExtension(plugin);
        string[] baseGame = ["Skyrim", "Update", "Dawnguard", "HearthFires", "Dragonborn"];
        var stems = baseGame.Contains(stem, StringComparer.OrdinalIgnoreCase) ? baseGame.Prepend(stem) : [stem];
        foreach (var f in folders.Where(Directory.Exists))
            foreach (var st in stems.Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var file in Directory.EnumerateFiles(f, st + "*.bsa").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(file);
                    if (!name.Contains("Textures", StringComparison.OrdinalIgnoreCase) && !Path.GetFileNameWithoutExtension(name).Equals(st, StringComparison.OrdinalIgnoreCase)) continue;
                    if (BsaArchive.Open(file) is { } bsa && bsa.Contains(p) && bsa.Read(p) is byte[] bytes) return bytes;
                }
        return null;
    }

    // BUG-065: a path given relative to Data (meshes\actors\...) was looked up under meshes\meshes, so a follower's loose face
    // file read as missing.
    private static string Relative(string meshPath)
    {
        var p = meshPath.Replace('/', '\\').TrimStart('\\');
        return p.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ? p[7..] : p;
    }

    private static List<BsaArchive> Archives()
    {
        lock (ArchiveLock)
        {
            // INFRA-027: the running game's folders, highest priority first; opened again when they change (another game, profile).
            var folders = SkyLinkSettings.GameFileFolders();
            folders.Reverse();
            var key = string.Join("|", folders);
            if (_archives != null && _archivesFor == key) return _archives;
            _archivesFor = key;
            var list = new List<BsaArchive>();
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.bsa"))
                {
                    // Texture and sound archives hold no meshes.
                    var name = Path.GetFileName(file);
                    if (name.Contains("Textures", StringComparison.OrdinalIgnoreCase) || name.Contains("Sounds", StringComparison.OrdinalIgnoreCase)) continue;
                    if (BsaArchive.Open(file) is { } bsa) list.Add(bsa);
                }
            }
            return _archives = list;
        }
    }

    public static string? Find(string meshPath)
    {
        var relative = Path.Combine("meshes", Relative(meshPath));
        var folders = SkyLinkSettings.GameFileFolders();
        folders.Reverse();   // highest priority first
        return folders.Select(f => Path.Combine(f, relative)).FirstOrDefault(File.Exists);
    }
}
