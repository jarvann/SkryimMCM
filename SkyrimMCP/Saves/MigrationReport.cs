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

    public static object Build(SaveScan scan, List<CurrentPlugin> current, string currentSource)
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

        // Order: positions among the plugins both lists share (full and light together, as the load order lists them).
        var sharedSaved = saved.Where(x => now.ContainsKey(x.n)).Select(x => x.n).ToList();
        var sharedNow = current.Where(p => savedNames.Contains(p.Name)).Select(p => p.Name).ToList();
        var posNow = sharedNow.Select((n, i) => (n, i)).ToDictionary(t => t.n, t => t.i, StringComparer.OrdinalIgnoreCase);
        var moved = sharedSaved.Select((n, i) => (n, i, j: posNow.TryGetValue(n, out var j) ? j : i)).Count(t => Math.Abs(t.i - t.j) > 0);

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
            counts = new { inSave = saved.Count, now = current.Count, removed = removed.Count, added = added.Count, typeChanged = typeChanged.Count, movedAmongShared = moved },
            removed = removed.Where(r => r.risk != "low"),
            removedNoFootprint = removed.Where(r => r.risk == "low").Select(r => r.plugin),
            typeChanged,
            added = added.Take(100),
            addedNote = added.Count > 0 ? "New mods start fresh on load; scripted ones run their setup then. Usually fine; a few want a new game (their pages say so)." : null,
            movedNote = moved > 0 ? $"{moved} plugins sit at a different place in the order: the game re-maps by name, so that alone is safe, unless a mod stores raw form IDs in its own data." : null,
            notChecked = "Script changes in updated mods (variables renamed or removed) and other mods' own saved data aren't compared yet.",
        };
    }
}
