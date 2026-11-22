using System.Text;

namespace SkyrimMCP.Face;

/// <summary>
/// Changes texture paths in a Skyrim SE mesh (NIF 20.2.0.7, user 12, BS 100): only BSShaderTextureSet blocks are rewritten, and the
/// header's block sizes with them, so paths may change length. For RaceMenu's exported heads: they name the face's skin texture by a
/// temporary path under Data\SKSE\Plugins\CharGen, which must become the slot's own FaceTint path (2026-11-22).
/// </summary>
public static class NifTextures
{
    /// <summary>Calls replace for every texture path; a non-null result takes its place. Returns the new file and how many changed.</summary>
    public static (byte[] Bytes, int Changed) Replace(byte[] b, Func<string, string?> replace)
    {
        var o = Array.IndexOf(b, (byte)'\n') + 1;
        if (o <= 0 || !Encoding.ASCII.GetString(b, 0, Math.Min(o, 40)).StartsWith("Gamebryo File Format")) throw new InvalidDataException("not a NIF");
        o += 4 + 1 + 4;   // version, endian, user version
        var blocks = BitConverter.ToInt32(b, o); o += 4;
        var bsVersion = BitConverter.ToUInt32(b, o); o += 4;
        if (bsVersion != 100) throw new InvalidDataException($"not a Skyrim SE mesh (BS version {bsVersion})");
        for (var k = 0; k < 3; k++) o += 1 + b[o];   // author, process and export script
        var typeCount = BitConverter.ToUInt16(b, o); o += 2;
        var types = new string[typeCount];
        for (var i = 0; i < typeCount; i++) { var n = BitConverter.ToInt32(b, o); o += 4; types[i] = Encoding.ASCII.GetString(b, o, n); o += n; }
        var blockType = new ushort[blocks];
        for (var i = 0; i < blocks; i++) { blockType[i] = BitConverter.ToUInt16(b, o); o += 2; }
        var sizesAt = o;
        var sizes = new uint[blocks];
        for (var i = 0; i < blocks; i++) { sizes[i] = BitConverter.ToUInt32(b, o); o += 4; }
        var strings = BitConverter.ToInt32(b, o); o += 8;
        for (var i = 0; i < strings; i++) o += 4 + BitConverter.ToInt32(b, o);
        var groups = BitConverter.ToInt32(b, o); o += 4 + 4 * groups;

        using var outStream = new MemoryStream();
        outStream.Write(b, 0, o);
        var newSizes = (uint[])sizes.Clone();
        var at = o;
        var changed = 0;
        for (var i = 0; i < blocks; i++)
        {
            var block = b.AsSpan(at, (int)sizes[i]).ToArray();
            at += (int)sizes[i];
            if (types[blockType[i]] == "BSShaderTextureSet")
            {
                var n = BitConverter.ToInt32(block, 0);
                using var rebuilt = new MemoryStream();
                rebuilt.Write(BitConverter.GetBytes(n));
                var q = 4;
                for (var t = 0; t < n; t++)
                {
                    var length = BitConverter.ToInt32(block, q);
                    var path = Encoding.Latin1.GetString(block, q + 4, length);
                    q += 4 + length;
                    if (replace(path) is string other) { path = other; changed++; }
                    var bytes = Encoding.Latin1.GetBytes(path);
                    rebuilt.Write(BitConverter.GetBytes(bytes.Length));
                    rebuilt.Write(bytes);
                }
                rebuilt.Write(block, q, block.Length - q);
                block = rebuilt.ToArray();
            }
            newSizes[i] = (uint)block.Length;
            outStream.Write(block);
        }
        outStream.Write(b, at, b.Length - at);
        var result = outStream.ToArray();
        for (var i = 0; i < blocks; i++) BitConverter.GetBytes(newSizes[i]).CopyTo(result, sizesAt + 4 * i);
        return (result, changed);
    }

    /// <summary>One shape: its name, shader type (4 is the face, 5 skin tint, 6 hair tint) and texture set block.</summary>
    private sealed record Shape(string Name, uint ShaderType, int TextureSet);

