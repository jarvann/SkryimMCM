// Provenance: written from the UESP "Skyrim Mod:Mod File Format" documentation (main page, TES4, GLOB, QUST, VMAD Field) and
// from inspecting the bytes of the game's own plugin files. No code from other plugin libraries (Mutagen, xEdit, nifly and the
// like) was used or consulted.
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SkyrimMCP.Plugins;

/// <summary>The TES4 header of a plugin: what the game reads before any record.</summary>
public sealed class PluginHeader
{
    public required string Name { get; init; }
    public uint Flags { get; init; }
    public ushort FormVersion { get; init; }
    public float Version { get; init; }
    public int NumRecords { get; init; }
    public uint NextObjectId { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public List<string> Masters { get; init; } = [];

    public bool MasterFlag => (Flags & 0x1) != 0;
    public bool Localized => (Flags & 0x80) != 0;
    public bool LightFlag => (Flags & 0x200) != 0;
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();
    // The game treats an .esl as both master and light whatever its flags say.
    public bool IsLight => LightFlag || Extension == ".esl";
    public bool IsMaster => MasterFlag || Extension is ".esm" or ".esl";
    /// <summary>1.71 headers (game 1.6.1130+) let light plugins use object IDs from 0x001; 1.70 ones start at 0x800.</summary>
    public bool Header171 => Version >= 1.705f;
    public string VersionText => Version.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class TypeCount
{
    public int New;
    public int Overrides;
}

/// <summary>What a header walk of the whole file found. Object IDs are the low 24 bits of new records' FormIDs.</summary>
public sealed class PluginScan
{
    public required PluginHeader Header { get; init; }
    public Dictionary<string, TypeCount> Types { get; } = new(StringComparer.Ordinal);
    public int Records, Groups, NewRecords, Overrides;
    /// <summary>New records whose master index is past the masters list (the game reads them as this plugin's own).</summary>
    public int OddMasterIndex;
    public uint MinNewId = uint.MaxValue, MaxNewId;
    /// <summary>New records by object-ID band: 0x000 (never valid), 0x001-0x7FF, 0x800-0xFFF, above 0xFFF.</summary>
    public int IdZero, IdLow, IdLightBand, IdHigh;
    public int NewCells;
    public int Compressed;
    /// <summary>The walk stopped once there were too many new records for a light plugin (the counts are partial).</summary>
    public bool StoppedEarly;
    public bool Truncated;
    public long FileSize;
}

public sealed record LightEligibility(
    bool AlreadyLight, bool Eligible, bool EligibleWithHeader171, bool NeedsCompacting, bool TooManyRecords, List<string> Reasons, string Verdict);

/// <summary>Reads plugin headers and walks record headers without loading the file or decompressing anything.</summary>
public static class PluginFile
{
    // Light plugins: 1.70 headers 0x800-0xFFF (2,048 forms); 1.71 headers 0x001-0xFFF (4,095), game 1.6.1130 and later.
    public const int LightMax170 = 0x800;
    public const int LightMax171 = 0xFFF;
    public const int FullPluginLimit = 254;   // slots 0x00-0xFD; 0xFE holds the light plugins, 0xFF is the game's own
    public const int LightPluginLimit = 4096;

    static readonly Encoding Text = Encoding.Latin1;

    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

    public static PluginHeader ReadHeader(string path)
    {
        using var s = OpenRead(path);
        return ReadHeader(new Reader(s, 64 * 1024), Path.GetFileName(path));
    }

    public static PluginScan Scan(string path, int stopAfterNew = int.MaxValue)
    {
        using var s = OpenRead(path);
        return Scan(s, Path.GetFileName(path), stopAfterNew);
    }

    public static PluginScan Scan(Stream stream, string name, int stopAfterNew = int.MaxValue)
    {
        var r = new Reader(stream, 1 << 20);
        var header = ReadHeader(r, name);
        var scan = new PluginScan { Header = header, FileSize = stream.Length };
        var own = (uint)header.Masters.Count;
        var types = new Dictionary<uint, TypeCount>();
        var hdr = new byte[24];
        const uint Grup = 0x50555247;   // "GRUP" read little-endian
        const uint Cell = 0x4C4C4543, Wrld = 0x444C5257;
        while (r.TryRead(hdr))
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(hdr);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
            if (type == Grup)
            {
                // A group's size includes its own header, and its records follow straight after: keep walking into it.
                if (size < 24) throw new InvalidDataException($"{name}: group with size {size} at byte {r.Position - 24}");
                scan.Groups++;
                continue;
            }
            scan.Records++;
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8));
            var formId = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(12));
            if ((flags & 0x40000) != 0) scan.Compressed++;
            if (!types.TryGetValue(type, out var tc)) types[type] = tc = new TypeCount();
            var index = formId >> 24;
            if (index >= own)
            {
                tc.New++;
                scan.NewRecords++;
                if (index > own) scan.OddMasterIndex++;
                var id = formId & 0xFFFFFF;
                if (id < scan.MinNewId) scan.MinNewId = id;
                if (id > scan.MaxNewId) scan.MaxNewId = id;
                if (id == 0) scan.IdZero++;
                else if (id < 0x800) scan.IdLow++;
                else if (id <= 0xFFF) scan.IdLightBand++;
                else scan.IdHigh++;
                if (type is Cell or Wrld) scan.NewCells++;
                if (scan.NewRecords > stopAfterNew) { scan.StoppedEarly = true; break; }
            }
            else
            {
                tc.Overrides++;
                scan.Overrides++;
            }
            r.Skip(size);
        }
        scan.Truncated = !scan.StoppedEarly && r.Position > stream.Length;
        foreach (var (k, v) in types) scan.Types[TypeName(k)] = v;
        return scan;
    }

    static string TypeName(uint t) => string.Create(4, t, (span, v) =>
    {
        for (var i = 0; i < 4; i++) { var c = (char)((v >> (8 * i)) & 0xFF); span[i] = c is >= ' ' and < (char)127 ? c : '?'; }
    });

    static PluginHeader ReadHeader(Reader r, string name)
    {
        var hdr = new byte[24];
        if (!r.TryRead(hdr) || Encoding.ASCII.GetString(hdr, 0, 4) != "TES4")
            throw new InvalidDataException($"{name} isn't a Skyrim plugin (no TES4 header).");
        var size = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
        if (size > 64 * 1024 * 1024) throw new InvalidDataException($"{name}: header record of {size} bytes.");
        var data = new byte[size];
        if (!r.TryRead(data)) throw new InvalidDataException($"{name} ends inside its header.");
        float version = 0; int numRecords = 0; uint next = 0; string? author = null, description = null;
        var masters = new List<string>();
        foreach (var (type, value) in Fields(data))
        {
            switch (type)
            {
                case "HEDR" when value.Length >= 12:
                    version = BinaryPrimitives.ReadSingleLittleEndian(value.Span);
                    numRecords = BinaryPrimitives.ReadInt32LittleEndian(value.Span[4..]);
                    next = BinaryPrimitives.ReadUInt32LittleEndian(value.Span[8..]);
                    break;
                case "CNAM": author = ZString(value.Span); break;
                case "SNAM": description = ZString(value.Span); break;
                case "MAST": masters.Add(ZString(value.Span)); break;
            }
        }
        return new PluginHeader
        {
            Name = name,
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8)),
            FormVersion = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(20)),
            Version = version, NumRecords = numRecords, NextObjectId = next,
            Author = string.IsNullOrEmpty(author) ? null : author,
            Description = string.IsNullOrEmpty(description) ? null : description,
            Masters = masters,
        };
    }

    /// <summary>A record's fields in order. An XXXX field carries the 32-bit size of the field after it.</summary>
    public static IEnumerable<(string Type, ReadOnlyMemory<byte> Value)> Fields(ReadOnlyMemory<byte> data)
    {
        var p = 0;
        uint? big = null;
        while (p + 6 <= data.Length)
        {
            var type = Encoding.ASCII.GetString(data.Span.Slice(p, 4));
            int size = BinaryPrimitives.ReadUInt16LittleEndian(data.Span[(p + 4)..]);
            p += 6;
            if (big is uint b) { size = (int)b; big = null; }
            if (p + size > data.Length) yield break;
            var value = data.Slice(p, size);
            p += size;
            if (type == "XXXX" && size == 4) { big = BinaryPrimitives.ReadUInt32LittleEndian(value.Span); continue; }
            yield return (type, value);
        }
    }

    static string ZString(ReadOnlySpan<byte> b)
    {
        var end = b.IndexOf((byte)0);
        return Text.GetString(end < 0 ? b : b[..end]);
    }

    /// <summary>
    /// Whether the plugin could carry the light flag without renumbering anything: few enough new records, all with object IDs
    /// inside the light range for its header version. Compacting (renumbering) works too, but a save that has any of the
    /// plugin's forms then points at the wrong ones.
    /// </summary>
    public static LightEligibility CheckLight(PluginScan s)
    {
        var h = s.Header;
        var reasons = new List<string>();
        var n = s.NewRecords;
        var outside170 = s.IdZero + s.IdLow + s.IdHigh;
        var outside171 = s.IdZero + s.IdHigh;
        if (h.IsLight)
        {
            var bad = h.Header171 ? outside171 : outside170;
            if (bad > 0)
                reasons.Add($"{bad} new record(s) have object IDs outside the light range {(h.Header171 ? "0x001-0xFFF" : "0x800-0xFFF")} " +
                            $"(header {h.VersionText}): the game misreads those forms. The plugin needs compacting by its author.");
            var verdict = bad > 0 ? "Light, but broken: some of its forms are outside the light range." : "Already light.";
            if (h.Header171) reasons.Add("Its 1.71 header needs game version 1.6.1130 or later.");
            return new LightEligibility(true, bad == 0, false, bad > 0, false, reasons, verdict);
        }
        if (s.StoppedEarly || n > LightMax171)
        {
            reasons.Add(s.StoppedEarly ? $"More than {LightMax171:N0} new records (stopped counting at {n:N0}): too many for a light plugin."
                                       : $"{n:N0} new records: a light plugin holds at most {LightMax171:N0}.");
            return new LightEligibility(false, false, false, false, true, reasons, "Can't be light: too many new records.");
        }
        if (s.NewCells > 0) reasons.Add(NewCellCaution(s.NewCells));
        if (n == 0)
        {
            reasons.Insert(0, "No new records (only overrides): flagging it light changes nothing it defines.");
            return new LightEligibility(false, true, false, false, false, reasons, "Can be light as it is.");
        }
        var range = $"object IDs 0x{s.MinNewId:X3}-0x{s.MaxNewId:X3}";
        if (outside170 == 0 && n <= LightMax170)
        {
            reasons.Insert(0, $"{n:N0} new records, {range}: inside the light range for its header.");
            return new LightEligibility(false, true, false, false, false, reasons, "Can be light as it is (set the light flag; no renumbering).");
        }
        if (outside171 == 0)
        {
            if (h.Header171)
            {
                reasons.Insert(0, $"{n:N0} new records, {range}: inside the 1.71 light range.");
                reasons.Add("Its 1.71 header needs game version 1.6.1130 or later.");
                return new LightEligibility(false, true, false, false, false, reasons, "Can be light as it is (set the light flag; no renumbering).");
            }
            reasons.Insert(0, $"{n:N0} new records, {range}: some below 0x800, which only 1.71 headers allow in a light plugin.");
            reasons.Add("Raising the header to 1.71 and setting the light flag keeps every FormID; it needs game version 1.6.1130 or later.");
            return new LightEligibility(false, false, true, false, false, reasons, "Can be light with a 1.71 header (game 1.6.1130+), no renumbering.");
        }
        reasons.Insert(0, $"{n:N0} new records, {range}: {outside171:N0} outside 0x001-0xFFF.");
        reasons.Add("Making it light needs compacting, which renumbers its forms: saves that use any of them break, and plugins or " +
                    "files that point at them (patches, FaceGen, SKSE plugin configs) need updating. Only worth it for a plugin not yet in a playthrough.");
        return new LightEligibility(false, false, false, true, false, reasons, "Only by compacting (renumbers forms; breaks saves that use them).");
    }

    static string NewCellCaution(int n) =>
        $"It adds {n} new cell or worldspace record(s): flagging plugins that add cells light has been reported to lose changes made " +
        "in those cells on some game versions. Check the mod's page before flagging it.";

    /// <summary>Records with their fields and decoded scripts, for small files (a round trip of a built plugin), or one record type.</summary>
    public static List<JsonObject> ReadRecords(Stream stream, string name, int max = 200, string? onlyType = null)
    {
        var r = new Reader(stream, 1 << 16);
        var header = ReadHeader(r, name);
        var list = new List<JsonObject>();
        var hdr = new byte[24];
        while (list.Count < max && r.TryRead(hdr))
        {
            var type = Encoding.ASCII.GetString(hdr, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
            if (type == "GRUP") continue;
            if (onlyType != null && type != onlyType) { r.Skip(size); continue; }
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8));
            var formId = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(12));
            var o = new JsonObject { ["type"] = type, ["formId"] = $"0x{formId:X8}", ["formVersion"] = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(20)) };
            if (flags != 0) o["flags"] = $"0x{flags:X}";
            if ((flags & 0x40000) != 0) { o["compressed"] = true; r.Skip(size); list.Add(o); continue; }
            var data = new byte[size];
            if (!r.TryRead(data)) throw new InvalidDataException($"{name} ends inside a {type} record.");
            var fields = new JsonArray();
            foreach (var (ft, value) in Fields(data))
            {
                fields.Add($"{ft}({value.Length})");
                var v = value.Span;
                switch (ft)
                {
                    case "EDID": o["editorId"] = ZString(v); break;
                    case "FULL" when !header.Localized: o["name"] = ZString(v); break;
                    case "FNAM" when type == "GLOB" && v.Length == 1: o["valueType"] = ((char)v[0]) switch { 's' => "short", 'l' => "long", 'f' => "float", var c => c.ToString() }; break;
                    case "FLTV" when v.Length == 4: o["value"] = BinaryPrimitives.ReadSingleLittleEndian(v); break;
                    case "DNAM" when type == "QUST" && v.Length >= 12:
                        var qf = BinaryPrimitives.ReadUInt16LittleEndian(v);
                        o["questFlags"] = $"0x{qf:X4}";
                        o["startGameEnabled"] = (qf & 0x0001) != 0;
                        o["runOnce"] = (qf & 0x0100) != 0;
                        o["priority"] = v[2];
                        o["questType"] = BinaryPrimitives.ReadUInt32LittleEndian(v[8..]);
                        break;
                    case "VMAD": o["scripts"] = DecodeVmad(v, header.Masters, name, out var tail); if (tail > 0) o["vmadTrailingBytes"] = tail; break;
                }
            }
            o["fields"] = fields;
            list.Add(o);
        }
        return list;
    }

    /// <summary>The primary scripts section of a VMAD field (what every record type shares); fragments are only counted.</summary>
    public static JsonArray DecodeVmad(ReadOnlySpan<byte> b, List<string> masters, string self, out int trailing)
    {
        var p = 0;
        var version = BinaryPrimitives.ReadInt16LittleEndian(b);
        var objFormat = BinaryPrimitives.ReadInt16LittleEndian(b[2..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(b[4..]);
        p = 6;
        var scripts = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            var script = new JsonObject { ["name"] = WString(b, ref p), ["vmadVersion"] = version, ["objFormat"] = objFormat };
            if (version >= 4) script["status"] = b[p++];
            int props = BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
            p += 2;
            var list = new JsonArray();
            for (var j = 0; j < props; j++)
            {
                var prop = new JsonObject { ["name"] = WString(b, ref p) };
                var type = b[p++];
                if (version >= 4) prop["status"] = b[p++];
                prop["type"] = type switch { 1 => "object", 2 => "string", 3 => "int", 4 => "float", 5 => "bool", >= 11 and <= 15 => "array", _ => $"type {type}" };
                if (type >= 11)
                {
                    var n = BinaryPrimitives.ReadUInt32LittleEndian(b[p..]);
                    p += 4;
                    var items = new JsonArray();
                    for (var k = 0; k < n; k++) items.Add(Value(b, ref p, type - 10, objFormat, masters, self));
                    prop["value"] = items;
                }
                else prop["value"] = Value(b, ref p, type, objFormat, masters, self);
                list.Add(prop);
            }
            script["properties"] = list;
            scripts.Add(script);
        }
        trailing = b.Length - p;
        return scripts;
    }

    static JsonNode? Value(ReadOnlySpan<byte> b, ref int p, int type, short objFormat, List<string> masters, string self)
    {
        switch (type)
        {
            case 1:
                uint formId; short alias;
                if (objFormat == 1) { formId = BinaryPrimitives.ReadUInt32LittleEndian(b[p..]); alias = BinaryPrimitives.ReadInt16LittleEndian(b[(p + 4)..]); }
                else { alias = BinaryPrimitives.ReadInt16LittleEndian(b[(p + 2)..]); formId = BinaryPrimitives.ReadUInt32LittleEndian(b[(p + 4)..]); }
                p += 8;
                var index = (int)(formId >> 24);
                var owner = index < masters.Count ? masters[index] : self;
                return $"{owner}|0x{formId & 0xFFFFFF:X6}" + (alias >= 0 ? $" (alias {alias})" : "");
            case 2: return WString(b, ref p);
            case 3: { var v = BinaryPrimitives.ReadInt32LittleEndian(b[p..]); p += 4; return v; }
            case 4: { var v = BinaryPrimitives.ReadSingleLittleEndian(b[p..]); p += 4; return v; }
            case 5: return b[p++] != 0;
            default: throw new InvalidDataException($"unknown script property type {type}");
        }
    }

    static string WString(ReadOnlySpan<byte> b, ref int p)
    {
        int n = BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
        var s = Text.GetString(b.Slice(p + 2, n));
        p += 2 + n;
        return s;
    }

    /// <summary>Buffered reads with cheap skips: most record data is skipped, so seek only when a skip leaves the buffer.</summary>
    sealed class Reader(Stream s, int bufferSize)
    {
        readonly byte[] _buf = new byte[bufferSize];
        int _len, _pos;
        long _bufStart = s.Position;

        public long Position => _bufStart + _pos;

        public bool TryRead(Span<byte> into)
        {
            var done = 0;
            while (done < into.Length)
            {
                if (_pos == _len && !Fill()) return false;
                var n = Math.Min(into.Length - done, _len - _pos);
                _buf.AsSpan(_pos, n).CopyTo(into[done..]);
                _pos += n;
                done += n;
            }
            return true;
        }

        public void Skip(long n)
        {
            if (_pos + n <= _len) { _pos += (int)n; return; }
            var target = Position + n;
            _bufStart = target;
            _pos = _len = 0;
            if (target <= s.Length) s.Seek(target, SeekOrigin.Begin);
        }

        bool Fill()
        {
            _bufStart += _len;
            _pos = 0;
            if (_bufStart >= s.Length) { _len = 0; return false; }
            if (s.Position != _bufStart) s.Seek(_bufStart, SeekOrigin.Begin);
            _len = s.Read(_buf, 0, _buf.Length);
            return _len > 0;
        }
    }
}

