using System.Text;

namespace SkyrimMCP.Face;

/// <summary>
/// Reads the vertex positions of a Skyrim SE mesh (NIF 20.2.0.7, user version 12, BS version 100) without the game
/// (FEAT-236): the header's block sizes let it jump straight to the shapes. Positions come from BSDynamicTriShape's
/// dynamic data (head parts), else BSTriShape's vertex data, else NiSkinPartition's vertex data (skinned SSE meshes
/// keep it there), each shape moved by its own transform.
/// </summary>
public static class NifShape
{
    public static List<(float X, float Y, float Z)> Positions(string path) => Positions(File.ReadAllBytes(path));

    public static List<(float X, float Y, float Z)> Positions(byte[] b)
    {
        var o = Array.IndexOf(b, (byte)'\n') + 1;
        if (o <= 0 || !Encoding.ASCII.GetString(b, 0, Math.Min(o, 40)).StartsWith("Gamebryo File Format")) throw new InvalidDataException("not a NIF");
        o += 4 + 1;  // version, endian
        o += 4;      // user version
        var blocks = BitConverter.ToInt32(b, o); o += 4;
        var bsVersion = BitConverter.ToUInt32(b, o); o += 4;
        o += 1 + b[o];  // author
        if (bsVersion > 130) o += 4;
        o += 1 + b[o];  // process script
        o += 1 + b[o];  // export script
        var typeCount = BitConverter.ToUInt16(b, o); o += 2;
        var types = new string[typeCount];
        for (var i = 0; i < typeCount; i++)
        {
            var n = BitConverter.ToInt32(b, o); o += 4;
            types[i] = Encoding.ASCII.GetString(b, o, n); o += n;
        }
        var blockType = new ushort[blocks];
        for (var i = 0; i < blocks; i++) { blockType[i] = BitConverter.ToUInt16(b, o); o += 2; }
        var blockSize = new uint[blocks];
        for (var i = 0; i < blocks; i++) { blockSize[i] = BitConverter.ToUInt32(b, o); o += 4; }
        var strings = BitConverter.ToInt32(b, o); o += 8;  // count, max length
        for (var i = 0; i < strings; i++) o += 4 + BitConverter.ToInt32(b, o);
        var groups = BitConverter.ToInt32(b, o); o += 4 + 4 * groups;

        var dynamic = new List<(float, float, float)>();
        var stat = new List<(float, float, float)>();
        var partition = new List<(float, float, float)>();
        var start = o;
        for (var i = 0; i < blocks; i++)
        {
            var type = types[blockType[i]];
            try
            {
                if (type is "BSDynamicTriShape" or "BSTriShape") ReadTriShape(b, start, type == "BSDynamicTriShape", bsVersion, dynamic, stat);
                else if (type == "NiSkinPartition" && bsVersion == 100) ReadPartition(b, start, partition, bsVersion);
            }
            catch (Exception) { /* an odd block: skip it */ }
            start += (int)blockSize[i];
        }
        return dynamic.Count > 0 ? dynamic : stat.Count > 0 ? stat : partition;
    }

    private static void ReadTriShape(byte[] b, int p, bool isDynamic, uint bsVersion, List<(float, float, float)> dynamic, List<(float, float, float)> stat)
    {
        p += 4;  // name
        var extra = BitConverter.ToInt32(b, p); p += 4 + 4 * extra;
        p += 4;  // controller
        p += 4;  // flags
        float tx = BitConverter.ToSingle(b, p), ty = BitConverter.ToSingle(b, p + 4), tz = BitConverter.ToSingle(b, p + 8);
        var r = new float[9];
        for (var k = 0; k < 9; k++) r[k] = BitConverter.ToSingle(b, p + 12 + 4 * k);
        var scale = BitConverter.ToSingle(b, p + 48);
        p += 52;
        p += 4;   // collision
        p += 16;  // bounding sphere
        p += 12;  // skin, shader, alpha
        var desc = BitConverter.ToUInt64(b, p); p += 8;
        var triangles = BitConverter.ToUInt16(b, p); p += 2;
        var vertices = BitConverter.ToUInt16(b, p); p += 2;
        var dataSize = BitConverter.ToUInt32(b, p); p += 4;
        var vertexSize = (int)(desc & 0xF) * 4;
        // Skyrim SE (BS version 100) always stores full-precision positions, whatever the flag says (checked on a wig
        // whose flags lack VF_FULLPREC); only Fallout 4 uses half floats.
        var fullPrecision = bsVersion == 100 || ((desc >> 44) & (1UL << 10)) != 0;

        (float, float, float) Move(float x, float y, float z)
        {
            x *= scale; y *= scale; z *= scale;
            return (r[0] * x + r[1] * y + r[2] * z + tx, r[3] * x + r[4] * y + r[5] * z + ty, r[6] * x + r[7] * y + r[8] * z + tz);
        }

        var local = new List<(float, float, float)>();
        if (dataSize > 0 && vertexSize > 0)
        {
            for (var v = 0; v < vertices; v++)
            {
                var q = p + v * vertexSize;
                local.Add(fullPrecision
                    ? Move(BitConverter.ToSingle(b, q), BitConverter.ToSingle(b, q + 4), BitConverter.ToSingle(b, q + 8))
                    : Move((float)BitConverter.ToHalf(b, q), (float)BitConverter.ToHalf(b, q + 2), (float)BitConverter.ToHalf(b, q + 4)));
            }
            p += (int)dataSize;
            p += triangles * 6;
        }
        if (bsVersion == 100)
        {
            var particles = BitConverter.ToUInt32(b, p); p += 4;
            if (particles > 0) p += (int)particles;  // not used by hair
        }
        if (isDynamic)
        {
            var size = BitConverter.ToInt32(b, p); p += 4;
            var n = size / 16;
            if (n > 0)
            {
                for (var v = 0; v < n; v++)
                {
                    var q = p + v * 16;
                    dynamic.Add(Move(BitConverter.ToSingle(b, q), BitConverter.ToSingle(b, q + 4), BitConverter.ToSingle(b, q + 8)));
                }
                return;
            }
        }
        stat.AddRange(local);
    }

    private static void ReadPartition(byte[] b, int p, List<(float, float, float)> into, uint bsVersion)
    {
        p += 4;  // partitions
        var dataSize = BitConverter.ToInt32(b, p); p += 4;
        var vertexSize = BitConverter.ToInt32(b, p); p += 4;
        var desc = BitConverter.ToUInt64(b, p); p += 8;
        if (dataSize <= 0 || vertexSize <= 0) return;
        var fullPrecision = bsVersion == 100 || ((desc >> 44) & (1UL << 10)) != 0;
        for (var v = 0; v < dataSize / vertexSize; v++)
        {
            var q = p + v * vertexSize;
            into.Add(fullPrecision
                ? (BitConverter.ToSingle(b, q), BitConverter.ToSingle(b, q + 4), BitConverter.ToSingle(b, q + 8))
                : ((float)BitConverter.ToHalf(b, q), (float)BitConverter.ToHalf(b, q + 2), (float)BitConverter.ToHalf(b, q + 4)));
        }
    }
}
