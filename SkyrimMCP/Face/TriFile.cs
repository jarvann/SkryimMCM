namespace SkyrimMCP.Face;

/// <summary>
/// Reads the connected pieces of a head part's chargen TRI file (FRTRI003: a 64-byte header with the vertex and
/// triangle counts, then vertex positions and triangles), so the sculpt brush can tell an eye mesh's eyeball from its
/// lashes and tear duct (BUG-047). Loose files or BSA archives (MeshFiles).
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
            List<int[]>? pieces = null;
            try { pieces = MeshFiles.Read(host) is byte[] bytes ? Read(bytes) : null; } catch (Exception) { pieces = null; }
            Cache[host] = pieces;
            return pieces;
        }
    }

    private static List<int[]> Read(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data));
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