/// <summary>A new plugin to write: the header and a few record types.</summary>
public sealed class NewPlugin
{
    public required string Name { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public bool Light { get; init; } = true;
    public List<string> Masters { get; init; } = [];
    public List<NewRecord> Records { get; init; } = [];
}

public abstract class NewRecord
{
    /// <summary>A fixed object ID (0x800-0xFFF), or null for the next free one. Records added later must not move earlier ones.</summary>
    public uint? ObjectId { get; init; }
    public required string EditorId { get; init; }
    public abstract string Type { get; }
}

public sealed class NewGlobal : NewRecord
{
    public override string Type => "GLOB";
    public char ValueType { get; init; } = 'f';   // 's' short, 'l' long, 'f' float: stored as a float either way
    public float Value { get; init; }
    public bool Constant { get; init; }
}

public sealed class NewQuest : NewRecord
{
    public override string Type => "QUST";
    public string? Name { get; init; }
    public bool StartGameEnabled { get; init; }
    public bool RunOnce { get; init; }
    public byte Priority { get; init; }
    public List<NewScript> Scripts { get; init; } = [];
    public List<NewAlias> Aliases { get; init; } = [];
}

/// <summary>A quest alias: an existing reference (Forced), or one the quest makes (Create, a base form) at another alias.</summary>
public sealed record NewAlias(uint Id, string Name, string? Forced = null, string? Create = null, uint CreateAt = 0);

/// <summary>A colour (hair, skin). 1.6: an exported character's own hair colour.</summary>
public sealed class NewColour : NewRecord
{
    public override string Type => "CLFM";
    public byte Red { get; init; }
    public byte Green { get; init; }
    public byte Blue { get; init; }
}

public sealed class NewOutfit : NewRecord
{
    public override string Type => "OTFT";
    public List<string> Items { get; init; } = [];
}

/// <summary>
/// An NPC (1.6, INFRA-024: SkyLink's NPC slots and the player's own exported characters). References are "Plugin.esm|0x00000014"
/// or the editor ID of another record in the plugin.
/// </summary>
public sealed class NewNpc : NewRecord
{
    public override string Type => "NPC_";
    public string? Name { get; init; }
    public bool Female { get; init; }
    public bool Unique { get; init; } = true;
    public bool Essential { get; init; }
    public bool Protected { get; init; }
    public required string Race { get; init; }
    public string? Voice { get; init; }
    public string? Class { get; init; }
    public string? Outfit { get; init; }
    public string? CombatStyle { get; init; }
    public List<string> Packages { get; init; } = [];
    public List<(string Ref, sbyte Rank)> Factions { get; init; } = [];
    public List<string> HeadParts { get; init; } = [];
    public string? HairColour { get; init; }
    public string? HeadTexture { get; init; }
    public string? Skin { get; init; }
    public (byte R, byte G, byte B)? BodyTint { get; init; }
    /// <summary>Grows with the player (level is then the multiplier times 1000).</summary>
    public bool LevelsWithPlayer { get; init; }
    public byte Aggression { get; init; }
    public byte Confidence { get; init; } = 2;
    public byte Assistance { get; init; }
    public ushort Level { get; init; } = 1;
    public float Height { get; init; } = 1f;
    public float Weight { get; init; } = 50f;

