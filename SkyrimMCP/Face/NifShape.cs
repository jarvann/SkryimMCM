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

        var starts = new int[blocks];
        var at = o;
        for (var i = 0; i < blocks; i++) { starts[i] = at; at += (int)blockSize[i]; }
        string TypeOf(int block) => block >= 0 && block < blocks ? types[blockType[block]] : "";

        // Each node's parent, so a bone's place in the character can be put together from the node tree.
        var parent = new Dictionary<int, int>();
        for (var i = 0; i < blocks; i++)
        {
            if (TypeOf(i) is not ("NiNode" or "BSFadeNode")) continue;
            try
            {
                var q = SkipAvObject(b, starts[i], out _) + 4;  // collision
                var children = BitConverter.ToInt32(b, q); q += 4;
                for (var c = 0; c < children; c++) { var child = BitConverter.ToInt32(b, q + 4 * c); if (child >= 0) parent[child] = i; }
            }
            catch (Exception) { }
        }

        var dynamic = new List<(float, float, float)>();
        var stat = new List<(float, float, float)>();
        var partition = new List<(float, float, float)>();
        for (var i = 0; i < blocks; i++)
        {
            var type = TypeOf(i);
            if (type is not ("BSDynamicTriShape" or "BSTriShape")) continue;
            try
            {
                var shape = ReadTriShape(b, starts[i], type == "BSDynamicTriShape", bsVersion);
                var points = shape.Points;
                var into = shape.FromDynamic ? dynamic : stat;
                // Skinned SSE meshes may keep their vertices in the skin partition instead.
                if (points.Count == 0 && TypeOf(shape.Skin) is "NiSkinInstance" or "BSDismemberSkinInstance")
                {
                    var part = BitConverter.ToInt32(b, starts[shape.Skin] + 4);
                    if (TypeOf(part) == "NiSkinPartition" && bsVersion == 100) { points = ReadPartition(b, starts[part], bsVersion); into = partition; }
                }
                // A skinned shape is placed by its bones (the game ignores its own transform); anything else by its own transform.
                into.AddRange(ToCharacterSpace(b, starts, TypeOf, parent, shape.Skin, points) ?? points.Select(shape.Own.Apply).ToList());
            }
            catch (Exception) { /* an odd block: skip it */ }
        }
        return dynamic.Count > 0 ? dynamic : stat.Count > 0 ? stat : partition;
    }

    // FEAT-254 (round B, GOG): vanilla hair keeps its vertices relative to the head bone (that bone's skin transform is the
    // identity), while mod wigs keep them in character space; read as they are, vanilla hair sat about 120 units low, at the
    // character's feet. A skinned vertex lands at bone node (bone skin transform (v)), whatever the shape's own transform
    // says (the vanilla head carries the head bone's offset there too, and counting it twice put the head 120 units high).
    // Uses the bone most vertices follow, which for hair is the head. For character-space meshes the two transforms cancel
    // out. Null when the shape isn't skinned or its skin can't be read.
    private static List<(float, float, float)>? ToCharacterSpace(byte[] b, int[] starts, Func<int, string> typeOf,
        Dictionary<int, int> parent, int skin, List<(float, float, float)> points)
    {
        if (points.Count == 0 || typeOf(skin) is not ("NiSkinInstance" or "BSDismemberSkinInstance")) return null;
        var q = starts[skin];
        var data = BitConverter.ToInt32(b, q);
        var boneCount = BitConverter.ToInt32(b, q + 12);
        if (typeOf(data) != "NiSkinData" || boneCount <= 0) return null;
        var bones = new int[boneCount];
        for (var k = 0; k < boneCount; k++) bones[k] = BitConverter.ToInt32(b, q + 16 + 4 * k);

        var d = starts[data] + 52;  // overall skin transform (the identity in SSE meshes)
        var count = BitConverter.ToInt32(b, d);
        var weights = b[d + 4] != 0;
        d += 5;
        if (count != boneCount) return null;
        Xf? skinXf = null;
        var best = -1;
        var bestBone = -1;
        for (var k = 0; k < count; k++)
        {
            var vertices = BitConverter.ToUInt16(b, d + 68);
            if (vertices > best) { best = vertices; skinXf = ReadSkinTransform(b, d); bestBone = bones[k]; }
            d += 70 + (weights ? vertices * 6 : 0);
        }
        if (skinXf is not { } sx || typeOf(bestBone) is not ("NiNode" or "BSFadeNode")) return null;

        // The bone's place in the character: its node transform and those of its parents, up to the root.
        var chain = new List<Xf>();
        for (var n = bestBone; n >= 0 && chain.Count < 64; n = parent.TryGetValue(n, out var up) ? up : -1)
        {
            SkipAvObject(b, starts[n], out var xf);
            chain.Add(xf);
        }
        return points.Select(v =>
        {
            var w = sx.Apply(v);
            foreach (var xf in chain) w = xf.Apply(w);
            return w;
        }).ToList();
    }

    private readonly record struct Xf(float[] R, float Tx, float Ty, float Tz, float S)
    {
        public (float, float, float) Apply((float X, float Y, float Z) v)
        {
            var (x, y, z) = (v.X * S, v.Y * S, v.Z * S);
            return (R[0] * x + R[1] * y + R[2] * z + Tx, R[3] * x + R[4] * y + R[5] * z + Ty, R[6] * x + R[7] * y + R[8] * z + Tz);
        }
    }

    // NiTransform as NiSkinData stores it: rotation, translation, scale.
    private static Xf ReadSkinTransform(byte[] b, int p)
    {
        var r = new float[9];
        for (var k = 0; k < 9; k++) r[k] = BitConverter.ToSingle(b, p + 4 * k);
        return new Xf(r, BitConverter.ToSingle(b, p + 36), BitConverter.ToSingle(b, p + 40), BitConverter.ToSingle(b, p + 44), BitConverter.ToSingle(b, p + 48));
    }

    // NiAVObject (SSE): name, extra data, controller, flags, then translation, rotation, scale. Returns the offset after them.
    private static int SkipAvObject(byte[] b, int p, out Xf xf)
    {
        p += 4;  // name
        var extra = BitConverter.ToInt32(b, p); p += 4 + 4 * extra;
        p += 4;  // controller
        p += 4;  // flags
        var r = new float[9];
        for (var k = 0; k < 9; k++) r[k] = BitConverter.ToSingle(b, p + 12 + 4 * k);
        xf = new Xf(r, BitConverter.ToSingle(b, p), BitConverter.ToSingle(b, p + 4), BitConverter.ToSingle(b, p + 8), BitConverter.ToSingle(b, p + 48));
        return p + 52;
    }

    // Points in the shape's own space; Own is its transform.
    private sealed record Shape(List<(float, float, float)> Points, bool FromDynamic, int Skin, Xf Own);

    private static Shape ReadTriShape(byte[] b, int p, bool isDynamic, uint bsVersion)
    {
        p = SkipAvObject(b, p, out var own);
        p += 4;   // collision
        p += 16;  // bounding sphere
        var skin = BitConverter.ToInt32(b, p);
        p += 12;  // skin, shader, alpha
        var desc = BitConverter.ToUInt64(b, p); p += 8;
        var triangles = BitConverter.ToUInt16(b, p); p += 2;
        var vertices = BitConverter.ToUInt16(b, p); p += 2;
        var dataSize = BitConverter.ToUInt32(b, p); p += 4;
        var vertexSize = (int)(desc & 0xF) * 4;
        // Skyrim SE (BS version 100) always stores full-precision positions, whatever the flag says (checked on a wig
        // whose flags lack VF_FULLPREC); only Fallout 4 uses half floats.
        var fullPrecision = bsVersion == 100 || ((desc >> 44) & (1UL << 10)) != 0;

        var local = new List<(float, float, float)>();
        if (dataSize > 0 && vertexSize > 0)
        {
            for (var v = 0; v < vertices; v++)
            {
                var q = p + v * vertexSize;
                local.Add(fullPrecision
                    ? (BitConverter.ToSingle(b, q), BitConverter.ToSingle(b, q + 4), BitConverter.ToSingle(b, q + 8))
                    : ((float)BitConverter.ToHalf(b, q), (float)BitConverter.ToHalf(b, q + 2), (float)BitConverter.ToHalf(b, q + 4)));
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
                var dyn = new List<(float, float, float)>(n);
                for (var v = 0; v < n; v++)
                {
                    var q = p + v * 16;
                    dyn.Add((BitConverter.ToSingle(b, q), BitConverter.ToSingle(b, q + 4), BitConverter.ToSingle(b, q + 8)));
                }
                return new Shape(dyn, true, skin, own);
            }
        }
        return new Shape(local, false, skin, own);
    }

    private static List<(float, float, float)> ReadPartition(byte[] b, int p, uint bsVersion)
    {
        var into = new List<(float, float, float)>();
        p += 4;  // partitions
        var dataSize = BitConverter.ToInt32(b, p); p += 4;
        var vertexSize = BitConverter.ToInt32(b, p); p += 4;
        var desc = BitConverter.ToUInt64(b, p); p += 8;
        if (dataSize <= 0 || vertexSize <= 0) return into;
        var fullPrecision = bsVersion == 100 || ((desc >> 44) & (1UL << 10)) != 0;
        for (var v = 0; v < dataSize / vertexSize; v++)
        {
            var q = p + v * vertexSize;
            into.Add(fullPrecision
                ? (BitConverter.ToSingle(b, q), BitConverter.ToSingle(b, q + 4), BitConverter.ToSingle(b, q + 8))
                : ((float)BitConverter.ToHalf(b, q), (float)BitConverter.ToHalf(b, q + 2), (float)BitConverter.ToHalf(b, q + 4)));
        }
        return into;
    }
}