    private static (List<Shape> Shapes, Dictionary<int, List<string>> Sets) Shapes(byte[] b)
    {
        var o = Array.IndexOf(b, (byte)'\n') + 1;
        if (o <= 0 || !Encoding.ASCII.GetString(b, 0, Math.Min(o, 40)).StartsWith("Gamebryo File Format")) throw new InvalidDataException("not a NIF");
        o += 9;
        var blocks = BitConverter.ToInt32(b, o); o += 4;
        if (BitConverter.ToUInt32(b, o) != 100) throw new InvalidDataException("not a Skyrim SE mesh");
        o += 4;
        for (var k = 0; k < 3; k++) o += 1 + b[o];
        var typeCount = BitConverter.ToUInt16(b, o); o += 2;
        var types = new string[typeCount];
        for (var i = 0; i < typeCount; i++) { var n = BitConverter.ToInt32(b, o); o += 4; types[i] = Encoding.ASCII.GetString(b, o, n); o += n; }
        var type = new string[blocks];
        for (var i = 0; i < blocks; i++) { type[i] = types[BitConverter.ToUInt16(b, o)]; o += 2; }
        var starts = new int[blocks];
        var sizes = new int[blocks];
        for (var i = 0; i < blocks; i++) { sizes[i] = (int)BitConverter.ToUInt32(b, o); o += 4; }
        var stringCount = BitConverter.ToInt32(b, o); o += 8;
        var strings = new string[stringCount];
        for (var i = 0; i < stringCount; i++) { var n = BitConverter.ToInt32(b, o); o += 4; strings[i] = Encoding.Latin1.GetString(b, o, n); o += n; }
        var groups = BitConverter.ToInt32(b, o); o += 4 + 4 * groups;
        for (var i = 0; i < blocks; i++) { starts[i] = o; o += sizes[i]; }

        var sets = new Dictionary<int, List<string>>();
        for (var i = 0; i < blocks; i++)
        {
            if (type[i] != "BSShaderTextureSet") continue;
            var list = new List<string>();
            var q = starts[i] + 4;
            for (var t = BitConverter.ToInt32(b, starts[i]); t > 0; t--) { var n = BitConverter.ToInt32(b, q); list.Add(Encoding.Latin1.GetString(b, q + 4, n)); q += 4 + n; }
            sets[i] = list;
        }
        var shapes = new List<Shape>();
        for (var i = 0; i < blocks; i++)
        {
            if (type[i] is not ("BSDynamicTriShape" or "BSTriShape")) continue;
            var q = starts[i];
            var nameIndex = BitConverter.ToInt32(b, q);
            var extras = BitConverter.ToInt32(b, q + 4);
            q += 8 + 4 * extras + 4;             // extra data, controller
            q += 4 + 12 + 36 + 4 + 4;            // flags, translation, rotation, scale, collision
            q += 16 + 4;                         // bounding sphere, skin
            var shader = BitConverter.ToInt32(b, q);
            if (shader < 0 || shader >= blocks || type[shader] != "BSLightingShaderProperty") continue;
            var s = starts[shader];
            var set = BitConverter.ToInt32(b, s + 40 + 4 * BitConverter.ToInt32(b, s + 8));
            if (sets.ContainsKey(set)) shapes.Add(new Shape(nameIndex >= 0 && nameIndex < stringCount ? strings[nameIndex] : "", BitConverter.ToUInt32(b, s), set));
        }
        return (shapes, sets);
    }

    /// <summary>
    /// BUG-102: RaceMenu's ExportHead gave some other shapes (a hair, a scar) the face's texture set. Those take the textures of their
    /// own head part's mesh; sourceMesh gives it by the shape's name (the head part's editor ID). Returns what was repaired and what
    /// couldn't be.
    /// </summary>
    public static (byte[] Bytes, List<string> Repaired, List<string> Unrepaired) RepairExportedHead(byte[] mesh, Func<string, byte[]?> sourceMesh)
    {
        var (shapes, sets) = Shapes(mesh);
        List<string> repaired = [], unrepaired = [];
        var face = shapes.FirstOrDefault(s => s.ShaderType == 4);
        if (face == null) return (mesh, repaired, unrepaired);
        var replacement = new Dictionary<int, List<string>>();
        foreach (var shape in shapes.Where(s => s.ShaderType != 4 && sets[s.TextureSet].SequenceEqual(sets[face.TextureSet])))
        {
            List<string>? own = null;
            try
            {
                if (sourceMesh(shape.Name) is byte[] source)
                {
                    var (theirs, theirSets) = Shapes(source);
                    var from = theirs.FirstOrDefault(t => t.ShaderType == shape.ShaderType) ?? theirs.FirstOrDefault();
                    if (from != null) own = theirSets[from.TextureSet];
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException) { }
            if (own == null || shape.TextureSet == face.TextureSet) { unrepaired.Add(shape.Name); continue; }
            replacement[shape.TextureSet] = own;
            repaired.Add(shape.Name);
        }
        if (replacement.Count == 0) return (mesh, repaired, unrepaired);
        return (ReplaceSets(mesh, replacement), repaired, unrepaired);
    }

    private static byte[] ReplaceSets(byte[] mesh, Dictionary<int, List<string>> replacement)
    {
        // Replace visits texture sets in block order and each set's paths in turn, so number them the same way.
        var (_, sets) = Shapes(mesh);
        var order = sets.Keys.OrderBy(k => k).SelectMany(k => Enumerable.Range(0, sets[k].Count).Select(t => (set: k, slot: t))).ToList();
        var at = 0;
        return Replace(mesh, _ =>
        {
            var (set, slot) = order[at++];
            return replacement.TryGetValue(set, out var own) ? (slot < own.Count ? own[slot] : "") : null;
        }).Bytes;
    }
}