    internal IEnumerable<string> References()
    {
        yield return Race;
        foreach (var r in new[] { Voice, Class, Outfit, CombatStyle, HairColour, HeadTexture, Skin }) if (!string.IsNullOrWhiteSpace(r)) yield return r;
        foreach (var r in Packages) yield return r;
        foreach (var (r, _) in Factions) yield return r;
        foreach (var r in HeadParts) yield return r;
    }
}

/// <summary>A faction with named ranks (1.6, FEAT-304). Relations: reaction 0 neutral, 1 enemy, 2 ally, 3 friend; "this" is itself.</summary>
public sealed class NewFaction : NewRecord
{
    public override string Type => "FACT";
    public string? Name { get; init; }
    public List<(int Rank, string? Male, string? Female)> Ranks { get; init; } = [];
    public List<(string Ref, int Reaction)> Relations { get; init; } = [];
}

public sealed record NewScript(string Name, List<NewProperty> Properties);

/// <summary>Kind: int, float, bool, string or object. An object value is "Plugin.esm|0x00000014", or an editor ID made in the same call.</summary>
public sealed record NewProperty(string Name, string Kind, JsonNode? Value);

public sealed class BuiltPlugin
{
    public required byte[] Bytes { get; init; }
    public required List<string> Masters { get; init; }
    public required List<(string Type, string EditorId, uint FormId)> Assigned { get; init; }
    public float Version { get; init; }
    public uint NextObjectId { get; init; }
    public int NumRecords { get; init; }
    public uint Flags { get; init; }
    public List<string> Notes { get; init; } = [];
}

/// <summary>Writes small plugins: TES4 header, then one top group per record type in the game's own group order.</summary>
public static class PluginWriter
{
    // Top-group order as Skyrim.esm stores it (UESP); the engine may not care, but other tools expect it.
    static readonly string[] GroupOrder = ("GMST KYWD LCRT AACT TXST GLOB CLAS FACT HDPT HAIR EYES RACE SOUN ASPC MGEF SCPT LTEX ENCH SPEL SCRL " +
        "ACTI TACT ARMO BOOK CONT DOOR INGR LIGH MISC APPA STAT SCOL MSTT PWAT GRAS TREE CLDC FLOR FURN WEAP AMMO NPC_ LVLN KEYM ALCH IDLM " +
        "COBJ PROJ HAZD SLGM LVLI WTHR CLMT SPGD RFCT REGN NAVI CELL WRLD DIAL QUST IDLE PACK CSTY LSCR LVSP ANIO WATR EFSH EXPL DEBR IMGS " +
        "IMAD FLST PERK BPTD ADDN AVIF CAMS CPTH VTYP MATT IPCT IPDS ARMA ECZN LCTN MESG RGDL DOBJ LGTM MUSC FSTP FSTS SMBN SMQN SMEN DLBR " +
        "MUST DLVW WOOP SHOU EQUP RELA SCEN ASTP OTFT ARTO MATO MOVT SNDR DUAL SNCT SOPM COLL CLFM REVB").Split(' ');

