namespace SkyrimMCP.Saves;

// FEAT-269: a save against the current load order, for collection updates: which of the save's plugins are gone, moved, changed
// between full and light, or new, and how much of the save each one owns (change forms, script instances, threads, queued
// calls). The engine re-maps forms by plugin name on load, so reordering alone is safe; what breaks is a removed plugin with a
// footprint, or one that changed type (an ESP compacted into an ESL renumbers its forms).
public static class MigrationReport
{
    public sealed record CurrentPlugin(string Name, bool? Light);

    static readonly string[] Implicit = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    /// <summary>Vortex/MO2-style Plugins.txt (active lines start with '*') plus the base game and Creation Club files.</summary>
    public static List<CurrentPlugin> FromPluginsTxt(string pluginsTxt, string? gameFolder)
    {
        var list = Implicit.Select(n => new CurrentPlugin(n, false)).ToList();
        if (gameFolder != null && File.Exists(Path.Combine(gameFolder, "Skyrim.ccc")))
            list.AddRange(File.ReadAllLines(Path.Combine(gameFolder, "Skyrim.ccc")).Where(l => l.Trim().Length > 0)
                .Select(l => new CurrentPlugin(l.Trim(), null)));
        foreach (var line in File.ReadAllLines(pluginsTxt))
        {
            var l = line.Trim();
            if (l.StartsWith('*')) list.Add(new CurrentPlugin(l[1..].Trim(), null));
        }
        return list.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>The BSA owners in load order, for ScriptFiles.Build (later archives win).</summary>
    public static List<string> ArchiveOrder(List<CurrentPlugin> current) => current.Select(p => Path.GetFileNameWithoutExtension(p.Name)).ToList();

    /// <summary>
    /// Scripts whose installed version has different variables than the save recorded: removed (their saved values are dropped),
    /// retyped (the riskiest) or added (start at their defaults: usually harmless). Only scripts with instances in the save.
    /// </summary>
    public static object CompareScripts(SaveScan scan, ScriptFiles.Index index)
    {
        var changed = new List<(string script, int instances, List<string> removed, List<string> retyped, List<string> added)>();
        var missing = new List<(string script, int instances)>();
        var compared = 0;
        foreach (var (script, members) in scan.ScriptMembers ?? [])
        {
            var instances = scan.InstancesByScript.GetValueOrDefault(script);
            if (instances == 0) continue;
            List<ScriptFiles.Variable>? vars;
            try { vars = ScriptFiles.ReadVariables(index, script); } catch (Exception ex) when (ex is IOException or InvalidDataException) { vars = null; }
            if (vars == null) { missing.Add((script, instances)); continue; }
            compared++;
            var saved = members.GroupBy(m => m[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);
            var now = vars.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Type, StringComparer.OrdinalIgnoreCase);
            var removed = saved.Keys.Where(k => !now.ContainsKey(k)).ToList();
            var added = now.Keys.Where(k => !saved.ContainsKey(k)).ToList();
            var retyped = saved.Where(kv => now.TryGetValue(kv.Key, out var t) && !t.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))
                .Select(kv => $"{kv.Key}: {kv.Value} -> {now[kv.Key]}").ToList();
            if (removed.Count + added.Count + retyped.Count > 0) changed.Add((script, instances, removed, retyped, added));
        }
        static string Clean(string v) => v.StartsWith("::") && v.EndsWith("_var") ? v[2..^4] : v;   // a property's hidden variable
        var risky = changed.Where(c => c.removed.Count + c.retyped.Count > 0).OrderByDescending(c => c.retyped.Count).ThenByDescending(c => c.instances).ToList();
        var onlyAdded = changed.Where(c => c.removed.Count + c.retyped.Count == 0).ToList();
        return new
        {
            compared,
            changedRisky = risky.Count,
            changedOnlyAdded = onlyAdded.Count,
            scriptFileMissing = missing.Count,
            changed = risky.Take(40).Select(c => new
            {
                c.script, c.instances,
                removed = c.removed.Select(Clean).Take(10), retyped = c.retyped.Take(10).Select(Clean), added = c.added.Select(Clean).Take(10),
            }),
            onlyAddedExamples = onlyAdded.OrderByDescending(c => c.instances).Take(10).Select(c => c.script),
            missingExamples = missing.OrderByDescending(m => m.instances).Take(15).Select(m => $"{m.script} ({m.instances})"),
            note = "Retyped variables are the riskiest (the saved value no longer fits); removed ones lose their saved value, which " +
                   "breaks a mod that relied on it; added ones start at their defaults, usually fine. Scripts with no file are " +
                   "leftovers of removed mods (CleanSave levels='safe' after loading). Scripts from MO2 mod folders aren't searched.",
        };
    }

