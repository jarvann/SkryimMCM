using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using SkyrimMCP.Plugins;

namespace SkyrimMCP.Tools;

/// <summary>
/// Cleaning dirty plugins (FEAT-288) by driving SSEEdit's own Quick Auto Clean (-qac) from the command line, one plugin at
/// a time: SkyLink chooses what to clean from LOOT's masterlist (dirty entries matched by checksum), backs each file up,
/// runs SSEEdit, and checks the result against LOOT's "clean" checksums. SSEEdit runs as a separate program, so none of its
/// (GPL) code is part of SkyLink. MO2 setups run SSEEdit from MO2 instead (it has to see MO2's mods).
/// </summary>
[McpServerToolType]
public class CleanTools : ToolBase
{
    public CleanTools(IPipeClient pipe) : base(pipe) { }

    static readonly string[] Official = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    [McpServerTool]
    [Description("Clean dirty plugins with SSEEdit's Quick Auto Clean (FEAT-288): removes identical-to-master records (ITMs) and " +
        "restores deleted references as disabled, as SSEEdit's own 'Quick Auto Clean' does. Without plugins, cleans what LOOT's " +
        "masterlist lists as dirty in this load order (GetLootNotes shows them); plugins: a ';'-separated list to limit it. Skips " +
        "the base game and Creation Club files unless includeOfficial (opinions differ on cleaning them; say so). Deleted navmeshes " +
        "can't be fixed this way: such plugins are cleaned of the rest and reported. Each file is backed up first (SkyLinkAI\\Build" +
        "\\cleaning-backups) and checked against LOOT's clean checksums afterwards. The game must be closed. Vortex: Vortex may " +
        "report the changed files; keep them. MO2: run SSEEdit from MO2 instead (this explains how). Ask the player first: a mod " +
        "update replaces a cleaned plugin. confirm=false (default) lists what would be cleaned.")]
    public async Task<object> CleanPlugins(string? plugins = null, bool confirm = false, bool includeOfficial = false, int max = 20)
    {
        var ml = LootMasterlist.Load();
        if (ml == null) return new { error = "LOOT's masterlist wasn't found: SkyLink picks plugins to clean from it. Install LOOT (loot.github.io) and run it once." };
        var lo = LoadOrder.Read();
        var manager = InstallTools.DetectManager();
        var xedit = FindXEdit();

        var wanted = plugins?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string plugin, string path, LootMasterlist.Cleaning info)>();
        var skipped = new List<object>();
        await Task.Run(() =>
        {
            foreach (var p in lo.Active)
            {
                if (wanted != null && !wanted.Contains(p)) continue;
                if (wanted == null && !ml.EntriesFor(p).Any()) continue;
                if (lo.FileOf(p) is not string path) continue;
                var info = ml.CleaningFor(p, path);
                if (!info.Dirty)
                {
                    if (wanted != null) skipped.Add(new { plugin = p, reason = info.KnownClean ? "already clean (LOOT's clean checksum)" : "LOOT has no dirty record for this exact file" });
                    continue;
                }
                var official = Official.Contains(p, StringComparer.OrdinalIgnoreCase) || p.StartsWith("cc", StringComparison.OrdinalIgnoreCase) || p.Equals("_ResourcePack.esl", StringComparison.OrdinalIgnoreCase);
                if (official && !includeOfficial) { skipped.Add(new { plugin = p, reason = "base game or Creation Club file (includeOfficial to clean)" }); continue; }
                candidates.Add((p, path, info));
            }
        });
        var plan = candidates.Take(Math.Clamp(max, 1, 200)).Select(c => new
        {
            plugin = c.plugin,
            itm = c.info.Itm, deletedReferences = c.info.Udr, deletedNavmeshes = c.info.Nav,
            note = c.info.Nav > 0 ? "Has deleted navmeshes: Quick Auto Clean can't fix those (the mod's author has to); the rest is cleaned." : null,
        }).ToList();

        if (!confirm || plan.Count == 0)
            return new
            {
                dryRun = true,
                masterlist = ml.Age(),
                toClean = plan,
                more = Math.Max(0, candidates.Count - plan.Count),
                skipped = skipped.Take(30),
                xedit = xedit ?? "not found",
                manager = manager.ToString(),
                next = xedit == null ? "SSEEdit isn't installed (free on Nexus: mod 164). Install it, or set its folder with SetSkyLinkSetting xeditPath."
                    : manager == InstallTools.Manager.Mo2 ? Mo2Instructions(xedit)
                    : plan.Count == 0 ? "Nothing to clean." : "If the player agrees, call again with confirm=true.",
            };