    const ushort FormVersion = 44;   // Skyrim SE records
    static readonly Encoding Text = Encoding.Latin1;
    static readonly Regex EditorIdPattern = new("^[A-Za-z][A-Za-z0-9_]{0,199}$", RegexOptions.Compiled);
    static readonly Regex ObjectRef = new(@"^\s*(?<plugin>[^|]+?)\s*\|\s*(?:0x)?(?<id>[0-9A-Fa-f]{1,8})\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Builds the plugin in memory. loadOrderIndex orders the masters the way the game loads them (unknown ones keep their place
    /// at the end). Throws ArgumentException with a plain message for anything that can't be written.
    /// </summary>
    public static BuiltPlugin Build(NewPlugin spec, Func<string, int?>? loadOrderIndex = null)
    {
        var ext = Path.GetExtension(spec.Name).ToLowerInvariant();
        var light = spec.Light || ext == ".esl";
        var notes = new List<string>();
        if (spec.Records.Count == 0) throw new ArgumentException("No records to write.");
        var dup = spec.Records.GroupBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new ArgumentException($"Editor ID '{dup.Key}' is used twice.");
        foreach (var r in spec.Records)
            if (!EditorIdPattern.IsMatch(r.EditorId))
                throw new ArgumentException($"Editor ID '{r.EditorId}': use letters, digits and underscores, starting with a letter.");

        // Masters: the ones asked for, plus every plugin a script property points at.
        var masters = new List<string>();
        void AddMaster(string m)
        {
            m = m.Trim();
            if (m.Length == 0 || masters.Contains(m, StringComparer.OrdinalIgnoreCase)) return;
            if (m.Equals(spec.Name, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{spec.Name} can't be its own master.");
            if (Path.GetExtension(m).ToLowerInvariant() is not (".esm" or ".esp" or ".esl")) throw new ArgumentException($"Master '{m}' isn't a plugin file name (.esm, .esp or .esl).");
            masters.Add(m);
        }
        foreach (var m in spec.Masters) AddMaster(m);
        var asked = masters.Count;
        foreach (var q in spec.Records.OfType<NewQuest>())
            foreach (var p in q.Scripts.SelectMany(s => s.Properties).Where(p => p.Kind == "object"))
                if (p.Value?.ToString() is string v && ObjectRef.Match(v) is { Success: true } m && !IsSelf(m.Groups["plugin"].Value, spec.Name))
                    AddMaster(m.Groups["plugin"].Value);
        foreach (var r in spec.Records.OfType<NewOutfit>().SelectMany(o => o.Items)
                     .Concat(spec.Records.OfType<NewQuest>().SelectMany(q => q.Aliases).SelectMany(a => new[] { a.Forced, a.Create }).OfType<string>()))
            if (ObjectRef.Match(r) is { Success: true } m && !IsSelf(m.Groups["plugin"].Value, spec.Name))
                AddMaster(m.Groups["plugin"].Value);
        foreach (var npc in spec.Records.OfType<NewFaction>())
            foreach (var (r, _) in npc.Relations)
                if (ObjectRef.Match(r) is { Success: true } m && !IsSelf(m.Groups["plugin"].Value, spec.Name))
                    AddMaster(m.Groups["plugin"].Value);
        foreach (var npc in spec.Records.OfType<NewNpc>())
            foreach (var r in npc.References())
                if (ObjectRef.Match(r) is { Success: true } m && !IsSelf(m.Groups["plugin"].Value, spec.Name))
                    AddMaster(m.Groups["plugin"].Value);
        if (masters.Count > asked) notes.Add($"Added as masters because records or script properties point at them: {string.Join(", ", masters.Skip(asked))}.");
        if (loadOrderIndex != null)
        {
            var sorted = masters.Select((m, i) => (m, i, at: loadOrderIndex(m))).OrderBy(t => t.at ?? int.MaxValue).ThenBy(t => t.i).Select(t => t.m).ToList();
            if (!sorted.SequenceEqual(masters)) notes.Add("Masters put in load order: " + string.Join(", ", sorted) + ".");
            masters = sorted;
        }
        if (masters.Count > 253) throw new ArgumentException("Too many masters (a plugin can have at most 253).");

        // Object IDs: from 0x800 up as the Creation Kit does; a light plugin past 0xFFF wraps to 0x001 and needs a 1.71 header.
        var top = (uint)masters.Count << 24;
        var assigned = new List<(string Type, string EditorId, uint FormId)>();
        uint next = 0x800;
        var needs171 = false;
        var taken = new HashSet<uint>();
        foreach (var r in spec.Records)
        {
            if (r.ObjectId is not uint fixedId) continue;
            if (fixedId is < 0x800 or > 0xFFF) throw new ArgumentException($"{r.EditorId}: a fixed object ID is 0x800 to 0xFFF.");
            if (!taken.Add(fixedId)) throw new ArgumentException($"{r.EditorId}: object ID 0x{fixedId:X3} is used twice.");
        }
        foreach (var r in spec.Records.OrderBy(r => GroupIndex(r.Type)))
        {
            if (r.ObjectId is uint own) { assigned.Add((r.Type, r.EditorId, top | own)); continue; }
            while (taken.Contains(next)) next++;
            if (light && next > 0xFFF) { next = 0x001; needs171 = true; }
            if (light && needs171 && next >= 0x800) throw new ArgumentException($"Too many records for a light plugin ({spec.Records.Count}; at most 4,095).");
            assigned.Add((r.Type, r.EditorId, top | next));
            next++;
        }
        if (needs171) notes.Add("More than 2,048 records: object IDs below 0x800 are used, so the header is 1.71 (needs game version 1.6.1130 or later).");
        if (taken.Count > 0 && !needs171) next = Math.Max(next, taken.Max() + 1);
        var ids = assigned.ToDictionary(a => a.EditorId, a => a.FormId, StringComparer.OrdinalIgnoreCase);

        uint ResolveObject(string text, string where)
        {
            if (ObjectRef.Match(text) is { Success: true } m)
            {
                var plugin = m.Groups["plugin"].Value.Trim();
                var raw = Convert.ToUInt32(m.Groups["id"].Value, 16);
                if (IsSelf(plugin, spec.Name)) return top | (raw & 0xFFFFFF);
                // A light plugin's runtime FormID (FExxxyyy) holds its object ID in the low 12 bits.
                var local = raw >> 24 == 0xFE ? raw & 0xFFF : raw & 0xFFFFFF;
                var index = masters.FindIndex(x => x.Equals(plugin, StringComparison.OrdinalIgnoreCase));
                return ((uint)index << 24) | local;
            }
            if (ids.TryGetValue(text.Trim(), out var own)) return own;
            throw new ArgumentException($"{where}: '{text}' isn't 'Plugin.esm|0x00000014' or the editor ID of a record in this plugin.");
        }

        using var body = new MemoryStream();
        var groups = 0;
        foreach (var group in spec.Records.GroupBy(r => r.Type).OrderBy(g => GroupIndex(g.Key)))
        {
            using var g = new MemoryStream();
            foreach (var r in group)
            {
                var fields = new MemoryStream();
                uint flags = 0;
                switch (r)
                {
                    case NewGlobal glob:
                        if (glob.Constant) flags |= 0x40;
                        Field(fields, "EDID", ZString(glob.EditorId));
                        Field(fields, "FNAM", [(byte)glob.ValueType]);
                        Field(fields, "FLTV", Float(glob.Value));
                        break;
                    case NewQuest q:
                        Field(fields, "EDID", ZString(q.EditorId));
                        if (q.Scripts.Count > 0) Field(fields, "VMAD", Vmad(q, ResolveObject));
                        if (!string.IsNullOrEmpty(q.Name)) Field(fields, "FULL", ZString(q.Name));
                        var dnam = new byte[12];
                        ushort qf = 0;
                        if (q.StartGameEnabled) qf |= 0x0001;
                        if (q.RunOnce) qf |= 0x0100;
                        BinaryPrimitives.WriteUInt16LittleEndian(dnam, qf);
                        dnam[2] = Math.Min(q.Priority, (byte)100);
                        Field(fields, "DNAM", dnam);
                        // Vanilla quests carry the condition-list marker and the next alias ID even when both are empty.
                        Field(fields, "NEXT", []);
                        var nextAlias = new byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(nextAlias, q.Aliases.Count == 0 ? 0 : q.Aliases.Max(a => a.Id) + 1);
                        Field(fields, "ANAM", nextAlias);
                        foreach (var a in q.Aliases) WriteAlias(fields, a, text => ResolveObject(text, $"{q.EditorId}.{a.Name}"));
                        break;
                    case NewColour colour:
                        Field(fields, "EDID", ZString(colour.EditorId));
                        Field(fields, "CNAM", [colour.Red, colour.Green, colour.Blue, 0]);
                        Field(fields, "FNAM", new byte[4]);
                        break;
                    case NewOutfit outfit:
                        Field(fields, "EDID", ZString(outfit.EditorId));
                        var inam = new byte[4 * outfit.Items.Count];
                        for (var k = 0; k < outfit.Items.Count; k++)
                            BinaryPrimitives.WriteUInt32LittleEndian(inam.AsSpan(4 * k), ResolveObject(outfit.Items[k], $"{outfit.EditorId}.items"));
                        Field(fields, "INAM", inam);
                        break;
                    case NewFaction faction:
                        WriteFaction(fields, faction, text => text.Equals("this", StringComparison.OrdinalIgnoreCase) ? ids[faction.EditorId] : ResolveObject(text, $"{faction.EditorId}.relations"));
                        break;
                    case NewNpc npc:
                        WriteNpc(fields, npc, ResolveObject);
                        break;
                    default: throw new ArgumentException($"Record type {r.Type} isn't supported yet.");
                }
                Record(g, r.Type, flags, ids[r.EditorId], fields.ToArray());
            }
            WriteGroup(body, group.Key, g.ToArray());
            groups++;
        }

        var version = needs171 ? 1.71f : 1.70f;
        var hedr = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(hedr, version);
        BinaryPrimitives.WriteInt32LittleEndian(hedr.AsSpan(4), spec.Records.Count + groups);
        BinaryPrimitives.WriteUInt32LittleEndian(hedr.AsSpan(8), next);
        var tes4 = new MemoryStream();
        Field(tes4, "HEDR", hedr);
        Field(tes4, "CNAM", ZString(Limit(spec.Author ?? "SkyLink AI", 511, "author")));
        if (!string.IsNullOrEmpty(spec.Description)) Field(tes4, "SNAM", ZString(Limit(spec.Description, 511, "description")));
        foreach (var m in masters)
        {
            Field(tes4, "MAST", ZString(m));
            Field(tes4, "DATA", new byte[8]);
        }
        uint fileFlags = 0;
        if (ext is ".esm" or ".esl") fileFlags |= 0x1;
        if (light) fileFlags |= 0x200;
        var output = new MemoryStream();
        Record(output, "TES4", fileFlags, 0, tes4.ToArray());
        body.Position = 0;
        body.CopyTo(output);
        return new BuiltPlugin
        {
            Bytes = output.ToArray(), Masters = masters, Assigned = assigned, Version = version, NextObjectId = next,
            NumRecords = spec.Records.Count + groups, Flags = fileFlags, Notes = notes,
        };
    }

    // Aliases as vanilla quests write them (WEJS28 000105D19 and WERJ13 000F5BC1, read 2026-11-22): a forced reference, or one created
    // at another alias, then no voice types and the end marker.
    static void WriteAlias(Stream f, NewAlias a, Func<string, uint> resolve)
    {
        static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
        Field(f, "ALST", U32(a.Id));
        Field(f, "ALID", ZString(a.Name));
        Field(f, "FNAM", U32(0));
        if (a.Forced != null) Field(f, "ALFR", U32(resolve(a.Forced)));
        if (a.Create != null)
        {
            Field(f, "ALCO", U32(resolve(a.Create)));
            Field(f, "ALCA", U32(a.CreateAt));
            Field(f, "ALCL", U32(0));
        }
        Field(f, "VTCK", U32(0));
        Field(f, "ALED", []);
    }

    // Field order and sizes as Skyrim.esm's own factions with ranks (CollegeofWinterholdArchMageFaction, read 2026-11-22).
    static void WriteFaction(Stream f, NewFaction n, Func<string, uint> resolve)
    {
        Field(f, "EDID", ZString(n.EditorId));
        if (!string.IsNullOrEmpty(n.Name)) Field(f, "FULL", ZString(n.Name));
        foreach (var (other, reaction) in n.Relations)
        {
            var xnam = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(xnam, resolve(other));
            BinaryPrimitives.WriteInt32LittleEndian(xnam.AsSpan(8), reaction);
            Field(f, "XNAM", xnam);
        }
        Field(f, "DATA", new byte[4]);
        var crva = new byte[20];
        crva[0] = crva[1] = 1;
        Field(f, "CRVA", crva);
        foreach (var (rank, male, female) in n.Ranks.OrderBy(r => r.Rank))
        {
            var rnam = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(rnam, rank);
            Field(f, "RNAM", rnam);
            if (!string.IsNullOrEmpty(male)) Field(f, "MNAM", ZString(male));
            if (!string.IsNullOrEmpty(female)) Field(f, "FNAM", ZString(female));
        }
        Field(f, "VENV", new byte[12]);
    }

    // Field order and sizes as Skyrim.esm's own NPC records have them (Northwatch Prisoner, 000C1AA3, read 2026-11-22).
    static void WriteNpc(Stream f, NewNpc n, Func<string, string, uint> resolve)
    {
        uint Ref(string text, string what) => resolve(text, $"{n.EditorId}.{what}");
        static byte[] Id(uint id) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, id); return b; }

        Field(f, "EDID", ZString(n.EditorId));
        Field(f, "OBND", new byte[12]);

        var acbs = new byte[24];
        uint flags = 0x10;                       // auto-calculated stats
        if (n.Female) flags |= 0x1;
        if (n.Essential) flags |= 0x2;
        if (n.Unique) flags |= 0x20;
        if (n.Protected) flags |= 0x800;
        if (n.LevelsWithPlayer) flags |= 0x80;
        BinaryPrimitives.WriteUInt32LittleEndian(acbs, flags);
        BinaryPrimitives.WriteUInt16LittleEndian(acbs.AsSpan(8), n.LevelsWithPlayer ? (ushort)1000 : Math.Max(n.Level, (ushort)1));
        if (n.LevelsWithPlayer) BinaryPrimitives.WriteUInt16LittleEndian(acbs.AsSpan(10), Math.Max(n.Level, (ushort)1));   // never below this level
        BinaryPrimitives.WriteUInt16LittleEndian(acbs.AsSpan(14), 100);   // speed
        BinaryPrimitives.WriteUInt16LittleEndian(acbs.AsSpan(16), 35);    // disposition base
        Field(f, "ACBS", acbs);

        foreach (var (faction, rank) in n.Factions)
        {
            var snam = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(snam, Ref(faction, "factions"));
            snam[4] = unchecked((byte)rank);
            Field(f, "SNAM", snam);
        }
        if (!string.IsNullOrWhiteSpace(n.Voice)) Field(f, "VTCK", Id(Ref(n.Voice, "voice")));
        Field(f, "RNAM", Id(Ref(n.Race, "race")));
        if (!string.IsNullOrWhiteSpace(n.Skin)) Field(f, "WNAM", Id(Ref(n.Skin, "skin")));

        var aidt = new byte[20];
        aidt[0] = n.Aggression;
        aidt[1] = n.Confidence;
        aidt[5] = n.Assistance;
        aidt[2] = 50;   // energy
        aidt[3] = 3;    // morality: no crime
        Field(f, "AIDT", aidt);
        foreach (var package in n.Packages) Field(f, "PKID", Id(Ref(package, "packages")));
        if (!string.IsNullOrWhiteSpace(n.Class)) Field(f, "CNAM", Id(Ref(n.Class, "class")));
        if (!string.IsNullOrEmpty(n.Name)) Field(f, "FULL", ZString(n.Name));
        Field(f, "DATA", []);

        var dnam = new byte[52];
        for (var i = 0; i < 18; i++) dnam[i] = 15;                                    // skill base
        BinaryPrimitives.WriteUInt16LittleEndian(dnam.AsSpan(36), 50);                // calculated health, magicka, stamina
        BinaryPrimitives.WriteUInt16LittleEndian(dnam.AsSpan(38), 50);
        BinaryPrimitives.WriteUInt16LittleEndian(dnam.AsSpan(40), 50);
        Field(f, "DNAM", dnam);

        foreach (var part in n.HeadParts) Field(f, "PNAM", Id(Ref(part, "headParts")));
        if (!string.IsNullOrWhiteSpace(n.HairColour)) Field(f, "HCLF", Id(Ref(n.HairColour, "hairColour")));
        if (!string.IsNullOrWhiteSpace(n.CombatStyle)) Field(f, "ZNAM", Id(Ref(n.CombatStyle, "combatStyle")));
        Field(f, "NAM5", [0xFF, 0x00]);
        Field(f, "NAM6", Float(n.Height));
        Field(f, "NAM7", Float(n.Weight));
        Field(f, "NAM8", Id(1));                                                      // sound level: normal
        if (!string.IsNullOrWhiteSpace(n.Outfit)) Field(f, "DOFT", Id(Ref(n.Outfit, "outfit")));
        if (!string.IsNullOrWhiteSpace(n.HeadTexture)) Field(f, "FTST", Id(Ref(n.HeadTexture, "headTexture")));
        var (qr, qg, qb) = n.BodyTint is { } t ? (t.R / 255f, t.G / 255f, t.B / 255f) : (0.5f, 0.5f, 0.5f);
        Field(f, "QNAM", [.. Float(qr), .. Float(qg), .. Float(qb)]);
    }

    static bool IsSelf(string plugin, string self) =>
        plugin.Trim().Equals(self, StringComparison.OrdinalIgnoreCase) || plugin.Trim().Equals("this", StringComparison.OrdinalIgnoreCase);

    static int GroupIndex(string type) { var i = Array.IndexOf(GroupOrder, type); return i < 0 ? int.MaxValue : i; }

    static string Limit(string s, int max, string what) =>
        Text.GetByteCount(s) <= max ? s : throw new ArgumentException($"The {what} is too long ({max} characters at most).");

    // VMAD version 5, object format 2: the layout the game's own records use (seen in Skyrim.esm). A quest without stage
    // fragments or alias scripts ends after the scripts, as vanilla ones do.
    static byte[] Vmad(NewQuest q, Func<string, string, uint> resolve)
    {
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write((short)5);
        w.Write((short)2);
        w.Write(checked((ushort)q.Scripts.Count));
        foreach (var s in q.Scripts)
        {
            WString(w, s.Name, "script name");
            w.Write((byte)0);   // local script
            w.Write(checked((ushort)s.Properties.Count));
            foreach (var p in s.Properties)
            {
                var where = $"{s.Name}.{p.Name}";
                WString(w, p.Name, "property name");
                switch (p.Kind)
                {
                    case "object":
                        w.Write((byte)1); w.Write((byte)1);
                        w.Write((ushort)0); w.Write((short)-1);
                        w.Write(p.Value == null ? 0u : resolve(p.Value.ToString(), where));
                        break;
                    case "string":
                        w.Write((byte)2); w.Write((byte)1);
                        WString(w, p.Value?.ToString() ?? "", where);
                        break;
                    case "int":
                        w.Write((byte)3); w.Write((byte)1);
                        w.Write(Number(p.Value, where) is var d && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue ? (int)d
                            : throw new ArgumentException($"{where}: an int property needs a whole number."));
                        break;
                    case "float":
                        w.Write((byte)4); w.Write((byte)1);
                        w.Write((float)Number(p.Value, where));
                        break;
                    case "bool":
                        w.Write((byte)5); w.Write((byte)1);
                        w.Write((byte)(Bool(p.Value, where) ? 1 : 0));
                        break;
                    default: throw new ArgumentException($"{where}: property type '{p.Kind}' isn't supported (int, float, bool, string, object).");
                }
            }
        }
        w.Flush();
        return o.ToArray();
    }

    static double Number(JsonNode? v, string where)
    {
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<double>(out var d)) return d;
            if (jv.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
        }
        throw new ArgumentException($"{where}: needs a number.");
    }

