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
}
