using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SkyrimMCP.Saves;

// FEAT-265: a native, read-only scan of a Skyrim SE/AE save (.ess). The read path is a port of ReSaver's (FallrimTools by
// Mark Fairchild, Apache-2.0; see docs/save-health-plan.md), for Skyrim SE only: the header, the plugin list, change forms,
// and the Papyrus section up to and including both suspended-stack tables. It never writes.
public static class EssScanner
{
    public sealed class FormatException(string message) : Exception(message);

    /// <summary>Reads only the uncompressed header: fast enough for a list of every save.</summary>
    public static SaveHeader ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var head = new byte[Math.Min(fs.Length, 4096)];
        fs.ReadExactly(head);
        var r = new Buf(head, 0, head.Length);
        return ReadHeader(r);
    }

    static SaveHeader ReadHeader(Buf r)
    {
        var magic = Encoding.ASCII.GetString(r.Bytes(13));
        if (magic != "TESV_SAVEGAME") throw new FormatException($"not a Skyrim save (starts with '{magic}')");
        var headerSize = r.I32();
        var start = r.Pos;
        var h = new SaveHeader { Version = r.I32() };
        if (h.Version < 12) throw new FormatException($"save version {h.Version}: Skyrim LE saves aren't supported (SE/AE only)");
        h.SaveNumber = r.I32();
        h.PlayerName = r.WStr();
        h.Level = r.I32();
        h.Location = r.WStr();
        h.GameDate = r.WStr();
        h.Race = r.WStr();
        h.Sex = r.U16();
        r.F32(); r.F32();
        h.FileTime = DateTime.FromFileTime(r.I64());
        h.ShotWidth = r.I32();
        h.ShotHeight = r.I32();
        h.Compression = r.U16() switch { 0 => "none", 1 => "zlib", 2 => "lz4", var c => $"unknown ({c})" };
        if (r.Pos - start != headerSize) throw new FormatException($"header size {headerSize}, read {r.Pos - start}");
        return h;
    }

    public static SaveScan Scan(string path, bool questStages = false)
    {
        var file = File.ReadAllBytes(path);
        var scan = new SaveScan { File = Path.GetFileName(path), FileSizeMB = Math.Round(file.Length / 1048576.0, 1) };
        var r = new Buf(file, 0, file.Length);
        scan.Header = ReadHeader(r);
        r.Skip(4 * scan.Header.ShotWidth * scan.Header.ShotHeight);
        var startingOffset = r.Pos;

        // The body: compressed (LZ4 in SE/AE, zlib rare) or not. Offsets in the file location table count from the start of the
        // file as if it were uncompressed, so body index = offset - startingOffset.
        byte[] body;
        if (scan.Header.Compression == "none") body = file[startingOffset..];
        else
        {
            var uncompressed = r.I32();
            var compressed = r.I32();
            if (uncompressed < 0 || compressed < 0 || compressed > r.End - r.Pos) throw new FormatException("bad compressed lengths");
            body = scan.Header.Compression == "lz4"
                ? Lz4.Decode(file, r.Pos, compressed, uncompressed)
                : Inflate(file, r.Pos, compressed, uncompressed);
        }
        scan.BodySizeMB = Math.Round(body.Length / 1048576.0, 1);

        var b = new Buf(body, 0, body.Length);
        scan.FormVersion = b.U8();
        if (scan.FormVersion < 77) throw new FormatException($"form version {scan.FormVersion} (SE needs 77+)");
        // Plugins: full ones, then light ones (form version 78+).
        b.I32();
        var full = b.U8();
        for (var i = 0; i < full; i++) scan.Plugins.Add(b.WStr());
        if (scan.FormVersion >= 78)
        {
            var lite = b.U16();
            for (var i = 0; i < lite; i++) scan.LightPlugins.Add(b.WStr());
        }

        // File location table.
        var formIdArrayOffset = b.I32();
        b.I32(); b.I32(); b.I32(); b.I32(); b.I32();
        var table1 = b.I32();
        var table2 = b.I32();
        var table3 = b.I32() + 1;   // Skyrim's count is one short (ReSaver)
        var changeForms = b.I32();
        b.Skip(15 * 4);
        scan.ChangeFormCount = changeForms;

        // The form ID array maps a save's form index to a real form ID, from which the plugin comes.
        int[] formIds = [];
        {
            var fb = new Buf(body, formIdArrayOffset - startingOffset, body.Length);
            var n = fb.I32();
            if (n >= 0 && n < 10_000_000)
            {
                formIds = new int[n];
                for (var i = 0; i < n; i++) formIds[i] = fb.I32();
            }
        }

        for (var i = 0; i < table1 + table2; i++) { b.I32(); b.Skip(b.I32()); }
        if (questStages) scan.QuestStages = new Dictionary<string, List<int[]>>();

        var cfTypes = new Dictionary<int, (int count, long bytes)>();
        for (var i = 0; i < changeForms; i++)
        {
            var refId = b.RefId();
            var changeFlags = b.I32();
            var typeField = b.U8();
            b.U8();
            int len1, len2;
            switch (typeField >> 6)
            {
                case 0: len1 = b.U8(); len2 = b.U8(); break;
                case 1: len1 = b.U16(); len2 = b.U16(); break;
                case 2: len1 = b.I32(); len2 = b.I32(); break;
                default: throw new FormatException($"change form {i}: bad length size");
            }
            var t = typeField & 0x3F;
            // FEAT-269: change forms per plugin (what each mod's forms changed during play).
            {
                var fid = ResolveRefId(refId, formIds);
                var owner = fid == 0 ? "(unresolved)" : PluginFor(fid, scan.Plugins, scan.LightPlugins) ?? "(plugin index past the list)";
                scan.ChangeFormsByPlugin[owner] = scan.ChangeFormsByPlugin.GetValueOrDefault(owner) + 1;
            }
            // FEAT-263: a quest's stage list with each stage's status (CHANGE_QUEST_STAGES, flag bit 31): the record of stages
            // done that the engine's in-memory list didn't give (it read back only zeros).
            if (scan.QuestStages != null && t == 8 && (changeFlags & unchecked((int)0x80000000)) != 0)
            {
                try
                {
                    var data = len2 > 0 ? Inflate(body, b.Pos, len1, len2) : body.AsSpan(b.Pos, len1).ToArray();
                    var q = new Buf(data, 0, data.Length);
                    if ((changeFlags & 1) != 0) { q.I32(); q.U16(); }   // change form flags
                    if ((changeFlags & 2) != 0) q.U16();                // quest flags
                    if ((changeFlags & 4) != 0) q.F32();                // script delay
                    var count = VsVal(q);
                    var stages = new List<int[]>(count);
                    for (var k = 0; k < count; k++) stages.Add([q.S16(), q.U8()]);
                    var formId = ResolveRefId(refId, formIds);
                    if (formId != 0) scan.QuestStages[formId.ToString("X8")] = stages;
                }
                catch (Exception ex) when (ex is FormatException or InvalidDataException) { }
            }
            b.Skip(len1);
            cfTypes.TryGetValue(t, out var cur);
            cfTypes[t] = (cur.count + 1, cur.bytes + len1);
        }
        scan.ChangeFormsByType = cfTypes.OrderByDescending(kv => kv.Value.bytes).Take(10)
            .Select(kv => new ChangeFormType(ChangeFormTypeName(kv.Key), kv.Value.count, Math.Round(kv.Value.bytes / 1048576.0, 2))).ToList();

        for (var i = 0; i < table3; i++)
        {
            if (b.Pos >= b.End) break;
            var type = b.I32();
            var size = b.I32();
            if (type == 1001)
            {
                scan.PapyrusSizeMB = Math.Round(size / 1048576.0, 2);
                var p = new PapyrusReader(new Buf(body, b.Pos, b.Pos + size), scan, scan.Plugins, scan.LightPlugins, formIds);
                try { p.Read(); scan.PapyrusParsed = true; }
                catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException)
                {
                    scan.PapyrusError = $"{p.Stage}: {ex.Message}";
                }
                p.Summarise();
            }
            b.Skip(size);
        }
        if (!scan.PapyrusParsed && scan.PapyrusError == null) scan.PapyrusError = "no Papyrus section found";
        return scan;
    }

    static int VsVal(Buf b)
    {
        var first = b.U8();
        return (first & 3) switch
        {
            0 => first >> 2,
            1 => (first | (b.U8() << 8)) >> 2,
            _ => (first | (b.U8() << 8) | (b.U8() << 16)) >> 2,
        };
    }

    /// <summary>A save's 3-byte reference to a form ID (0 when it can't be resolved).</summary>
    internal static int ResolveRefId(int refId, int[] formIds)
    {
        var value = refId & 0x3FFFFF;
        return ((refId >> 22) & 3) switch
        {
            0 => value > 0 && value - 1 < formIds.Length ? formIds[value - 1] : 0,
            1 => value,
            2 => unchecked((int)0xFF000000) | value,
            _ => 0,
        };
    }

    static byte[] Inflate(byte[] src, int at, int length, int expected)
    {
        using var input = new MemoryStream(src, at, length);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        var output = new byte[expected];
        z.ReadExactly(output);
        return output;
    }

    static readonly string[] CfNames =
    [
        "REFR", "ACHR", "PMIS", "PGRE", "PBEA", "PFLA", "CELL", "INFO", "QUST", "NPC_", "ACTI", "TACT", "ARMO", "BOOK", "CONT",
        "DOOR", "INGR", "LIGH", "MISC", "APPA", "STAT", "MSTT", "FURN", "WEAP", "AMMO", "KEYM", "ALCH", "IDLM", "NOTE", "ECZN",
        "CLAS", "FACT", "PACK", "NAVM", "WOOP", "MGEF", "SMQN", "SCEN", "LCTN", "RELA", "PHZD", "PBAR", "PCON", "FLST", "LVLN",
        "LVLI", "LVSP", "PARW", "ENCH",
    ];
    static string ChangeFormTypeName(int t) => t < CfNames.Length ? CfNames[t] : $"type {t}";

    /// <summary>Plugin of a form ID, from the save's own plugin list.</summary>
    internal static string? PluginFor(int formId, List<string> full, List<string> light)
    {
        var top = (formId >> 24) & 0xFF;
        if (top == 0xFF) return "(created in game)";
        if (top == 0xFE)
        {
            var li = (formId >> 12) & 0xFFF;
            return li < light.Count ? light[li] : null;
        }
        return top < full.Count ? full[top] : null;
    }

    // ---------------------------------------------------------------------------------------------------------------------

    internal sealed class Buf(byte[] bytes, int pos, int end)
    {
        public readonly byte[] B = bytes;
        public int Pos = pos;
        public readonly int End = end;

        void Need(int n)
        {
            if (n < 0 || Pos + n > End) throw new FormatException($"ran past the end of the block at byte {Pos} (needed {n})");
        }
        public byte U8() { Need(1); return B[Pos++]; }
        public sbyte S8() => unchecked((sbyte)U8());
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(B.AsSpan(Pos)); Pos += 2; return v; }
        public short S16() => unchecked((short)U16());
        public int I32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(B.AsSpan(Pos)); Pos += 4; return v; }
        public long I64() { Need(8); var v = BinaryPrimitives.ReadInt64LittleEndian(B.AsSpan(Pos)); Pos += 8; return v; }
        public float F32() { Need(4); var v = BinaryPrimitives.ReadSingleLittleEndian(B.AsSpan(Pos)); Pos += 4; return v; }
        public void Skip(int n) { Need(n); Pos += n; }
        public byte[] Bytes(int n) { Need(n); var v = B.AsSpan(Pos, n).ToArray(); Pos += n; return v; }
        public string WStr()
        {
            var n = U16();
            Need(n);
            var s = Encoding.UTF8.GetString(B, Pos, n);
            Pos += n;
            return s;
        }
        public int RefId()
        {
            Need(3);
            var v = (B[Pos] << 16) | (B[Pos + 1] << 8) | B[Pos + 2];
            Pos += 3;
            return v;
        }
    }

    static class Lz4
    {
        // LZ4 block format (no frame), as SE/AE saves use it.
        public static byte[] Decode(byte[] src, int at, int length, int expected)
        {
            var dst = new byte[expected];
            int s = at, end = at + length, d = 0;
            while (s < end)
            {
                var token = src[s++];
                var lit = token >> 4;
                if (lit == 15) { byte x; do { x = src[s++]; lit += x; } while (x == 255); }
                if (d + lit > expected || s + lit > end) throw new FormatException("LZ4: literal run past the end");
                Buffer.BlockCopy(src, s, dst, d, lit);
                s += lit; d += lit;
                if (s >= end) break;
                var offset = src[s] | (src[s + 1] << 8);
                s += 2;
                if (offset == 0 || offset > d) throw new FormatException("LZ4: bad match offset");
                var match = (token & 15) + 4;
                if ((token & 15) == 15) { byte x; do { x = src[s++]; match += x; } while (x == 255); }
                if (d + match > expected) throw new FormatException("LZ4: match past the end");
                var from = d - offset;
                for (var i = 0; i < match; i++) dst[d++] = dst[from + i];
            }
            if (d != expected) throw new FormatException($"LZ4: decoded {d} of {expected} bytes");
            return dst;
        }
    }
}