        if (xedit == null) return new { error = "SSEEdit isn't installed (free on Nexus: mod 164), or set its folder with SetSkyLinkSetting xeditPath." };
        if (manager == InstallTools.Manager.Mo2) return new { cleaned = false, note = Mo2Instructions(xedit), toClean = plan };
        if (Process.GetProcessesByName("SkyrimSE").Length > 0) return new { error = "Close the game first: plugins can't be cleaned while it has them loaded." };

        var backupDir = Path.Combine(ModBuildTools.BuildRoot, "cleaning-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backupDir);
        var data = SkyLinkSettings.DataPath();
        var log = Path.Combine(Path.GetDirectoryName(xedit)!, Path.GetFileNameWithoutExtension(xedit) + "_log.txt");
        var results = new List<object>();
        foreach (var c in candidates.Take(Math.Clamp(max, 1, 200)))
        {
            File.Copy(c.path, Path.Combine(backupDir, c.plugin), overwrite: true);
            var logBefore = File.Exists(log) ? new FileInfo(log).Length : 0;
            var args = $"-qac -autoexit -autoload \"{c.plugin}\"" + (data != null ? $" -D:\"{data}\"" : "");
            var run = await ModBuildTools.RunProcess(xedit, args, Path.GetDirectoryName(xedit)!, TimeSpan.FromMinutes(15));
            var after = await Task.Run(() => ml.CleaningFor(c.plugin, c.path));
            string? logTail = null;
            int? removed = null, undeleted = null;
            try
            {
                if (File.Exists(log))
                {
                    using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(Math.Min(logBefore, fs.Length), SeekOrigin.Begin);
                    var text = new StreamReader(fs).ReadToEnd();
                    removed = Regex.Matches(text, @"Removing:", RegexOptions.IgnoreCase).Count;
                    undeleted = Regex.Matches(text, @"Undeleting:", RegexOptions.IgnoreCase).Count;
                    logTail = text.Length > 400 ? text[^400..] : text;
                }
            }
            catch (IOException) { }
            results.Add(new
            {
                plugin = c.plugin,
                changed = after.Crc != c.info.Crc,
                matchesLootClean = after.KnownClean,
                stillDirtyPerLoot = after.Dirty,
                removedRecords = removed,
                undeletedReferences = undeleted,
                deletedNavmeshesLeft = c.info.Nav,
                xeditExit = run.ExitCode,
                timedOut = run.TimedOut,
                logTail = after.Crc == c.info.Crc ? logTail : null,
            });
        }
        return new
        {
            cleaned = results.Count,
            backups = backupDir,
            results,
            undo = "To undo, copy a plugin back from the backups folder over the cleaned one.",
            note = "matchesLootClean: the cleaned file now has a checksum LOOT knows as clean. Changed but not matching can still be fine " +
                   "(a different SSEEdit version); unchanged means SSEEdit didn't save (see logTail).",
        };
    }

    static string Mo2Instructions(string? xedit) =>
        "MO2 manages this game, so SSEEdit must run from MO2 to see its mods. In MO2, add an executable for " +
        (xedit ?? "SSEEdit.exe") + " named 'SSEEdit QAC' with the arguments -qac -autoexit -autoload \"<plugin>\" (one plugin per run), " +
        "or copy SSEEdit.exe as SSEEditQuickAutoClean.exe (the file name turns on that mode) and pick each dirty plugin when it asks. " +
        "Cleaned plugins land in MO2's overwrite folder; move them into the mod they came from.";

    internal static string? FindXEdit()
    {
        var names = new[] { "SSEEdit.exe", "SSEEdit64.exe", "xEdit.exe", "xEdit64.exe" };
        var folders = new List<string>();
        if (SkyLinkSettings.Load()["xeditPath"] is JsonValue v && v.TryGetValue<string>(out var configured) && configured.Length > 0) folders.Add(configured);
        if (SkyLinkSettings.SkyrimPath().Path is string game) { folders.Add(game); folders.Add(Path.Combine(game, "Tools")); folders.Add(Path.Combine(game, "SSEEdit")); }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\Modding", @"C:\Games", @"C:\Tools" })
            foreach (var sub in new[] { "SSEEdit", "xEdit", "TES5Edit", "Modding\\SSEEdit" }) folders.Add(Path.Combine(root, sub));
        foreach (var f in folders.Where(Directory.Exists))
            foreach (var n in names)
                if (File.Exists(Path.Combine(f, n))) return Path.Combine(f, n);
        return null;
    }
}
