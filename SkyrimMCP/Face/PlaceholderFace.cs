using System.Text;

namespace SkyrimMCP.Face;

/// <summary>
/// FEAT-305: a slot's stand-in face files, there only so the game lists them when it starts. The game lists loose files as it
/// starts: a face file added later isn't seen until the next start, but one it already knows can be replaced while it runs. So
/// SkyLink keeps a few free slots of each race and sex with these, and a new character's own face replaces them and shows at once.
/// The mesh is a valid Skyrim SE NIF with no shapes (a root BSFadeNode whose one child is the BSFaceGenNiNodeSkinned node), so a
/// slot whose face was never written shows a head built from parts rather than crashing the game on a missing file (BUG-108).
/// </summary>
public static class PlaceholderFace
{
    public static byte[] Mesh()
    {
        using var s = new MemoryStream();
        using var w = new BinaryWriter(s);
        void Text(string t) { var b = Encoding.ASCII.GetBytes(t); w.Write(b.Length); w.Write(b); }
        string[] types = ["BSFadeNode", "NiNode"];
        string[] strings = ["SkyLinkPlaceholder", "BSFaceGenNiNodeSkinned"];
        byte[][] blocks = [Node(0, [1]), Node(1, [])];

        w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
        w.Write(0x14020007u);           // version
        w.Write((byte)1);               // little endian
        w.Write(12u);                   // user version
        w.Write(blocks.Length);
        w.Write(100u);                  // Bethesda version: Skyrim SE
        for (var k = 0; k < 3; k++) { w.Write((byte)1); w.Write((byte)0); }   // export info: three empty strings
        w.Write((ushort)types.Length);
        foreach (var t in types) Text(t);
        w.Write((ushort)0);             // block 0: BSFadeNode
        w.Write((ushort)1);             // block 1: NiNode
        foreach (var b in blocks) w.Write(b.Length);
        w.Write(strings.Length);
        w.Write(strings.Max(x => x.Length));
        foreach (var t in strings) Text(t);
        w.Write(0);                     // no groups
        foreach (var b in blocks) w.Write(b);
        w.Write(1);                     // one root
        w.Write(0);                     // block 0
        return s.ToArray();
    }

    /// <summary>A NiNode body (Skyrim SE): name, no extra data or controller, the usual flags, identity transform, children, no effects.</summary>
    private static byte[] Node(int name, int[] children)
    {
        using var s = new MemoryStream();
        using var w = new BinaryWriter(s);
        w.Write(name);
        w.Write(0);                     // extra data
        w.Write(-1);                    // controller
        w.Write(0x0Eu);                 // flags, as in the game's own face files
        w.Write(0f); w.Write(0f); w.Write(0f);
        foreach (var r in new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f }) w.Write(r);
        w.Write(1f);                    // scale
        w.Write(-1);                    // collision
        w.Write(children.Length);
        foreach (var c in children) w.Write(c);
        w.Write(0);                     // effects
        return s.ToArray();
    }

    /// <summary>A 4x4 uncompressed BGRA texture in a plain skin tone.</summary>
    public static byte[] Tint()
    {
        using var s = new MemoryStream();
        using var w = new BinaryWriter(s);
        w.Write(Encoding.ASCII.GetBytes("DDS "));
        w.Write(124u);                  // header size
        w.Write(0x1u | 0x2u | 0x4u | 0x1000u | 0x8u);  // caps, height, width, pixel format, pitch
        w.Write(4u); w.Write(4u);       // height, width
        w.Write(16u);                   // pitch
        w.Write(0u); w.Write(1u);       // depth, mip levels
        for (var k = 0; k < 11; k++) w.Write(0u);
        w.Write(32u);                   // pixel format size
        w.Write(0x41u);                 // RGB with alpha
        w.Write(0u);                    // no FourCC
        w.Write(32u);                   // bits per pixel
        w.Write(0x00FF0000u); w.Write(0x0000FF00u); w.Write(0x000000FFu); w.Write(0xFF000000u);
        w.Write(0x1000u);               // caps: texture
        for (var k = 0; k < 4; k++) w.Write(0u);
        for (var p = 0; p < 16; p++) { w.Write((byte)0x80); w.Write((byte)0x98); w.Write((byte)0xB8); w.Write((byte)0xFF); }  // B, G, R, A
        return s.ToArray();
    }
}
