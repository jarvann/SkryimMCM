namespace SkyrimMCP.Face;

/// <summary>
/// RaceMenu's extended face sliders as an install defines them (FEAT-314): each plugin's folder
/// meshes\actors\character\FaceGenMorphs\&lt;plugin&gt;\ holds sliders\*.ini ("SliderName = CategoryFlag, SliderType, Data") and
/// races.ini ("RaceEditorID = sliders\file.ini": which races get which slider file). Read from loose files (MO2's overwrite and
/// mods by priority, then Data) and from the archives, loose first.
/// </summary>
public static class SliderFiles
{
    private const string Root = @"meshes\actors\character\facegenmorphs\";

    public sealed record Slider(string Name, string Plugin, string File, int Category, string Type, string Data, string Source);

    public sealed record Definitions(List<Slider> Sliders, Dictionary<string, List<string>> RaceFiles);

    private static Definitions? _cached;
    private static string? _cachedFor;
    private static readonly object CacheLock = new();

    /// <summary>The category flag's name, as RaceMenu's slider files list them.</summary>
    public static string CategoryName(int flag) => flag switch
    {
        -1 => "Extra", 4 => "Body", 8 => "Head", 16 => "Face", 32 => "Eyes", 64 => "Brow", 128 => "Mouth", 256 => "Hair",
        _ => flag.ToString(),
    };

    /// <summary>Every slider file and races.ini on the install, read once per set of game folders.</summary>
    public static Definitions Read()
    {
        lock (CacheLock)
        {
            var folders = SkyLinkSettings.GameFileFolders();
            var key = string.Join("|", folders);
            if (_cached != null && _cachedFor == key) return _cached;

            // Relative path (lower case) -> (text, source); loose files win over archives.
            var files = new Dictionary<string, (string Text, string Source)>();
            folders.Reverse();   // highest priority first
            foreach (var folder in folders)
            {
                var dir = Path.Combine(folder, "meshes", "actors", "character", "FaceGenMorphs");
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var ini in Directory.EnumerateFiles(dir, "*.ini", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(folder, ini).Replace('/', '\\').ToLowerInvariant();
                        if (!files.ContainsKey(rel)) files[rel] = (SafeRead(ini), "loose");
                    }
                }
                catch { /* unreadable folder: skip it */ }
            }
            foreach (var (path, bsa) in MeshFiles.ArchiveFilesUnder(Root))
            {
                if (!path.EndsWith(".ini", StringComparison.Ordinal) || files.ContainsKey(path)) continue;
                try
                {
                    if (bsa.Read(path) is byte[] bytes)
                        files[path] = (System.Text.Encoding.Latin1.GetString(bytes), "bsa:" + Path.GetFileName(bsa.Path));
                }
                catch { /* a broken entry: skip it */ }
            }

            var sliders = new List<Slider>();
            var raceFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rel, (text, source)) in files)
            {
                if (!rel.StartsWith(Root, StringComparison.Ordinal)) continue;
                var parts = rel[Root.Length..].Split('\\');
                if (parts.Length < 2) continue;
                var plugin = parts[0];
                var inPlugin = string.Join('\\', parts.Skip(1));
                if (inPlugin.Equals("races.ini", StringComparison.Ordinal))
                {
                    foreach (var (race, value) in Lines(text))
                    {
                        var file = Root + plugin + "\\" + value.Replace('/', '\\').Trim().ToLowerInvariant();
                        if (!raceFiles.TryGetValue(race, out var list)) raceFiles[race] = list = new List<string>();
                        if (!list.Contains(file)) list.Add(file);
                    }
                }
                else if (parts.Length >= 3 && parts[1].Equals("sliders", StringComparison.Ordinal))
                {
                    foreach (var (name, value) in Lines(text))
                    {
                        var fields = value.Split(',', 3, StringSplitOptions.TrimEntries);
                        if (fields.Length < 2 || !int.TryParse(fields[0], out var category)) continue;
                        sliders.Add(new Slider(name, plugin, rel, category, fields[1], fields.Length > 2 ? fields[2] : "", source));
                    }
                }
            }
            _cachedFor = key;
            return _cached = new Definitions(sliders, raceFiles);
        }
    }

    // "Key = value" lines; comments (# or ;) and blank lines skipped; the key must be a plain name.
    private static IEnumerable<(string Key, string Value)> Lines(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (key.Length == 0 || !key.All(c => char.IsLetterOrDigit(c) || c == '_')) continue;
            yield return (key, line[(eq + 1)..].Trim());
        }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return ""; }
    }
}
