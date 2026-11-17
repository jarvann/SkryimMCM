namespace SkyrimMCP.Face;

/// <summary>
/// Reads the connected pieces of a head part's chargen TRI file (FRTRI003: a 64-byte header with the vertex and
/// triangle counts, then vertex positions and triangles), so the sculpt brush can tell an eye mesh's eyeball from its
/// lashes and tear duct (BUG-047). Loose files only: MO2's overwrite and mods, then Data\meshes.
/// </summary>
public static class TriFile
{
    private static readonly Dictionary<string, List<int[]>?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mesh's connected pieces (vertex indices), largest first; null when the file isn't found or readable.</summary>
    public static List<int[]>? Pieces(string host)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(host, out var cached)) return cached;
            var path = Find(host);
            List<int[]>? pieces = null;
            try { pieces = path == null ? null : Read(path); } catch (Exception) { pieces = null; }
            Cache[host] = pieces;
            return pieces;
        }
    }

    private static string? Find(string host)
    {
        var relative = Path.Combine("meshes", host.Replace('/', '\\').TrimStart('\\'));
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

    private static List<int[]> Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        var magic = new string(reader.ReadChars(8));
        if (magic != "FRTRI003") throw new InvalidDataException("not a FRTRI003 file");
        var header = new uint[14];
        for (var i = 0; i < 14; i++) header[i] = reader.ReadUInt32();
        int vertices = (int)header[0], triangles = (int)header[1], added = (int)header[9];
        reader.BaseStream.Seek(64 + (long)(vertices + added) * 12, SeekOrigin.Begin);
        var parent = Enumerable.Range(0, vertices).ToArray();
        int Root(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        for (var t = 0; t < triangles; t++)
        {
            var a = (int)reader.ReadUInt32();
            var b = (int)reader.ReadUInt32();
            var c = (int)reader.ReadUInt32();
            if (a >= vertices || b >= vertices || c >= vertices) continue;
            parent[Root(a)] = Root(b);
            parent[Root(b)] = Root(c);
        }
        return Enumerable.Range(0, vertices).GroupBy(Root).Select(g => g.ToArray()).OrderByDescending(p => p.Length).ToList();
    }
}
