namespace SkyrimMCP.Saves;

// FEAT-269: a save against the current load order, for collection updates: which of the save's plugins are gone, moved, changed
// between full and light, or new, and how much of the save each one owns (change forms, script instances, threads, queued
// calls). The engine re-maps forms by plugin name on load, so reordering alone is safe; what breaks is a removed plugin with a
// footprint, or one that changed type (an ESP compacted into an ESL renumbers its forms).
public static class MigrationReport
{
    public sealed record CurrentPlugin(string Name, bool? Light);

    static readonly string[] Implicit = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    /// <summary>
    /// Vortex/MO2-style Plugins.txt (active lines start with '*') plus the base game and Creation Club files. A null
    /// pluginsTxt is a game with no list yet (vanilla): only the base game and Creation Club files.
    /// </summary>
    public static List<CurrentPlugin> FromPluginsTxt(string? pluginsTxt, string? gameFolder)
    {
        var list = Implicit.Select(n => new CurrentPlugin(n, false)).ToList();
        if (gameFolder != null && File.Exists(Path.Combine(gameFolder, "Skyrim.ccc")))
            list.AddRange(File.ReadAllLines(Path.Combine(gameFolder, "Skyrim.ccc")).Where(l => l.Trim().Length > 0)
                .Select(l => new CurrentPlugin(l.Trim(), null)));
        foreach (var line in pluginsTxt == null ? [] : File.ReadAllLines(pluginsTxt))
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
    public static object CompareScripts(SaveScan scan, ScriptFiles.Index index, SaveScan? baseline = null)
    {
        // A save made with the current mods should match its scripts; whatever still differs there is noise (file resolution,
        // scripts patched without the save refreshing its record): drop those scripts from the old save's list.
        var noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (baseline?.ScriptMembers != null)
            foreach (var (script, members) in baseline.ScriptMembers)
            {
                if (baseline.InstancesByScript.GetValueOrDefault(script) == 0) continue;
                List<ScriptFiles.Variable>? v;
                try { v = ScriptFiles.ReadVariables(index, script); } catch (Exception ex) when (ex is IOException or InvalidDataException) { v = null; }
                if (v == null) continue;
                var a = new HashSet<string>(members.Select(m => m[0] + ":" + m[1]), StringComparer.OrdinalIgnoreCase);
                var b = new HashSet<string>(v.Select(x => x.Name + ":" + x.Type), StringComparer.OrdinalIgnoreCase);
                if (!a.SetEquals(b)) noise.Add(script);
            }
        var changed = new List<(string script, int instances, List<string> removed, List<string> retyped, List<string> added)>();
        var missing = new List<(string script, int instances)>();
        var compared = 0;
        var objectTypeChanges = 0;
        foreach (var (script, members) in scan.ScriptMembers ?? [])
        {
            var instances = scan.InstancesByScript.GetValueOrDefault(script);
            if (instances == 0 || noise.Contains(script)) continue;
            List<ScriptFiles.Variable>? vars;
            try { vars = ScriptFiles.ReadVariables(index, script); } catch (Exception ex) when (ex is IOException or InvalidDataException) { vars = null; }
            if (vars == null) { missing.Add((script, instances)); continue; }
            compared++;
            var saved = members.GroupBy(m => m[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);
            var now = vars.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Type, StringComparer.OrdinalIgnoreCase);
            var removed = saved.Keys.Where(k => !now.ContainsKey(k)).ToList();
            var added = now.Keys.Where(k => !saved.ContainsKey(k)).ToList();
            // A saved type of None means the save couldn't resolve it then (its script wasn't loaded): not a change. One object
            // type to another (ObjectReference -> Actor) usually keeps the value: noted, not counted as risky. Changes involving
            // Int/Float/Bool/String or arrays drop or misread the value.
            static bool Plain(string t) => t.EndsWith("[]") || t is "int" or "float" or "bool" or "string" ||
                                           t.Equals("Int", StringComparison.OrdinalIgnoreCase) || t.Equals("Float", StringComparison.OrdinalIgnoreCase) ||
                                           t.Equals("Bool", StringComparison.OrdinalIgnoreCase) || t.Equals("String", StringComparison.OrdinalIgnoreCase);
            var retyped = new List<string>();
            foreach (var (k, was) in saved)
            {
                if (!now.TryGetValue(k, out var t) || t.Equals(was, StringComparison.OrdinalIgnoreCase) || was.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
                if (Plain(was) || Plain(t)) retyped.Add($"{k}: {was} -> {t}");
                else objectTypeChanges++;
            }
            if (removed.Count + added.Count + retyped.Count > 0) changed.Add((script, instances, removed, retyped, added));
        }
        static string Clean(string v) => v.StartsWith("::") && v.EndsWith("_var") ? v[2..^4] : v;   // a property's hidden variable
        var risky = changed.Where(c => c.removed.Count + c.retyped.Count > 0).OrderByDescending(c => c.retyped.Count).ThenByDescending(c => c.instances).ToList();
        var onlyAdded = changed.Where(c => c.removed.Count + c.retyped.Count == 0).ToList();
        return new
        {
            compared,
            baseline = baseline?.File,
            ignoredAsNoise = noise.Count,
            changedRisky = risky.Count,
            changedOnlyAdded = onlyAdded.Count,
            objectTypeChanges,
            scriptFileMissing = missing.Count,
            changed = risky.Take(20).Select(c => new
            {
                c.script, c.instances,
                removed = c.removed.Select(Clean).Take(5), retyped = c.retyped.Take(5).Select(Clean), added = c.added.Select(Clean).Take(5),
            }),
            onlyAddedExamples = onlyAdded.OrderByDescending(c => c.instances).Take(10).Select(c => c.script),
            missingExamples = missing.OrderByDescending(m => m.instances).Take(10).Select(m => $"{m.script} ({m.instances})"),
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
        string Footprint(string n)
        {
            var running = ModCount(scan.ThreadsByPlugin, n) + ModCount(scan.QueuedByPlugin, n);
            return $"{Get(scan.ChangeFormsByPlugin, n)} changed forms, {Get(scan.InstancesByPlugin, n)} script instances" + (running > 0 ? $", {running} threads/queued calls" : "");
        }

        var removed = saved.Where(x => !now.ContainsKey(x.n)).Select(x =>
        {
            var cf = Get(scan.ChangeFormsByPlugin, x.n);
            var inst = Get(scan.InstancesByPlugin, x.n);
            var running = ModCount(scan.ThreadsByPlugin, x.n) + ModCount(scan.QueuedByPlugin, x.n);
            var risk = running > 0 || inst > 50 || cf > 500 ? "high" : inst > 0 || cf > 0 ? "medium" : "low";
            // Compact: one line per plugin (the round's July save gave a 311 KB reply with the reason repeated 2,000 times).
            return new { plugin = x.n, risk, size = cf + inst + running,
                         footprint = $"{cf} changed forms, {inst} script instances" + (running > 0 ? $", {running} threads/queued calls" : "") };
        }).OrderBy(r => r.risk == "high" ? 0 : r.risk == "medium" ? 1 : 2).ThenByDescending(r => r.size).ToList();

        var typeChanged = saved.Where(x => now.TryGetValue(x.n, out var c) && c.Light is bool l && l != x.light)
            .Select(x => new
            {
                plugin = x.n, was = x.light ? "light" : "full", now = x.light ? "full" : "light", footprint = Footprint(x.n),
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

        var highList = removed.Where(r => r.risk == "high").ToList();
        var mediumList = removed.Where(r => r.risk == "medium").ToList();
        var lowList = removed.Where(r => r.risk == "low").ToList();
        var summary = $"{saved.Count} plugins in the save, {current.Count} now: {removed.Count} removed ({highList.Count} high risk, {mediumList.Count} medium, " +
                      $"{lowList.Count} with nothing in the save), {renamed.Count} probably renamed, {typeChanged.Count} changed full/light, {added.Count} added, {moved} reordered.";
        return new
        {
            verdict,
            summary,
            save = scan.File,
            character = scan.Header.PlayerName,
            currentLoadOrder = currentSource,
            legend = new
            {
                high = "the save holds a lot of the mod's state (script data, running scripts, many changed forms): expect leftovers; quests or mods that relied on it may stall. After loading: CheckSave, CleanSave levels='safe'.",
                medium = "some of its forms or scripts are in the save: the game drops its forms and leaves script leftovers (cleanable).",
                renamed = "a rename is a removal to the game: its data doesn't carry over to the new name.",
                typeChanged = "an ESP compacted into an ESL (or back) renumbers its forms: every saved form of it points at the wrong thing unless the author kept the IDs.",
                reordered = "safe on its own (the game re-maps by name), but overrides now win differently.",
                added = "new mods start fresh on load; a few want a new game (their pages say so).",
            },
            highRisk = highList.Take(30),
            highRiskMore = highList.Count > 30 ? highList.Count - 30 : (int?)null,
            mediumRisk = mediumList.Take(20),
            mediumRiskMore = mediumList.Count > 20 ? mediumList.Count - 20 : (int?)null,
            removedNoFootprint = new { count = lowList.Count, examples = lowList.Take(15).Select(r => r.plugin) },
            probablyRenamed = renamed.Take(20).Select(r => new { r.from, r.to, r.risk }),
            typeChanged = typeChanged.Take(20),
            added = new { count = added.Count, examples = added.Take(15) },
            scripts,
            notChecked = scripts == null
                ? "Script changes in updated mods weren't compared (scripts: true compares them). Other mods' own saved data (SKSE co-save) isn't compared."
                : "Other mods' own saved data (SKSE co-save) isn't compared.",
        };
    }
}
