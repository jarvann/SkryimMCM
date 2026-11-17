namespace SkyrimMCP.Face;

/// <summary>Finds a loose mesh file (a path relative to Data\meshes): MO2's overwrite and mods by priority, then Data.</summary>
public static class MeshFiles
{
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
