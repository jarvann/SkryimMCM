using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SkyrimMCP.Saves;

// FEAT-269: the installed scripts' variable lists, to compare with the ones a save recorded (a mod updated mid-playthrough
// that renamed, removed or retyped script variables is the usual reason an old save misbehaves after an update). Reads loose
// Data\Scripts\*.pex and the scripts folder of every BSA in Data (SE v105, LZ4; LE v104, zlib). Read-only.
public static class ScriptFiles
{
    public sealed record Variable(string Name, string Type);

    /// <summary>Where each script's .pex comes from: loose files win; among archives, the one loaded later wins.</summary>
    public sealed class Index
    {
        public Dictionary<string, string> Loose { get; } = new(StringComparer.OrdinalIgnoreCase);              // script -> path
        public Dictionary<string, (string bsa, long offset, int size, bool compressed, bool embed, int version)> Archived { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Archives { get; set; }
        public List<string> Errors { get; } = [];
    }

    /// <summary>Indexes Data's scripts. archiveOrder: BSA base names in load order (later wins); others count as earliest.</summary>
    public static Index Build(string dataFolder, IReadOnlyList<string>? archiveOrder = null)
    {
        var index = new Index();
        var scripts = Path.Combine(dataFolder, "Scripts");
        if (Directory.Exists(scripts))
            foreach (var f in Directory.EnumerateFiles(scripts, "*.pex"))
                index.Loose[Path.GetFileNameWithoutExtension(f)] = f;

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (archiveOrder != null) for (var i = 0; i < archiveOrder.Count; i++) rank.TryAdd(archiveOrder[i], i + 1);
        var bsas = Directory.Exists(dataFolder) ? Directory.EnumerateFiles(dataFolder, "*.bsa").ToList() : [];
        foreach (var bsa in bsas.OrderBy(b => rank.GetValueOrDefault(BsaOwner(b))))
        {
            try { IndexBsa(bsa, index); index.Archives++; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or EssScanner.FormatException)
            { index.Errors.Add($"{Path.GetFileName(bsa)}: {ex.Message}"); }
        }
        return index;
    }

    // "Mod - Textures.bsa" and "Mod.bsa" both belong to Mod.esp.
    static string BsaOwner(string bsa)
    {
        var n = Path.GetFileNameWithoutExtension(bsa);
        var dash = n.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? n[..dash] : n;
    }

    static void IndexBsa(string path, Index index)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = new byte[36];
        fs.ReadExactly(head);
        if (head[0] != 'B' || head[1] != 'S' || head[2] != 'A' || head[3] != 0) throw new InvalidDataException("not a BSA");
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(at));
        var version = I(4);
        if (version is not (104 or 105)) throw new InvalidDataException($"BSA version {version}");
        var folderOffset = I(8);
        var flags = I(12);
        var folderCount = I(16);
        var fileCount = I(20);
        var totalFolderNames = I(24);
        var totalFileNames = I(28);
        var dirNames = (flags & 1) != 0;
        var fileNames = (flags & 2) != 0;
        var compressedDefault = (flags & 4) != 0;
        var embed = (flags & 0x100) != 0;
        if (!dirNames || !fileNames) return;