public sealed class SaveHeader
{
    public int Version { get; set; }
    public int SaveNumber { get; set; }
    public string PlayerName { get; set; } = "";
    public int Level { get; set; }
    public string Location { get; set; } = "";
    public string GameDate { get; set; } = "";
    public string Race { get; set; } = "";
    public int Sex { get; set; }
    public DateTime FileTime { get; set; }
    public int ShotWidth { get; set; }
    public int ShotHeight { get; set; }
    public string Compression { get; set; } = "";
}

public sealed record ChangeFormType(string Type, int Count, double SizeMB);
// RunningOn: the plugin of the object the script runs on (the player and base-game objects are Skyrim.esm; an active magic
// effect's script runs on the actor it's applied to), not necessarily the mod that defines the script.
public sealed record NameCount(string Name, int Count, string? RunningOn = null);
// Plugin totals: Plugin = where the objects come from, Handlers = how many handlers and the biggest.
public sealed record ModCount(string Plugin, int Count, string Handlers);

public sealed class SaveScan
{
    public string File { get; set; } = "";
    public double FileSizeMB { get; set; }
    public double BodySizeMB { get; set; }
    public SaveHeader Header { get; set; } = new();
    public int FormVersion { get; set; }
    public List<string> Plugins { get; set; } = [];
    public List<string> LightPlugins { get; set; } = [];
    public int ChangeFormCount { get; set; }
    public List<ChangeFormType> ChangeFormsByType { get; set; } = [];
    public double PapyrusSizeMB { get; set; }
    public bool PapyrusParsed { get; set; }
    public string? PapyrusError { get; set; }

