namespace SkyrimMCP.Face;

/// <summary>
/// RaceMenu's exported skin texture is 4096x4096 uncompressed without mipmaps (67 MB): too big to share. Makes a smaller one with
/// mipmaps. Only that layout (32-bit RGBA) is changed; anything else, such as a game texture (already small), is returned as it is.
/// </summary>
public static class TintTexture
{
    public static byte[] Shrink(byte[] dds, int size)
    {
        if (dds.Length < 128 || dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S') return dds;
        int height = BitConverter.ToInt32(dds, 12), width = BitConverter.ToInt32(dds, 16);
        var plain = (BitConverter.ToInt32(dds, 80) & 0x4) == 0 && BitConverter.ToInt32(dds, 88) == 32;
        if (!plain || width != height || width <= size || width % size != 0 || dds.Length < 128 + 4 * width * height) return dds;
        var redFirst = BitConverter.ToUInt32(dds, 92) == 0xFF;
        var step = width / size;
        var level = new byte[4 * size * size];   // blue, green, red, alpha
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            int c0 = 0, c1 = 0, c2 = 0;
            for (var v = 0; v < step; v++)
            for (var u = 0; u < step; u++)
            {
                var at = 128 + 4 * ((y * step + v) * width + x * step + u);
                c0 += dds[at]; c1 += dds[at + 1]; c2 += dds[at + 2];
            }
            var n = step * step;
            var o = 4 * (y * size + x);
            level[o] = (byte)((redFirst ? c2 : c0) / n);
            level[o + 1] = (byte)(c1 / n);
            level[o + 2] = (byte)((redFirst ? c0 : c2) / n);
            level[o + 3] = 255;
        }
        List<byte[]> levels = [level];
        for (var s = size; s > 1; s /= 2)
        {
            var from = levels[^1];
            var half = s / 2;
            var next = new byte[4 * half * half];
            for (var y = 0; y < half; y++)
            for (var x = 0; x < half; x++)
            for (var k = 0; k < 4; k++)
                next[4 * (y * half + x) + k] = (byte)((from[4 * (2 * y * s + 2 * x) + k] + from[4 * (2 * y * s + 2 * x + 1) + k] +
                                                       from[4 * ((2 * y + 1) * s + 2 * x) + k] + from[4 * ((2 * y + 1) * s + 2 * x + 1) + k] + 2) / 4);
            levels.Add(next);
        }
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("DDS "u8);
        w.Write(124);
        w.Write(0x1 | 0x2 | 0x4 | 0x8 | 0x1000 | 0x20000);
        w.Write(size); w.Write(size); w.Write(4 * size); w.Write(0); w.Write(levels.Count);
        w.Write(new byte[44]);
        w.Write(32); w.Write(0x41); w.Write(0); w.Write(32);
        w.Write(0x00FF0000u); w.Write(0x0000FF00u); w.Write(0x000000FFu); w.Write(0xFF000000u);
        w.Write(0x1000 | 0x400000 | 0x8);
        w.Write(new byte[16]);
        foreach (var l in levels) w.Write(l);
        return stream.ToArray();
    }
}
