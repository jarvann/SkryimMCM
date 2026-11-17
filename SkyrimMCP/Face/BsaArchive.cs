using System.Text;

namespace SkyrimMCP.Face;

/// <summary>
/// Reads files out of Skyrim SE BSA archives (version 105, LZ4 frame compression), so mesh tools work on mods that pack
/// their meshes (FEAT-254). Only the name tables are read up front; a file is read when asked for.
/// </summary>
public sealed class BsaArchive
{
    private readonly string _path;
    private readonly bool _compressedByDefault;
    private readonly bool _embedNames;
    private readonly Dictionary<string, (long Offset, uint Size)> _files = new(StringComparer.OrdinalIgnoreCase);

    public string Path => _path;
    public IEnumerable<string> Names => _files.Keys;

    private BsaArchive(string path, bool compressed, bool embed) { _path = path; _compressedByDefault = compressed; _embedNames = embed; }

    public static BsaArchive? Open(string path)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "BSA\0") return null;
            var version = r.ReadUInt32();
            if (version != 105) return null;  // SE archives only
            r.ReadUInt32();  // header size (36)
            var flags = r.ReadUInt32();
            var folderCount = r.ReadUInt32();
            var fileCount = r.ReadUInt32();
            r.ReadUInt32();  // total folder name length
            r.ReadUInt32();  // total file name length
            r.ReadUInt32();  // file flags + padding
            if ((flags & 1) == 0 || (flags & 2) == 0) return null;  // needs directory and file names
            var bsa = new BsaArchive(path, (flags & 4) != 0, (flags & 0x100) != 0);

            var folderCounts = new uint[folderCount];
            for (var i = 0; i < folderCount; i++)
            {
                r.ReadUInt64();  // hash
                folderCounts[i] = r.ReadUInt32();
                r.ReadUInt32();  // padding
                r.ReadUInt64();  // offset
            }
            var records = new List<(string Folder, uint Size, uint Offset)>();
            for (var i = 0; i < folderCount; i++)
            {
                var nameLength = r.ReadByte();
                var folder = Encoding.UTF8.GetString(r.ReadBytes(nameLength)).TrimEnd('\0');
                for (var f = 0; f < folderCounts[i]; f++)
                {
                    r.ReadUInt64();  // hash
                    var size = r.ReadUInt32();
                    var offset = r.ReadUInt32();
                    records.Add((folder, size, offset));
                }
            }
            for (var i = 0; i < fileCount && i < records.Count; i++)
            {
                var name = new StringBuilder();
                byte c;
                while ((c = r.ReadByte()) != 0) name.Append((char)c);
                var (folder, size, offset) = records[i];
                bsa._files[$"{folder}\\{name}"] = (offset, size);
            }
            return bsa;
        }
        catch (Exception) { return null; }
    }

    public bool Contains(string path) => _files.ContainsKey(Normalise(path));

    public byte[]? Read(string path)
    {
        if (!_files.TryGetValue(Normalise(path), out var entry)) return null;
        using var r = new BinaryReader(File.OpenRead(_path));
        r.BaseStream.Seek(entry.Offset, SeekOrigin.Begin);
        var size = (int)(entry.Size & 0x3FFFFFFF);
        var compressed = _compressedByDefault ^ ((entry.Size & 0x40000000) != 0);
        if (_embedNames)
        {
            var n = r.ReadByte();
            r.ReadBytes(n);
            size -= n + 1;
        }
        if (!compressed) return r.ReadBytes(size);
        var original = r.ReadInt32();
        return Lz4Frame(r.ReadBytes(size - 4), original);
    }

    private static string Normalise(string path) => path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    // LZ4 frame format: magic, frame descriptor, then blocks (size, data) until a zero size. All blocks decode into
    // one buffer, so linked blocks can refer back into earlier ones.
    private static byte[] Lz4Frame(byte[] src, int originalSize)
    {
        var dst = new byte[originalSize];
        var s = 0;
        if (BitConverter.ToUInt32(src, 0) != 0x184D2204) throw new InvalidDataException("not an LZ4 frame");
        s += 4;
        var flg = src[s++];
        s++;  // BD
        if ((flg & 0x08) != 0) s += 8;  // content size
        if ((flg & 0x01) != 0) s += 4;  // dictionary id
        s++;  // header checksum
        var blockChecksum = (flg & 0x10) != 0;
        var d = 0;
        while (s + 4 <= src.Length)
        {
            var blockSize = BitConverter.ToUInt32(src, s); s += 4;
            if (blockSize == 0) break;
            var raw = (blockSize & 0x80000000) != 0;
            var length = (int)(blockSize & 0x7FFFFFFF);
            if (raw) { Buffer.BlockCopy(src, s, dst, d, length); d += length; }
            else d = Lz4Block(src, s, s + length, dst, d);
            s += length;
            if (blockChecksum) s += 4;
        }
        return dst;
    }

    private static int Lz4Block(byte[] src, int s, int end, byte[] dst, int d)
    {
        while (s < end)
        {
            var token = src[s++];
            var literal = token >> 4;
            if (literal == 15) { byte b; do { b = src[s++]; literal += b; } while (b == 255); }
            Buffer.BlockCopy(src, s, dst, d, literal);
            s += literal; d += literal;
            if (s >= end) break;
            var offset = src[s] | (src[s + 1] << 8); s += 2;
            var match = token & 15;
            if (match == 15) { byte b; do { b = src[s++]; match += b; } while (b == 255); }
            match += 4;
            var from = d - offset;
            for (var i = 0; i < match; i++) dst[d++] = dst[from + i];  // may overlap
        }
        return d;
    }
}