    // Papyrus counts.
    public int Strings { get; set; }
    public int Scripts { get; set; }
    public int ScriptInstances { get; set; }
    public int References { get; set; }
    public int Arrays { get; set; }
    public int ActiveScripts { get; set; }
    public int FunctionMessages { get; set; }
    public int SuspendedStacks1 { get; set; }
    public int SuspendedStacks2 { get; set; }

    // Findings (ReSaver's rules: an undefined script is one whose definition has no parent type and isn't a native script,
    // which is how the game saves a script whose .pex it couldn't load; unattached = an instance bound to no object).
    public List<string> UndefinedScripts { get; set; } = [];
    public int UndefinedInstances { get; set; }
    public List<NameCount> UndefinedInstancesByScript { get; set; } = [];
    public int UnattachedInstances { get; set; }
    public List<NameCount> UnattachedInstancesByScript { get; set; } = [];
    public int UndefinedThreads { get; set; }
    public int TerminatedThreads { get; set; }
    public int SuspendedThreads { get; set; }

    // Stacks: running threads by the function they started in (entry) and by where they are now (top), and queued calls
    // (suspended stacks with a pending call: where a flood like the Aniya one lives) by script and event.
    public List<NameCount> ThreadsByEntryPoint { get; set; } = [];
    public List<NameCount> ThreadsByTopFrame { get; set; } = [];
    public List<NameCount> QueuedCalls { get; set; } = [];
    public List<NameCount> FunctionMessageCalls { get; set; } = [];

    // Totals per plugin (Name = plugin, Plugin field = how many handlers and the biggest one).
    public List<ModCount> QueuedByPlugin { get; set; } = [];
    public List<ModCount> ThreadsByPlugin { get; set; } = [];
    public List<ModCount> FunctionMessagesByPlugin { get; set; } = [];

    // FEAT-269: per plugin, change forms and script instances attached to its forms.
    public Dictionary<string, int> ChangeFormsByPlugin { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> InstancesByPlugin { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // FEAT-263: quest form ID (as in the save's load order) -> [stage, status] pairs, when asked for.
    public Dictionary<string, List<int[]>>? QuestStages { get; set; }
}
