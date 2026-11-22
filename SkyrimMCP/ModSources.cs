using System.Text.Json;
using System.Text.RegularExpressions;

namespace SkyrimMCP;

/// <summary>
/// FEAT-296: which mod put a file into the game. Vortex records every deployed file in vortex.deployment.json (hundreds of MB on a big
/// setup: streamed once for all the files asked about); MO2 keeps each mod in its own folder with a meta.ini.
/// </summary>
public static partial class ModSources
{
    /// <summary>Name is the mod manager's folder name; NexusId when it is in the folder name (Vortex) or meta.ini (MO2).</summary>
    public sealed record Mod(string Name, string Folder, int? NexusId)
    {
        public string? Link => NexusId is int id ? $"https://www.nexusmods.com/skyrimspecialedition/mods/{id}" : null;
    }

    /// <summary>The mod of each file (paths relative to Data). Files no manager knows are left out.</summary>
    public static Dictionary<string, Mod> Find(IEnumerable<string> dataRelative)
    {
        var want = dataRelative.Select(Normal).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
        if (want.Count == 0) return found;
        if (SkyLinkSettings.Mo2Instance().Folder is string mo2) FromMo2(mo2, want, found);
        else if (SkyLinkSettings.DataPath() is string data) FromVortex(Path.Combine(data, "vortex.deployment.json"), want, found);
        return found;
    }

    public static string Normal(string path) => path.Replace('/', '\\').TrimStart('\\');

    private static void FromVortex(string manifest, HashSet<string> want, Dictionary<string, Mod> found)
    {
        if (!File.Exists(manifest)) return;
        using var reader = new StreamReader(new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20));
        const string key = "\"relPath\": ";
        while (reader.ReadLine() is string line && found.Count < want.Count)
        {
            var at = line.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) continue;
            var value = line.AsSpan(at + key.Length).TrimEnd(", \r");
            string? rel;
            try { rel = JsonSerializer.Deserialize<string>(value); } catch (JsonException) { continue; }
            if (rel == null || !want.Contains(rel)) continue;
            var next = reader.ReadLine()?.Trim().TrimEnd(',');
            if (next == null || !next.StartsWith("\"source\": ")) continue;
            try { if (JsonSerializer.Deserialize<string>(next["\"source\": ".Length..]) is string folder) found[rel] = FromFolderName(folder); } catch (JsonException) { }
        }
    }

    // "KS Hairdos SMP-31300-1-7-1579138592": name, Nexus mod ID, version, upload time (ten digits). Hand-installed mods have no such ending.
    public static Mod FromFolderName(string folder)
    {
        var m = VortexName().Match(folder);
        return m.Success ? new Mod(m.Groups["name"].Value.Trim(), folder, int.Parse(m.Groups["id"].Value)) : new Mod(folder, folder, null);
    }

    private static void FromMo2(string mo2, HashSet<string> want, Dictionary<string, Mod> found)
    {
        var mods = Path.Combine(mo2, "mods");
        var folders = SkyLinkSettings.GameFileFolders();
        folders.Reverse();   // the winning folder first
        foreach (var folder in folders.Where(f => f.StartsWith(mods + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var rel in want.Where(w => !found.ContainsKey(w)).ToList())
            {
                if (!File.Exists(Path.Combine(folder, rel))) continue;
                var name = Path.GetFileName(folder);
                var id = GameEnvironment.ReadValue(Path.Combine(folder, "meta.ini"), "General", "modid");
                found[rel] = new Mod(name, name, int.TryParse(id, out var n) && n > 0 ? n : null);
            }
        }
    }

    [GeneratedRegex(@"^(?<name>.+?)-(?<id>\d{1,7})(-[0-9A-Za-z]+)*-\d{10}(\.\d+)?$")]
    private static partial Regex VortexName();
}