    public static object Build(SaveScan scan, List<CurrentPlugin> current, string currentSource, object? scripts = null)
    {
        var now = current.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var savedFull = scan.Plugins.Select((n, i) => (n, i, light: false));
        var savedLight = scan.LightPlugins.Select((n, i) => (n, i, light: true));
        var saved = savedFull.Concat(savedLight).ToList();
        var savedNames = new HashSet<string>(saved.Select(x => x.n), StringComparer.OrdinalIgnoreCase);

        int Get(Dictionary<string, int> d, string k) => d.TryGetValue(k, out var v) ? v : 0;
        int ModCount(List<ModCount> l, string k) => l.FirstOrDefault(x => x.Plugin.Equals(k, StringComparison.OrdinalIgnoreCase))?.Count ?? 0;
        object Footprint(string n) => new
        {
            changeForms = Get(scan.ChangeFormsByPlugin, n),
            scriptInstances = Get(scan.InstancesByPlugin, n),
            threads = ModCount(scan.ThreadsByPlugin, n),
            queuedCalls = ModCount(scan.QueuedByPlugin, n),
        };

        var removed = saved.Where(x => !now.ContainsKey(x.n)).Select(x =>
        {
            var cf = Get(scan.ChangeFormsByPlugin, x.n);
            var inst = Get(scan.InstancesByPlugin, x.n);
            var running = ModCount(scan.ThreadsByPlugin, x.n) + ModCount(scan.QueuedByPlugin, x.n);
            var risk = running > 0 || inst > 50 || cf > 500 ? "high" : inst > 0 || cf > 0 ? "medium" : "low";
            var why = risk switch
            {
                "high" => "the save holds a lot of this mod's state (script data, running scripts or many changed forms): expect leftovers, and quests or other mods that relied on it may stall. Clean leftovers after loading (CheckSave, CleanSave levels='safe').",
                "medium" => "some of its forms or scripts are in the save: the game drops its forms on load and leaves its script data as leftovers (cleanable).",
                _ => "nothing of it is in the save: removing it is safe for this save.",
            };
            return new { plugin = x.n, wasLight = x.light, risk, size = cf + inst + running, footprint = Footprint(x.n), why };
        }).OrderBy(r => r.risk == "high" ? 0 : r.risk == "medium" ? 1 : 2).ThenByDescending(r => r.size).ToList();

        var typeChanged = saved.Where(x => now.TryGetValue(x.n, out var c) && c.Light is bool l && l != x.light)
            .Select(x => new
            {
                plugin = x.n, was = x.light ? "light" : "full", now = x.light ? "full" : "light", footprint = Footprint(x.n),
                why = "if the update compacted its form IDs to make it light, every form of it in the save points at the wrong thing or nothing: high risk unless the author says the IDs were kept.",
            }).ToList();

        var added = current.Where(p => !savedNames.Contains(p.Name)).Select(p => p.Name).ToList();

