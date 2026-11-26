using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SkyrimMCP.Plugins;

/// <summary>
/// A race's makeup layer files (TINI index -> TINT path) for one sex, as the plugin that last edits the race has them on disk
/// (BUG-147: in game the Nord race's female set read the male files, while Skyrim.esm and Update.esm have the female ones).
/// </summary>
public static class RaceTints
{
    /// <summary>
    /// The layers by TINI index, or null with a reason. raceFormId is the load-order form ID; editedBy lists the plugins that
    /// edit the race in load order (the first adds it, the last wins).
    /// </summary>
    public static (Dictionary<int, string>? Layers, string? Why) FromDisk(string raceFormId, IReadOnlyList<string> editedBy, bool female)
    {
        if (editedBy.Count == 0) return (null, "no plugin edits the race");
        if (!uint.TryParse(raceFormId, System.Globalization.NumberStyles.HexNumber, null, out var loadOrderId))
            return (null, "bad form ID " + raceFormId);
        if ((loadOrderId >> 24) == 0xFE) return (null, "a race from a light plugin isn't read");
        var origin = editedBy[0];
        var winner = editedBy[^1];
        var path = Locate(winner);
        if (path == null) return (null, winner + " not found in the game's folders");

        var header = PluginFile.ReadHeader(path);
        var masterIndex = winner.Equals(origin, StringComparison.OrdinalIgnoreCase)
            ? header.Masters.Count
            : header.Masters.FindIndex(m => m.Equals(origin, StringComparison.OrdinalIgnoreCase));
        if (masterIndex < 0) return (null, origin + " isn't a master of " + winner);
        var fileId = ((uint)masterIndex << 24) | (loadOrderId & 0x00FFFFFF);

        var body = FindRecord(path, "RACE", fileId);
        if (body == null) return (null, $"no RACE {fileId:X8} in {winner}");

        // Head data: NAM0, then MNAM (male) or FNAM (female) starts each sex's part; TINI then TINT per layer.
        var layers = new Dictionary<int, string>();
        string? sex = null;
        var inHead = false;
        int? tini = null;
        foreach (var (type, value) in PluginFile.Fields(body))
        {
            var v = value.Span;
            switch (type)
            {
                case "NAM0": inHead = true; break;
                case "MNAM" when inHead: sex = "male"; break;
                case "FNAM" when inHead: sex = "female"; break;
                case "TINI" when sex != null && v.Length >= 2: tini = BinaryPrimitives.ReadUInt16LittleEndian(v); break;
                case "TINT" when sex == (female ? "female" : "male") && tini is int i:
                    layers[i] = Encoding.Latin1.GetString(v).TrimEnd('\0');
                    tini = null;
                    break;
            }
        }
        return layers.Count == 0 ? (null, "the record has no makeup layers for this sex") : (layers, null);
    }

    private static string? Locate(string plugin)
    {
        var folders = SkyLinkSettings.GameFileFolders();
        folders.Reverse();   // highest priority first
        return folders.Select(f => Path.Combine(f, plugin)).FirstOrDefault(File.Exists);
    }

    // Walks the plugin, entering only the top group of that record type; returns the record's fields (decompressed).
    private static byte[]? FindRecord(string path, string recordType, uint fileId)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var hdr = new byte[24];
        while (s.Read(hdr, 0, 24) == 24)
        {
            var type = Encoding.ASCII.GetString(hdr, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
            if (type == "GRUP")
            {
                var groupType = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(12));
                var label = Encoding.ASCII.GetString(hdr, 8, 4);
                if (groupType == 0 && label != recordType) s.Seek(size - 24, SeekOrigin.Current);
                continue;   // otherwise step inside
            }
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8));
            var id = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(12));
            if (type != recordType || id != fileId) { s.Seek(size, SeekOrigin.Current); continue; }
            var data = new byte[size];
            s.ReadExactly(data);
            if ((flags & 0x40000) == 0) return data;
            using var z = new ZLibStream(new MemoryStream(data, 4, data.Length - 4), CompressionMode.Decompress);
            using var outStream = new MemoryStream();
            z.CopyTo(outStream);
            return outStream.ToArray();
        }
        return null;
    }
}
