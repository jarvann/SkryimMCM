using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace SkyrimMCP.Plugins;

/// <summary>
/// The player's own LOOT masterlist (FEAT-286): per plugin, what the LOOT community records about it: requirements,
/// incompatibilities, messages, dirty edits (by the file's CRC) and links. Read from LOOT's data folder at runtime and never
/// shipped with SkyLink (the masterlist has its own licence). Message conditions are evaluated where they only test which
/// plugins and files exist (active, file, many, is_master, not/and/or); conditions on versions, checksums or the game
/// executable are left as "might apply" with the condition shown, rather than guessed.
/// </summary>
public sealed class LootMasterlist
{
    public sealed record Entry(string Name, Regex? Pattern, YamlMappingNode Node);

    public required string Path { get; init; }
    public required DateTime Updated { get; init; }
    public required Dictionary<string, Entry> Exact { get; init; }
    public required List<Entry> Patterns { get; init; }

    static (string path, DateTime stamp, LootMasterlist list)? _cache;
    static readonly object CacheLock = new();

    /// <summary>The masterlist LOOT keeps for Skyrim SE (or its GOG/VR variants' folders when those exist).</summary>
    public static string? FindFile()
    {
        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LOOT", "games");
        foreach (var game in new[] { "Skyrim Special Edition", "Skyrim Special Edition (GOG)", "Skyrim Special Edition GOG" })
        {
            var f = System.IO.Path.Combine(root, game, "masterlist.yaml");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    public static LootMasterlist? Load()
    {
        var file = FindFile();
        if (file == null) return null;
        var stamp = File.GetLastWriteTimeUtc(file);
        lock (CacheLock)
        {
            if (_cache is { } c && c.path == file && c.stamp == stamp) return c.list;
            var yaml = new YamlStream();
            using (var reader = new StreamReader(file)) yaml.Load(reader);
            var root = (YamlMappingNode)yaml.Documents[0].RootNode;
            var exact = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var patterns = new List<Entry>();
            if (root.Children.TryGetValue(new YamlScalarNode("plugins"), out var plugins) && plugins is YamlSequenceNode seq)
            {
                foreach (var item in seq.OfType<YamlMappingNode>())
                {
                    var name = Scalar(item, "name");
                    if (name == null) continue;
                    // LOOT treats a name containing any of : \ * ? | as a regular expression over the whole file name.
                    if (name.IndexOfAny([':', '\\', '*', '?', '|']) >= 0)
                    {
                        try { patterns.Add(new Entry(name, new Regex("^(?:" + name + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), item)); }
                        catch (ArgumentException) { }
                    }
                    else exact.TryAdd(name, new Entry(name, null, item));
                }
            }
            var list = new LootMasterlist { Path = file, Updated = File.GetLastWriteTime(file), Exact = exact, Patterns = patterns };
            _cache = (file, stamp, list);
            return list;
        }
    }

    public IEnumerable<Entry> EntriesFor(string plugin)
    {
        if (Exact.TryGetValue(plugin, out var e)) yield return e;
        foreach (var p in Patterns) if (p.Pattern!.IsMatch(plugin)) yield return p;
    }

    public object Age()
    {
        var days = (int)(DateTime.Now - Updated).TotalDays;
        return new
        {
            path = Path,
            updated = Updated.ToString("yyyy-MM-dd"),
            ageDays = days,
            note = days > 60 ? $"The masterlist is {days} days old: open LOOT once to update it, so new mods and fixes are known." : null,
        };
    }

    // ---- reading one plugin's notes ----

    public sealed record Note(string Kind, string Text, string? Condition, bool? Applies);

    /// <summary>Everything the masterlist says about a plugin that applies (or might) in this load order.</summary>
    public object NotesFor(string plugin, LoadOrderState lo, bool checkDirty)
    {
        var entries = EntriesFor(plugin).ToList();
        var requirements = new List<object>();
        var incompatible = new List<object>();
        var messages = new List<Note>();
        var dirty = new List<object>();
        var urls = new List<string>();
        uint? crc = null;
        foreach (var e in entries)
        {
            foreach (var (name, display, cond) in FileRefs(e.Node, "req"))
            {
                if (Eval(cond, lo) == false) continue;
                var present = Present(name, lo);
                requirements.Add(new { name = display ?? name, file = name, present });
            }
            foreach (var (name, display, cond) in FileRefs(e.Node, "inc"))
            {
                if (Eval(cond, lo) == false) continue;
                var active = Active(name, lo);
                incompatible.Add(new { name = display ?? name, file = name, active });
            }
            foreach (var m in Children(e.Node, "msg").OfType<YamlMappingNode>())
            {
                var cond = Scalar(Merged(m), "condition");
                var applies = Eval(cond, lo);
                if (applies == false) continue;
                messages.Add(new Note(Scalar(Merged(m), "type") ?? "say", MessageText(Merged(m)), applies == null ? cond : null, applies));
            }
            foreach (var u in Children(e.Node, "url"))
            {
                if (u is YamlScalarNode s && s.Value != null) urls.Add(s.Value);
                else if (u is YamlMappingNode um && Scalar(um, "link") is string link) urls.Add(link);
            }
            var dirtyEntries = Children(e.Node, "dirty").OfType<YamlMappingNode>().Select(Merged).ToList();
            if (checkDirty && (dirtyEntries.Count > 0 || Children(e.Node, "clean").Any()) && lo.FileOf(plugin) is string path)
            {
                crc ??= Crc32Of(path);
                foreach (var d in dirtyEntries)
                {
                    if (!TryHex(Scalar(d, "crc"), out var want) || want != crc) continue;
                    static int? Num(string? s) => int.TryParse(s, out var n) ? n : null;
                    dirty.Add(new
                    {
                        itm = Num(Scalar(d, "itm")), deletedReferences = Num(Scalar(d, "udr")), deletedNavmeshes = Num(Scalar(d, "nav")),
                        cleanWith = Scalar(d, "util"), note = MessageText(d),
                    });
                }
            }
        }
        return new
        {
            plugin,
            known = entries.Count > 0,
            requirements = requirements.Count > 0 ? requirements : null,
            missingRequirements = requirements.Count(r => r.GetType().GetProperty("present")!.GetValue(r) is false),
            incompatibilities = incompatible.Count > 0 ? incompatible : null,
            activeIncompatibilities = incompatible.Count(r => r.GetType().GetProperty("active")!.GetValue(r) is true),
            messages = messages.Count > 0 ? messages.Select(m => new { type = m.Kind, text = m.Text, condition = m.Condition, applies = m.Applies }) : null,
            dirty = dirty.Count > 0 ? dirty : null,
            // Round A: Update.esm came back "known" with nothing about cleaning: say whether LOOT knows dirty versions at all.
            dirtyStatus = !checkDirty ? null
                : dirty.Count > 0 ? "dirty (this exact file, by checksum)"
                : entries.Any(e => Children(e.Node, "dirty").Any()) ? $"LOOT lists dirty versions of this plugin, but not this file (checksum {crc:X8}): it may be a newer or older version than the masterlist knows, or already cleaned"
                : entries.Any(e => Children(e.Node, "clean").Any()) ? "LOOT knows it as clean"
                : "LOOT has no cleaning information for it",
            links = urls.Count > 0 ? urls.Distinct().Take(4) : null,
        };
    }

    /// <summary>FEAT-288: what LOOT knows about cleaning this exact file (matched by CRC).</summary>
    public sealed record Cleaning(uint Crc, bool Dirty, bool KnownClean, int? Itm, int? Udr, int? Nav, IReadOnlyList<uint> CleanCrcs);

    public Cleaning CleaningFor(string plugin, string path, uint? crc = null)
    {
        var actual = crc ?? Crc32Of(path);
        int? itm = null, udr = null, nav = null;
        var dirty = false;
        var cleanCrcs = new List<uint>();
        foreach (var e in EntriesFor(plugin))
        {
            foreach (var d in Children(e.Node, "dirty").OfType<YamlMappingNode>().Select(Merged))
            {
                if (!TryHex(Scalar(d, "crc"), out var c) || c != actual) continue;
                dirty = true;
                itm = int.TryParse(Scalar(d, "itm"), out var i) ? i : null;
                udr = int.TryParse(Scalar(d, "udr"), out var u) ? u : null;
                nav = int.TryParse(Scalar(d, "nav"), out var n) ? n : null;
            }
            foreach (var c in Children(e.Node, "clean").OfType<YamlMappingNode>())
                if (TryHex(Scalar(c, "crc"), out var cc)) cleanCrcs.Add(cc);
        }
        return new Cleaning(actual, dirty, cleanCrcs.Contains(actual), itm, udr, nav, cleanCrcs);
    }

    // ---- small YAML helpers ----

    static string? Scalar(YamlMappingNode m, string key) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;

    static IEnumerable<YamlNode> Children(YamlMappingNode m, string key)
    {
        if (!m.Children.TryGetValue(new YamlScalarNode(key), out var v)) return [];
        return v is YamlSequenceNode seq ? seq.Children : [v];
    }

    /// <summary>YAML merge keys (`&lt;&lt;: *anchor`) aren't applied by the representation model: apply them here.</summary>
    static YamlMappingNode Merged(YamlMappingNode m)
    {
        if (!m.Children.TryGetValue(new YamlScalarNode("<<"), out var b) || b is not YamlMappingNode baseMap) return m;
        var result = new YamlMappingNode();
        foreach (var kv in Merged(baseMap).Children) result.Children[kv.Key] = kv.Value;
        foreach (var kv in m.Children) if (kv.Key is YamlScalarNode k && k.Value != "<<") result.Children[kv.Key] = kv.Value;
        return result;
    }

    static IEnumerable<(string name, string? display, string? condition)> FileRefs(YamlMappingNode m, string key)
    {
        foreach (var n in Children(m, key))
        {
            if (n is YamlScalarNode s && s.Value != null) yield return (s.Value, null, null);
            else if (n is YamlMappingNode f && Scalar(f, "name") is string name) yield return (name, Scalar(f, "display"), Scalar(f, "condition"));
        }
    }

    /// <summary>A message's English text with its {0} substitutions filled; markdown links kept as they are.</summary>
    static string MessageText(YamlMappingNode m)
    {
        string text = "";
        if (m.Children.TryGetValue(new YamlScalarNode("content"), out var c))
        {
            if (c is YamlScalarNode s) text = s.Value ?? "";
            else if (c is YamlSequenceNode langs)
            {
                var en = langs.OfType<YamlMappingNode>().FirstOrDefault(l => Scalar(l, "lang") is "en" or null) ?? langs.OfType<YamlMappingNode>().FirstOrDefault();
                text = en != null ? Scalar(en, "text") ?? "" : "";
            }
        }
        var subs = Children(m, "subs").OfType<YamlScalarNode>().Select(x => x.Value ?? "").ToList();
        for (var i = 0; i < subs.Count; i++) text = text.Replace("{" + i + "}", subs[i]);
        return text;
    }

    static bool TryHex(string? s, out uint value)
    {
        value = 0;
        if (s == null) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    // ---- the parts of LOOT's condition language that only ask what is installed ----

    // "../X" names a file in the game folder (round A: VR-only requirements guarded by file("../SkyrimVR.exe") counted as missing on SE).
    static string? GameFile(string name) =>
        name.StartsWith("../") && SkyLinkSettings.SkyrimPath().Path is string game ? System.IO.Path.Combine(game, name[3..].Replace('/', '\\')) : null;

    static bool Present(string name, LoadOrderState lo) =>
        GameFile(name) is string g ? File.Exists(g)
        : lo.FileOf(name) != null || (SkyLinkSettings.DataPath() is string data && File.Exists(System.IO.Path.Combine(data, name)));

    static bool Active(string name, LoadOrderState lo) => lo.IndexOf(name) != null;

    /// <summary>true, false, or null when the condition uses something not evaluated here.</summary>
    public static bool? Eval(string? condition, LoadOrderState lo)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        try
        {
            var p = new CondParser(condition, lo);
            var v = p.Or();
            p.SkipSpace();
            return p.AtEnd ? v : null;
        }
        catch (FormatException) { return null; }
    }

    sealed class CondParser(string s, LoadOrderState lo)
    {
        int _i;
        public bool AtEnd => _i >= s.Length;
        public void SkipSpace() { while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++; }
        bool Word(string w)
        {
            SkipSpace();
            if (string.Compare(s, _i, w, 0, w.Length, StringComparison.Ordinal) != 0) return false;
            var end = _i + w.Length;
            if (end < s.Length && char.IsLetter(s[end]) && w.All(char.IsLetter)) return false;
            _i = end;
            return true;
        }
        // Three-valued: null is "unknown", which stays unknown unless the other side settles the answer.
        public bool? Or()
        {
            var v = And();
            while (Word("or")) { var r = And(); v = v == true || r == true ? true : v == null || r == null ? null : false; }
            return v;
        }
        bool? And()
        {
            var v = Not();
            while (Word("and")) { var r = Not(); v = v == false || r == false ? false : v == null || r == null ? null : true; }
            return v;
        }
        bool? Not() => Word("not") ? Not() is bool b ? !b : null : Atom();
        bool? Atom()
        {
            SkipSpace();
            if (Word("(")) { var v = Or(); if (!Word(")")) throw new FormatException(); return v; }
            var m = Regex.Match(s[_i..], @"^([a-z_]+)\(");
            if (!m.Success) throw new FormatException();
            var fn = m.Groups[1].Value;
            _i += m.Length;
            var args = new List<string>();
            while (true)
            {
                SkipSpace();
                if (Word(")")) break;
                if (_i < s.Length && s[_i] == '"')
                {
                    var end = s.IndexOf('"', _i + 1);
                    if (end < 0) throw new FormatException();
                    args.Add(s[(_i + 1)..end]);
                    _i = end + 1;
                }
                else
                {
                    var end = _i;
                    while (end < s.Length && s[end] != ',' && s[end] != ')') end++;
                    args.Add(s[_i..end].Trim());
                    _i = end;
                }
                SkipSpace();
                if (_i < s.Length && s[_i] == ',') _i++;
            }
            if (args.Count == 0) return null;
            var a = args[0];
            bool IsRegex(string x) => x.IndexOfAny([':', '\\', '*', '?', '|']) >= 0 && !x.StartsWith("../");
            IEnumerable<string> Matching(IEnumerable<string> names) => IsRegex(a)
                ? names.Where(n => Regex.IsMatch(n, "^(?:" + a + ")$", RegexOptions.IgnoreCase))
                : names.Where(n => n.Equals(a, StringComparison.OrdinalIgnoreCase));
            return fn switch
            {
                "active" => Matching(lo.Active).Any(),
                "file" or "readable" when a.StartsWith("../") => GameFile(a) is string g ? File.Exists(g) : null,
                "product_version" when a.StartsWith("../") && args.Count >= 3 => ProductVersion(a, args[1], args[2]),
                "file" => IsRegex(a) ? Matching(lo.Files.Keys).Any() : Present(a, lo),
                "many_active" => Matching(lo.Active).Skip(1).Any(),
                "many" => Matching(lo.Files.Keys).Skip(1).Any(),
                "is_master" => lo.FileOf(a) is string f ? PluginFile.ReadHeader(f).IsMaster : false,
                _ => null,   // version, checksum, readable, product_version, ...: not evaluated
            };
        }
    }

    static bool? ProductVersion(string file, string wanted, string op)
    {
        if (GameFile(file) is not string g || !File.Exists(g)) return false;
        var have = System.Diagnostics.FileVersionInfo.GetVersionInfo(g).ProductVersion;
        if (!Version.TryParse(have?.Split(' ')[0], out var h) || !Version.TryParse(wanted, out var w)) return null;
        var c = h.CompareTo(w);
        return op.Trim() switch { ">=" => c >= 0, ">" => c > 0, "<=" => c <= 0, "<" => c < 0, "==" => c == 0, "!=" => c != 0, _ => null };
    }

    // ---- CRC-32 (the zlib/ISO polynomial LOOT and xEdit report) ----

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Crc32Of(string path)
    {
        var crc = 0xFFFFFFFFu;
        var buffer = new byte[1 << 20];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < n; i++) crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
