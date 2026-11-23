using System.Text;

namespace SkyrimMCP.Face;

/// <summary>
/// A Skyrim SE mesh (NIF 20.2.0.7, user 12, BS 100) taken apart into its blocks and strings and put back together. Blocks stay raw
/// bytes, so whatever isn't understood passes through unchanged; a document that isn't edited is written back byte for byte.
/// Written from the block layout as seen in game files.
/// </summary>
public sealed class NifDocument
{
    private byte[] _head = [];          // version line, version, endian, user version
    private byte[] _exportInfo = [];
    private byte[] _groups = [];
    private byte[] _footer = [];
    private uint _bsVersion;

    public List<string> Types { get; } = [];
    public List<ushort> BlockTypes { get; } = [];
    public List<byte[]> Blocks { get; } = [];
    public List<string> Strings { get; } = [];

    public string TypeOf(int block) => block >= 0 && block < Blocks.Count ? Types[BlockTypes[block]] : "";

    public static NifDocument Read(byte[] b)
    {
        var o = Array.IndexOf(b, (byte)'\n') + 1;
        if (o <= 0 || !Encoding.ASCII.GetString(b, 0, Math.Min(o, 40)).StartsWith("Gamebryo File Format")) throw new InvalidDataException("not a NIF");
        var d = new NifDocument();
        o += 9;
        d._head = b[..o];
        var blocks = BitConverter.ToInt32(b, o); o += 4;
        d._bsVersion = BitConverter.ToUInt32(b, o); o += 4;
        if (d._bsVersion != 100) throw new InvalidDataException($"not a Skyrim SE mesh (BS version {d._bsVersion})");
        var info = o;
        for (var k = 0; k < 3; k++) o += 1 + b[o];
        d._exportInfo = b[info..o];
        var types = BitConverter.ToUInt16(b, o); o += 2;
        for (var i = 0; i < types; i++) d.Types.Add(Text(b, ref o));
        for (var i = 0; i < blocks; i++) { d.BlockTypes.Add(BitConverter.ToUInt16(b, o)); o += 2; }
        var sizes = new int[blocks];
        for (var i = 0; i < blocks; i++) { sizes[i] = BitConverter.ToInt32(b, o); o += 4; }
        var strings = BitConverter.ToInt32(b, o); o += 8;
        for (var i = 0; i < strings; i++) d.Strings.Add(Text(b, ref o));
        var groups = 4 + 4 * BitConverter.ToInt32(b, o);
        d._groups = b[o..(o + groups)]; o += groups;
        foreach (var size in sizes) { d.Blocks.Add(b[o..(o + size)]); o += size; }
        d._footer = b[o..];
        return d;
    }

    private static string Text(byte[] b, ref int o)
    {
        var n = BitConverter.ToInt32(b, o);
        var s = Encoding.Latin1.GetString(b, o + 4, n);
        o += 4 + n;
        return s;
    }

    public byte[] ToBytes()
    {
        using var s = new MemoryStream();
        using var w = new BinaryWriter(s);
        void Write(string text) { var t = Encoding.Latin1.GetBytes(text); w.Write(t.Length); w.Write(t); }
        w.Write(_head);
        w.Write(Blocks.Count);
        w.Write(_bsVersion);
        w.Write(_exportInfo);
        w.Write((ushort)Types.Count);
        foreach (var t in Types) Write(t);
        foreach (var t in BlockTypes) w.Write(t);
        foreach (var b in Blocks) w.Write(b.Length);
        w.Write(Strings.Count);
        w.Write(Strings.Count == 0 ? 0 : Strings.Max(x => Encoding.Latin1.GetByteCount(x)));
        foreach (var t in Strings) Write(t);
        w.Write(_groups);
        foreach (var b in Blocks) w.Write(b);
        w.Write(_footer);
        return s.ToArray();
    }

    public int Add(ushort type, byte[] bytes) { BlockTypes.Add(type); Blocks.Add(bytes); return Blocks.Count - 1; }

    public int AddString(string s) { Strings.Add(s); return Strings.Count - 1; }

    public int Int(int block, int offset) => BitConverter.ToInt32(Blocks[block], offset);
    public void SetInt(int block, int offset, int value) => BitConverter.GetBytes(value).CopyTo(Blocks[block], offset);

    /// <summary>The name of a block that has one (nodes, shapes).</summary>
    public string Name(int block) => Int(block, 0) is var i && i >= 0 && i < Strings.Count ? Strings[i] : "";

    // NiObjectNET: name, extra data list, controller. NiAVObject: flags, translation, rotation, scale, collision.
    private int FlagsAt(int block) => 8 + 4 * Int(block, 4) + 4;
    public int TranslationAt(int block) => FlagsAt(block) + 4;
    private int AfterAvObject(int block) => TranslationAt(block) + 12 + 36 + 4 + 4;

