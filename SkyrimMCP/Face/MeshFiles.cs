namespace SkyrimMCP.Face;

/// <summary>
/// Finds a mesh (a path relative to Data\meshes): a loose file (MO2's overwrite and mods by priority, then Data), else
/// inside a BSA archive (FEAT-254).
/// </summary>
public static class MeshFiles
{
    private static List<BsaArchive>? _archives;
    private static readonly object ArchiveLock = new();

    /// <summary>The mesh's bytes, from a loose file or a BSA; null when it isn't found.</summary>
    public static byte[]? Read(string meshPath)
    {
        if (Find(meshPath) is string loose) return File.ReadAllBytes(loose);
        var inArchive = Path.Combine("meshes", meshPath.Replace('/', '\\').TrimStart('\\'));
        foreach (var bsa in Archives())
            if (bsa.Contains(inArchive)) return bsa.Read(inArchive);
        return null;
    }

    /// <summary>Where a mesh comes from: "loose", "bsa" or null.</summary>
    public static string? Source(string meshPath)
    {
        if (Find(meshPath) != null) return "loose";
        var inArchive = Path.Combine("meshes", meshPath.Replace('/', '\\').TrimStart('\\'));
        return Archives().Any(b => b.Contains(inArchive)) ? "bsa" : null;
    }

    private static List<BsaArchive> Archives()
    {
        lock (ArchiveLock)
        {
            if (_archives != null) return _archives;
            var folders = new List<string>();
            var settings = SkyLinkSettings.Load();
            if (settings["mo2Path"]?.GetValue<string>() is { Length: > 0 } mo2)
            {
                try
                {
                    foreach (var mod in SkyLinkSettings.Mo2ModsByPriority(mo2, settings["mo2Profile"]?.GetValue<string>()))
                        folders.Add(Path.Combine(mo2, "mods", mod));
                }
                catch (Exception) { }
            }
            if (SkyLinkSettings.DataPath() is string data) folders.Add(data);
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
        var relative = Path.Combine("meshes", meshPath.Replace('/', '\\').TrimStart('\\'));
        var settings = SkyLinkSettings.Load();
        var candidates = new List<string>();
        if (settings["mo2Path"]?.GetValue<string>() is { Length: > 0 } mo2)
        {
            candidates.Add(Path.Combine(mo2, "overwrite", relative));
            try
            {
                foreach (var mod in SkyLinkSettings.Mo2ModsByPriority(mo2, settings["mo2Profile"]?.GetValue<string>()))
                    candidates.Add(Path.Combine(mo2, "mods", mod, relative));
            }
            catch (Exception) { /* fall through to Data */ }
        }
        if (SkyLinkSettings.DataPath() is string data) candidates.Add(Path.Combine(data, relative));
        return candidates.FirstOrDefault(File.Exists);
    }
}