        var folderRec = version == 105 ? 24 : 16;
        var recordsLength = folderCount * folderRec + totalFolderNames + folderCount + fileCount * 16;
        var block = new byte[recordsLength + totalFileNames];
        fs.Seek(folderOffset, SeekOrigin.Begin);
        fs.ReadExactly(block);
        var counts = new int[folderCount];
        for (var f = 0; f < folderCount; f++) counts[f] = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(f * folderRec + 8));

        // Folder name + file records per folder, then every file name (null-terminated) in the same order.
        var p = folderCount * folderRec;
        var files = new List<(string folder, int size, int offset)>(fileCount);
        for (var f = 0; f < folderCount; f++)
        {
            int len = block[p];
            var folder = Encoding.ASCII.GetString(block, p + 1, Math.Max(0, len - 1));
            p += 1 + len;
            for (var k = 0; k < counts[f]; k++)
            {
                files.Add((folder, BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(p + 8)), BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(p + 12))));
                p += 16;
            }
        }
        var names = recordsLength;
        foreach (var file in files)
        {
            var end = Array.IndexOf(block, (byte)0, names);
            if (end < 0) break;
            var name = Encoding.ASCII.GetString(block, names, end - names);
            names = end + 1;
            if (!file.folder.Equals("scripts", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".pex", StringComparison.OrdinalIgnoreCase)) continue;
            const int toggle = 1 << 30;
            var compressed = compressedDefault ^ ((file.size & toggle) != 0);
            index.Archived[Path.GetFileNameWithoutExtension(name)] = (path, file.offset, file.size & ~toggle, compressed, embed, version);
        }
    }

    /// <summary>The variables of a script's own object (not its parent's), or null when no file is found.</summary>
    public static List<Variable>? ReadVariables(Index index, string script)
    {
        byte[]? data = null;
        if (index.Loose.TryGetValue(script, out var loose)) data = File.ReadAllBytes(loose);
        else if (index.Archived.TryGetValue(script, out var a))
        {
            using var fs = new FileStream(a.bsa, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(a.offset, SeekOrigin.Begin);
            var raw = new byte[a.size];
            fs.ReadExactly(raw);
            var at = 0;
            if (a.embed) at += 1 + raw[0];
            if (!a.compressed) data = raw[at..];
            else
            {
                var original = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at));
                at += 4;
                data = a.version == 104 ? Inflate(raw, at, original) : Lz4Any(raw, at, original);
            }
        }
        return data == null ? null : Pex(data);
    }

    static byte[] Inflate(byte[] src, int at, int expected)
    {
        using var input = new MemoryStream(src, at, src.Length - at);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        var output = new byte[expected];
        z.ReadExactly(output);
        return output;
    }

    // SE archives hold LZ4 frames; a bare LZ4 block is accepted too.
    static byte[] Lz4Any(byte[] src, int at, int expected)
    {
        var dst = new byte[expected];
        if (BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(at)) == 0x184D2204)
        {
            var flg = src[at + 4];
            var p = at + 6 + ((flg & 0x08) != 0 ? 8 : 0) + ((flg & 0x01) != 0 ? 4 : 0) + 1;   // magic, FLG, BD, [size], [dictID], HC
            var d = 0;
            var blockChecksum = (flg & 0x10) != 0;
            while (p + 4 <= src.Length)
            {
                var size = BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(p));
                p += 4;
                if (size == 0) break;
                var raw = (size & unchecked((int)0x80000000)) != 0;
                size &= 0x7FFFFFFF;
                if (raw) { Buffer.BlockCopy(src, p, dst, d, Math.Min(size, expected - d)); d += size; }
                else d = Lz4Block(src, p, size, dst, d);
                p += size + (blockChecksum ? 4 : 0);
            }
            return dst;
        }
        Lz4Block(src, at, src.Length - at, dst, 0);
        return dst;
    }

    // Decodes one LZ4 block into dst from position d (matches may reach back into earlier blocks); returns the new position.
    static int Lz4Block(byte[] src, int s, int length, byte[] dst, int d)
    {
        var end = s + length;
        while (s < end)
        {
            var token = src[s++];
            var lit = token >> 4;
            if (lit == 15) { byte x; do { x = src[s++]; lit += x; } while (x == 255); }
            if (d + lit > dst.Length) throw new InvalidDataException("LZ4 literals past the end");
            Buffer.BlockCopy(src, s, dst, d, lit);
            s += lit; d += lit;
            if (s >= end) break;
            var offset = src[s] | (src[s + 1] << 8);
            s += 2;
            var match = (token & 15) + 4;
            if ((token & 15) == 15) { byte x; do { x = src[s++]; match += x; } while (x == 255); }
            if (offset == 0 || offset > d || d + match > dst.Length) throw new InvalidDataException("LZ4 bad match");
            for (var i = 0; i < match; i++, d++) dst[d] = dst[d - offset];
        }
        return d;
    }

    // Skyrim .pex (big-endian): header, string table, debug info, user flags, then the first object's variables.
    static List<Variable>? Pex(byte[] b)
    {
        var p = 0;
        ushort U16() { var v = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); p += 2; return v; }
        uint U32() { var v = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4; return v; }
        string WStr() { var n = U16(); var s = Encoding.UTF8.GetString(b, p, n); p += n; return s; }
        try
        {
            if (U32() != 0xFA57C0DE) return null;
            p += 2 + 2 + 8;                   // version, game, compile time
            WStr(); WStr(); WStr();           // source, user, machine
            var strings = new string[U16()];
            for (var i = 0; i < strings.Length; i++) strings[i] = WStr();
            if (b[p++] != 0)
            {
                p += 8;
                int functions = U16();
                for (var i = 0; i < functions; i++) { p += 2 + 2 + 2 + 1; int n = U16(); p += 2 * n; }
            }
            int userFlags = U16();
            p += userFlags * 3;
            int objects = U16();
            if (objects == 0) return [];
            p += 2 + 4 + 2 + 2 + 4 + 2;       // name, size, parent, doc string, user flags, auto state
            int vars = U16();
            var list = new List<Variable>(vars);
            for (var i = 0; i < vars; i++)
            {
                var name = strings[U16()];
                var type = strings[U16()];
                p += 4;
                switch (b[p++])
                {
                    case 1: case 2: p += 2; break;
                    case 3: case 4: p += 4; break;
                    case 5: p += 1; break;
                }
                list.Add(new Variable(name, type));
            }
            return list;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException) { return null; }
    }
}