    /// <summary>The blocks in a node's or shape's extra data list.</summary>
    public IEnumerable<int> ExtraData(int block)
    {
        for (var k = 0; k < Int(block, 4); k++) if (Int(block, 8 + 4 * k) is >= 0 and var d) yield return d;
    }

    public void AddExtraData(int block, int data)
    {
        var count = Int(block, 4);
        var end = 8 + 4 * count;
        var b = Blocks[block];
        Blocks[block] = [.. b[..4], .. BitConverter.GetBytes(count + 1), .. b[8..end], .. BitConverter.GetBytes(data), .. b[end..]];
    }

    /// <summary>A block type's index, added to the file's type list when it isn't there yet.</summary>
    public ushort TypeIndex(string type)
    {
        var i = Types.IndexOf(type);
        if (i < 0) { Types.Add(type); i = Types.Count - 1; }
        return (ushort)i;
    }

    /// <summary>An NiStringExtraData's name and text (both string-table entries).</summary>
    public (string Name, string Text)? StringExtraData(int block) =>
        TypeOf(block) == "NiStringExtraData" && Int(block, 4) is var t && t >= 0 && t < Strings.Count ? (Name(block), Strings[t]) : null;

    public int AddStringExtraData(string name, string text) =>
        Add(TypeIndex("NiStringExtraData"), [.. BitConverter.GetBytes(AddString(name)), .. BitConverter.GetBytes(AddString(text))]);

    public bool IsNode(int block) => TypeOf(block) is "NiNode" or "BSFadeNode" or "BSFaceGenNiNode";
    public bool IsShape(int block) => TypeOf(block) is "BSDynamicTriShape" or "BSTriShape";

    public IEnumerable<int> Children(int node)
    {
        var at = AfterAvObject(node);
        for (var k = 0; k < Int(node, at); k++) if (Int(node, at + 4 + 4 * k) is >= 0 and var c) yield return c;
    }

    public void AddChild(int node, int child)
    {
        var at = AfterAvObject(node);
        var count = Int(node, at);
        var end = at + 4 + 4 * count;
        var b = Blocks[node];
        Blocks[node] = [.. b[..at], .. BitConverter.GetBytes(count + 1), .. b[(at + 4)..end], .. BitConverter.GetBytes(child), .. b[end..]];
    }

    public (float[] Translation, float[] Rotation) Transform(int block)
    {
        var at = TranslationAt(block);
        return (Enumerable.Range(0, 3).Select(i => BitConverter.ToSingle(Blocks[block], at + 4 * i)).ToArray(),
                Enumerable.Range(0, 9).Select(i => BitConverter.ToSingle(Blocks[block], at + 12 + 4 * i)).ToArray());
    }

    public void SetTransform(int block, float[] translation, float[] rotation)
    {
        var at = TranslationAt(block);
        for (var i = 0; i < 3; i++) BitConverter.GetBytes(translation[i]).CopyTo(Blocks[block], at + 4 * i);
        for (var i = 0; i < 9; i++) BitConverter.GetBytes(rotation[i]).CopyTo(Blocks[block], at + 12 + 4 * i);
    }

    // BSTriShape: a bounding sphere, then skin, shader and alpha.
    public int SkinAt(int shape) => AfterAvObject(shape) + 16;
    public int ShaderAt(int shape) => SkinAt(shape) + 4;
    public int AlphaAt(int shape) => SkinAt(shape) + 8;

    /// <summary>The shape's lighting shader block and its texture set block, or null.</summary>
    public (int Shader, int TextureSet, uint Type)? Lighting(int shape)
    {
        var shader = Int(shape, ShaderAt(shape));
        if (TypeOf(shader) != "BSLightingShaderProperty") return null;
        var set = Int(shader, 40 + 4 * Int(shader, 8));
        return TypeOf(set) == "BSShaderTextureSet" ? (shader, set, BitConverter.ToUInt32(Blocks[shader], 0)) : null;
    }

    public List<string> Textures(int set)
    {
        var b = Blocks[set];
        var list = new List<string>();
        var q = 4;
        for (var t = BitConverter.ToInt32(b, 0); t > 0; t--) list.Add(Text(b, ref q));
        return list;
    }

    public void SetTextures(int set, IReadOnlyList<string> textures)
    {
        using var s = new MemoryStream();
        using var w = new BinaryWriter(s);
        w.Write(textures.Count);
        foreach (var t in textures) { var bytes = Encoding.Latin1.GetBytes(t); w.Write(bytes.Length); w.Write(bytes); }
        Blocks[set] = s.ToArray();
    }
}