        // A renamed plugin is a removal to the game (it matches by file name): pair removed and added names that differ only by
        // case, punctuation, version numbers or SE/AE/ESL tags, so the report can say "renamed", not "gone and something new".
        static string Norm(string n)
        {
            var b = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(n).ToLowerInvariant(), @"[^a-z0-9]+", " ");
            b = System.Text.RegularExpressions.Regex.Replace(b, @"\b(v?\d+(\s\d+)*|sse|se|ae|esl|esp|esm|flagged|version)\b", " ");
            return System.Text.RegularExpressions.Regex.Replace(b, @"\s+", "");
        }
        var addedByNorm = added.GroupBy(Norm).Where(g => g.Key.Length >= 4).ToDictionary(g => g.Key, g => g.First());
        var renamed = removed.Select(r => (r, to: addedByNorm.GetValueOrDefault(Norm(r.plugin)))).Where(t => t.to != null)
            .Select(t => new { from = t.r.plugin, to = t.to, t.r.risk, t.r.footprint }).ToList();

        // Order: relative order within each type (full, light) among plugins both lists share. Counting every shift would flag a
        // whole list after one insertion, so count plugins out of order (not on the longest run kept in the same order).
        var nowIndex = current.Select((p, k) => (p.Name, k)).ToDictionary(t => t.Name, t => t.k, StringComparer.OrdinalIgnoreCase);
        int OutOfOrder(IEnumerable<string> names)
        {
            var seq = names.Where(nowIndex.ContainsKey).Select(n => nowIndex[n]).ToList();
            var tails = new List<int>();   // longest increasing subsequence
            foreach (var v in seq)
            {
                var at = tails.BinarySearch(v);
                if (at < 0) at = ~at;
                if (at == tails.Count) tails.Add(v); else tails[at] = v;
            }
            return seq.Count - tails.Count;
        }
        var moved = OutOfOrder(scan.Plugins) + OutOfOrder(scan.LightPlugins);

        var high = removed.Count(r => r.risk == "high") + typeChanged.Count;
        var medium = removed.Count(r => r.risk == "medium");
        var verdict = high > 0
            ? $"Risky: {high} change(s) touch a lot of this save ({removed.Count(r => r.risk == "high")} removed mods with a big footprint{(typeChanged.Count > 0 ? $", {typeChanged.Count} changed between full and light" : "")}). Keep this save as a fallback; if you try it, check it afterwards (CheckSave, StackReport) and watch quests from those mods."
            : medium > 0
                ? $"Probably playable: {medium} removed mods left some data in the save; expect leftovers to clean (CheckSave, CleanSave levels='safe')."
                : "Safe on plugins: nothing removed has data in this save. (Changed scripts in updated mods can still matter; not checked yet.)";

        return new
        {
            save = scan.File,
            character = scan.Header.PlayerName,
            currentLoadOrder = currentSource,
            verdict,
            counts = new { inSave = saved.Count, now = current.Count, removed = removed.Count, probablyRenamed = renamed.Count, added = added.Count, typeChanged = typeChanged.Count, reordered = moved },
            probablyRenamed = renamed.Take(100),
            renamedNote = renamed.Count > 0 ? "To the game a renamed plugin is removed plus a new one: its forms and script data in the save don't carry over to the new name. Where the risk is high, the rename matters as much as a removal." : null,
            removed = removed.Where(r => r.risk != "low"),
            removedNoFootprint = removed.Where(r => r.risk == "low").Select(r => r.plugin),
            typeChanged,
            added = added.Take(100),
            addedNote = added.Count > 0 ? "New mods start fresh on load; scripted ones run their setup then. Usually fine; a few want a new game (their pages say so)." : null,
            movedNote = moved > 0 ? $"{moved} plugins load in a different order relative to the others: the game re-maps by name, so that alone is safe, but overrides now win differently (another mod's version of a record may apply)." : null,
            scripts,
            notChecked = scripts == null
                ? "Script changes in updated mods weren't compared (scripts: true compares them, 10-60 s). Other mods' own saved data (SKSE co-save) isn't compared."
                : "Other mods' own saved data (SKSE co-save) isn't compared.",
        };
    }
}