    static bool Bool(JsonNode? v, string where)
    {
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out var b)) return b;
            if (jv.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return b;
            if (jv.TryGetValue<double>(out var d)) return d != 0;
        }
        throw new ArgumentException($"{where}: needs true or false.");
    }

    static void WString(BinaryWriter w, string s, string where)
    {
        var b = Text.GetBytes(s);
        if (b.Length > ushort.MaxValue) throw new ArgumentException($"{where} is too long.");
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    static byte[] ZString(string s) => [.. Text.GetBytes(s), 0];
    static byte[] Float(float f) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, f); return b; }

    static void Field(Stream o, string type, byte[] data)
    {
        Span<byte> h = stackalloc byte[6];
        if (data.Length > ushort.MaxValue)
        {
            Encoding.ASCII.GetBytes("XXXX", h);
            BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 4);
            o.Write(h);
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
            o.Write(size);
            Encoding.ASCII.GetBytes(type, h);
            BinaryPrimitives.WriteUInt16LittleEndian(h[4..], 0);
        }
        else
        {
            Encoding.ASCII.GetBytes(type, h);
            BinaryPrimitives.WriteUInt16LittleEndian(h[4..], (ushort)data.Length);
        }
        o.Write(h);
        o.Write(data);
    }

    static void Record(Stream o, string type, uint flags, uint formId, byte[] data)
    {
        Span<byte> h = stackalloc byte[24];
        h.Clear();
        Encoding.ASCII.GetBytes(type, h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], formId);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], FormVersion);
        o.Write(h);
        o.Write(data);
    }

    static void WriteGroup(Stream o, string label, byte[] content)
    {
        Span<byte> h = stackalloc byte[24];
        h.Clear();
        Encoding.ASCII.GetBytes("GRUP", h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(content.Length + 24));
        Encoding.ASCII.GetBytes(label, h[8..]);
        o.Write(h);   // group type 0 (top), timestamp and version control 0
        o.Write(content);
    }

    /// <summary>
    /// Parses the records argument: a JSON array (or {"records": [...]}) of objects with "type" (GLOB, QUST or NPC_; "record" also
    /// accepted) and "editorId". Throws ArgumentException naming the bad entry.
    /// </summary>
    public static List<NewRecord> ParseRecords(JsonNode? json)
    {
        var array = json as JsonArray ?? json?["records"] as JsonArray ?? throw new ArgumentException("records must be a JSON array of record objects.");
        var list = new List<NewRecord>();
        var i = 0;
        foreach (var node in array)
        {
            i++;
            if (node is not JsonObject o) throw new ArgumentException($"Record {i} isn't an object.");
            string? Str(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[k]?.ToString();
            bool Flag(string k) => o[k] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out b) && b);
            var kind = (Str("record") ?? Str("type") ?? "").Trim().ToUpperInvariant();
            var edid = Str("editorId") ?? Str("edid") ?? throw new ArgumentException($"Record {i} has no editorId.");
            var where = $"Record {i} ({edid})";
            uint? objectId = null;
            if (Str("objectId") is { Length: > 0 } oid)
            {
                var hex = oid.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? oid[2..] : oid;
                objectId = uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var parsedId) ? parsedId : throw new ArgumentException($"{where}: objectId '{oid}' isn't a hexadecimal number.");
            }
            switch (kind)
            {
                case "GLOB" or "GLOBAL":
                    // "type" names the value type when "record" names the record (both spellings are natural to write).
                    var vt = (Str("valueType") ?? Str("globalType") ?? (Str("record") != null ? Str("type") : null) ?? "float").Trim().ToLowerInvariant();
                    var c = vt switch { "short" or "s" => 's', "long" or "l" or "int" => 'l', "float" or "f" => 'f', _ => throw new ArgumentException($"{where}: valueType must be short, long or float.") };
                    var value = o["value"] == null ? 0 : Number(o["value"], where);
                    if (c == 's' && (value < short.MinValue || value > short.MaxValue)) throw new ArgumentException($"{where}: a short holds -32768 to 32767.");
                    if (c != 'f') value = Math.Round(value);
                    if (c == 'l' && Math.Abs(value) > 16_777_216) throw new ArgumentException($"{where}: globals are stored as floats, so a long past 16,777,216 loses precision.");
                    list.Add(new NewGlobal { EditorId = edid, ValueType = c, Value = (float)value, Constant = Flag("constant") });
                    break;
                case "QUST" or "QUEST":
                    var scripts = new List<NewScript>();
                    foreach (var sn in o["scripts"] as JsonArray ?? [])
                    {
                        if (sn is JsonValue sv && sv.TryGetValue<string>(out var bare)) { scripts.Add(new NewScript(bare, [])); continue; }
                        if (sn is not JsonObject so || (so["name"] ?? so["script"])?.ToString() is not { Length: > 0 } sname)
                            throw new ArgumentException($"{where}: each script needs a name.");
                        var props = new List<NewProperty>();
                        foreach (var pn in so["properties"] as JsonArray ?? [])
                        {
                            if (pn is not JsonObject po || po["name"]?.ToString() is not { Length: > 0 } pname) throw new ArgumentException($"{where}, {sname}: each property needs a name.");
                            var ptype = (po["type"]?.ToString() ?? "").Trim().ToLowerInvariant();
                            ptype = ptype switch { "integer" => "int", "boolean" => "bool", "form" or "objectreference" => "object", _ => ptype };
                            props.Add(new NewProperty(pname, ptype, po["value"]?.DeepClone()));
                        }
                        scripts.Add(new NewScript(sname, props));
                    }
                    var priority = o["priority"] == null ? 0 : Number(o["priority"], where);
                    if (priority is < 0 or > 100) throw new ArgumentException($"{where}: priority is 0 to 100.");
                    list.Add(new NewQuest
                    {
                        EditorId = edid, Name = Str("name"), StartGameEnabled = Flag("startGameEnabled"), RunOnce = Flag("runOnce"),
                        Priority = (byte)priority, Scripts = scripts,
                    });
                    break;
                case "NPC_" or "NPC":
                    List<string> Refs(string k) => (o[k] as JsonArray ?? []).Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
                    list.Add(new NewNpc
                    {
                        EditorId = edid, ObjectId = objectId, Name = Str("name"), Female = Flag("female"), Essential = Flag("essential"), Protected = Flag("protected"),
                        Unique = o["unique"] == null || Flag("unique"),
                        Race = Str("race") ?? throw new ArgumentException($"{where}: an NPC needs a race (\"Skyrim.esm|0x00013746\")."),
                        Voice = Str("voice"), Class = Str("class"), Outfit = Str("outfit"), CombatStyle = Str("combatStyle"),
                        Packages = Refs("packages"), HeadParts = Refs("headParts"),
                        Factions = (o["factions"] as JsonArray ?? []).Select(x => x is JsonObject fo
                            ? (fo["faction"]?.ToString() ?? throw new ArgumentException($"{where}: each faction needs a \"faction\"."), (sbyte)(fo["rank"] == null ? 0 : Number(fo["rank"], where)))
                            : (x?.ToString() ?? "", (sbyte)0)).ToList(),
                        Level = (ushort)(o["level"] == null ? 1 : Math.Clamp(Number(o["level"], where), 1, 1000)),
                        Height = o["height"] == null ? 1f : (float)Number(o["height"], where),
                        Weight = o["weight"] == null ? 50f : (float)Number(o["weight"], where),
                    });
                    break;
                case "FACT" or "FACTION":
                    var ranks = new List<(int, string?, string?)>();
                    foreach (var rn in o["ranks"] as JsonArray ?? [])
                    {
                        if (rn is not JsonObject ro || ro["rank"] == null) throw new ArgumentException($"{where}: each rank needs a number (\"rank\").");
                        var number = (int)Number(ro["rank"], where);
                        if (number is < 0 or > 127) throw new ArgumentException($"{where}: ranks are 0 to 127.");
                        ranks.Add((number, ro["male"]?.ToString(), ro["female"]?.ToString()));
                    }
                    if (ranks.Select(r => r.Item1).Distinct().Count() != ranks.Count) throw new ArgumentException($"{where}: a rank number is used twice.");
                    list.Add(new NewFaction
                    {
                        EditorId = edid, ObjectId = objectId, Name = Str("name"), Ranks = ranks,
                        Relations = (o["relations"] as JsonArray ?? []).OfType<JsonObject>().Select(r => (r["faction"]?.ToString()
                            ?? throw new ArgumentException($"{where}: each relation needs a \"faction\"."), (int)(r["reaction"] == null ? 2 : Number(r["reaction"], where)))).ToList(),
                    });
                    break;
                default: throw new ArgumentException($"{where}: type '{kind}' isn't supported yet (GLOB, QUST, NPC_, FACT).");
            }
        }
        return list;
    }
}
