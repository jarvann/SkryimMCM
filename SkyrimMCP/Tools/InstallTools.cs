using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using SkyrimMCP.Plugins;

namespace SkyrimMCP.Tools;

/// <summary>
/// Putting a plugin SkyLink built into the load order and sorting it (FEAT-275). Who owns the load order decides how:
/// MO2 keeps it per profile (SkyLink installs into the mods folder and enables it there); Vortex owns Data and
/// Plugins.txt and redeploys over hand edits (the player installs the zip and sorts in Vortex); with no mod manager the
/// game's own Plugins.txt is the load order. LOOT sorts when it is installed and nothing else manages the order.
/// Every write backs up the file it changes first.
/// </summary>
[McpServerToolType]
public class InstallTools : ToolBase
{
    public InstallTools(IPipeClient pipe) : base(pipe) { }

    internal enum Manager { None, Mo2, Vortex }

    internal static Manager DetectManager()
    {
        if (SkyLinkSettings.Mo2Instance().Folder != null) return Manager.Mo2;
        if (SkyLinkSettings.DataPath() is string data && File.Exists(Path.Combine(data, "vortex.deployment.json"))) return Manager.Vortex;
        return Manager.None;
    }

    [McpServerTool]
    [Description("Install a plugin SkyLink built (CreatePlugin, in a build project) into the load order (FEAT-275). MO2: copied into " +
        "the mods folder, the mod enabled and the plugin activated in the profile (MO2 must be closed). Vortex: Vortex owns the load " +
        "order and redeploys over hand edits, so this packages a zip for the player to install in Vortex. No mod manager: copied into " +
        "Data and activated in Plugins.txt. Checks the plugin's masters first (CheckPluginDependencies) and stops when one is " +
        "missing, disabled or loads too late (ignoreMasterProblems: true installs anyway). Changes apply the next time the game " +
        "starts (RestartGame). dryRun (default true) says what would change.")]
    public async Task<object> InstallPlugin(string project, bool dryRun = true, bool ignoreMasterProblems = false)
    {
        var result = await InstallPluginCore(project, dryRun, ignoreMasterProblems);
        return result;
    }

    async Task<object> InstallPluginCore(string project, bool dryRun, bool ignoreMasterProblems)
    {
        var name = string.Concat(project.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dir = Path.Combine(ModBuildTools.BuildRoot, name);
        var plugins = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        if (plugins.Count == 0) return (new { error = $"No plugin in build project '{name}' ({dir}). CreatePlugin writes one." });
        var manager = DetectManager();

        // FEAT-274: a plugin whose masters are missing or load after it crashes the game at start.
        var checks = new List<object>();
        var blocking = new List<string>();
        var deps = new PluginTools(_pipe);
        foreach (var p in plugins)
        {
            var check = await deps.CheckPluginDependencies(p);
            checks.Add(check);
            var node = System.Text.Json.JsonSerializer.SerializeToNode(check);
            if (node?["ok"]?.GetValue<bool>() == false) blocking.Add(node["verdict"]?.ToString() ?? Path.GetFileName(p));
        }
        if (blocking.Count > 0 && !ignoreMasterProblems)
            return new { installed = false, error = "Masters problem: " + string.Join(" ", blocking), masters = checks, next = "Fix the masters (install or enable them), or pass ignoreMasterProblems: true if you know better." };

        if (manager == Manager.Vortex)
            return (new
            {
                manager = "Vortex",
                installed = false,
                // Round A: build the zip here rather than send the AI to another tool first.
                package = dryRun ? null : ModBuildTools.PackageOverrideModCore(name, null, null, installToMo2: false),
                next = (dryRun ? "With dryRun: false this packages a zip; then " : "") +
                       "ask the player to install the zip in Vortex (Mods > Install From File), enable it and the plugin, then sort in Vortex (Plugins > Sort Now).",
                plugins = plugins.Select(Path.GetFileName),
            });

        if (manager == Manager.Mo2)
        {
            var (mo2, profile) = SkyLinkSettings.Mo2Instance();
            profile ??= DefaultProfile(mo2!);
            var profileDir = Path.Combine(mo2!, "profiles", profile);
            if (dryRun) return (new { dryRun = true, manager = "MO2", modFolder = Path.Combine(mo2!, "mods", name), profile, activate = plugins.Select(Path.GetFileName) });
            if (Process.GetProcessesByName("ModOrganizer").Length > 0)
                return (new { error = "MO2 is running: it rewrites its profile files when it closes. Close MO2 and run this again." });
            var pack = ModBuildTools.PackageOverrideModCore(name, null, null, installToMo2: true);
            var activated = plugins.Select(p => Activate(Path.Combine(profileDir, "plugins.txt"), Path.GetFileName(p), Path.Combine(profileDir, "loadorder.txt"))).ToList();
            return (new { manager = "MO2", package = pack, activated, next = "Sort with LOOT run from MO2 (SortLoadOrder says how), then RestartGame." });
        }

        // No mod manager: the Data folder and the game's Plugins.txt.
        var data = SkyLinkSettings.DataPath();
        if (data == null) return (new { error = "The game's Data folder wasn't found (SetSkyLinkSetting skyrimPath)." });
        var pluginsTxt = GamePluginsTxt();
        var targets = plugins.Select(p => Path.Combine(data, Path.GetFileName(p))).ToList();
        var clash = targets.Where(File.Exists).ToList();
        if (dryRun) return (new { dryRun = true, manager = "none", copyTo = targets, pluginsTxt, wouldReplace = clash });
        if (clash.Count > 0) return (new { error = $"A plugin with that name is already in Data: {string.Join(", ", clash.Select(Path.GetFileName))}. Rename the build or remove the old one first." });
        foreach (var (src, dest) in plugins.Zip(targets)) File.Copy(src, dest);
        var activatedNone = plugins.Select(p => Activate(pluginsTxt, Path.GetFileName(p), null)).ToList();
        return (new { manager = "none", copied = targets, activated = activatedNone, next = "Sort with SortLoadOrder (LOOT), then RestartGame." });
    }

    [McpServerTool]
    [Description("Sort the load order with LOOT (FEAT-275) and say what moved. Vortex: sort in Vortex instead (it uses LOOT itself and " +
        "owns the order). MO2: LOOT must run through MO2 to see its mods; uses MO2's LOOT executable entry when there is one. " +
        "No mod manager: runs LOOT with --auto-sort, which sorts, applies and quits. checkPlugin: report where that plugin ends up. " +
        "Close the game first: a sorted order applies on the next start. confirm (default false) runs it; otherwise says how it would.")]
    public async Task<object> SortLoadOrder(bool confirm = false, string? checkPlugin = null)
    {
        var manager = DetectManager();
        if (manager == Manager.Vortex)
            return new { manager = "Vortex", sorted = false, next = "Ask the player to sort in Vortex: Plugins > Sort Now (Vortex runs LOOT's rules itself)." };
        var loot = FindLoot();
        if (loot == null)
            return new { sorted = false, lootFound = false, note = "LOOT isn't installed (it's free: loot.github.io). Without it, new plugins sit at the end of the load order, which is usually fine for a small patch." };
        if (Process.GetProcessesByName("SkyrimSE").Length > 0)
            return new { sorted = false, error = "Close the game first: the load order is read when the game starts." };

        var game = SkyLinkSettings.SkyrimPath().Path;
        if (manager == Manager.Mo2)
        {
            var (mo2, _) = SkyLinkSettings.Mo2Instance();
            var launch = SessionTools.FindLauncher();
            return new
            {
                manager = "MO2",
                sorted = false,
                note = "LOOT has to run inside MO2 to see the mods' plugins. Ask the player to start LOOT from MO2's executables list and click Sort, then Apply.",
                mo2,
                lootPath = loot,
                launchHint = launch.how,
            };
        }

        var txt = GamePluginsTxt();
        var before = File.Exists(txt) ? ActiveOrder(txt) : [];
        if (!confirm) return new { dryRun = true, manager = "none", loot, pluginsTxt = txt, active = before.Count };

        File.Copy(txt, txt + $".skylink-{DateTime.Now:yyyyMMdd-HHmmss}.bak");
        var args = $"--game=\"Skyrim Special Edition\" --auto-sort" + (game != null ? $" --game-path=\"{game}\"" : "");
        var run = await ModBuildTools.RunProcess(loot, args, Path.GetDirectoryName(loot)!, TimeSpan.FromMinutes(10));
        var after = File.Exists(txt) ? ActiveOrder(txt) : [];
        var moved = after.Where((p, i) => before.IndexOf(p) != i).Count();
        return new
        {
            manager = "none",
            sorted = run.ExitCode == 0 && !run.TimedOut,
            lootExit = run.ExitCode,
            timedOut = run.TimedOut,
            pluginsMoved = moved,
            checkPlugin,
            position = checkPlugin == null ? null : (int?)(after.FindIndex(p => p.Equals(checkPlugin, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i + 1 : null),
            backup = "Plugins.txt was backed up next to it (.skylink-<time>.bak).",
            output = run.ExitCode == 0 ? null : run.Output.Length > 800 ? run.Output[^800..] : run.Output,
        };
    }

    // ---- files ----

    /// <summary>The game's Plugins.txt (Steam or GOG folder; MO2's profile when MO2 manages the game).</summary>
    internal static string GamePluginsTxt() => LoadOrder.PluginsTxtPath().Path;

    static string DefaultProfile(string mo2)
    {
        var all = Directory.Exists(Path.Combine(mo2, "profiles")) ? Directory.GetDirectories(Path.Combine(mo2, "profiles")) : [];
        return all.Length == 1 ? Path.GetFileName(all[0]) : "Default";
    }

    static List<string> ActiveOrder(string txt) =>
        File.ReadAllLines(txt).Select(l => l.Trim()).Where(l => l.StartsWith('*')).Select(l => l[1..]).ToList();

    /// <summary>Add "*plugin" to a plugins.txt (and to MO2's loadorder.txt), at the end, after a backup.</summary>
    static object Activate(string pluginsTxt, string plugin, string? loadorderTxt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pluginsTxt)!);
        var lines = File.Exists(pluginsTxt) ? File.ReadAllLines(pluginsTxt).ToList() : [];
        if (File.Exists(pluginsTxt)) File.Copy(pluginsTxt, pluginsTxt + $".skylink-{DateTime.Now:yyyyMMdd-HHmmss}.bak", overwrite: true);
        lines.RemoveAll(l => l.TrimStart('*').Trim().Equals(plugin, StringComparison.OrdinalIgnoreCase));
        lines.Add("*" + plugin);
        File.WriteAllLines(pluginsTxt, lines);
        if (loadorderTxt != null && File.Exists(loadorderTxt))
        {
            var order = File.ReadAllLines(loadorderTxt).ToList();
            if (!order.Any(l => l.Trim().Equals(plugin, StringComparison.OrdinalIgnoreCase)))
            {
                File.Copy(loadorderTxt, loadorderTxt + $".skylink-{DateTime.Now:yyyyMMdd-HHmmss}.bak", overwrite: true);
                order.Add(plugin);
                File.WriteAllLines(loadorderTxt, order);
            }
        }
        return new { plugin, file = pluginsTxt, active = true };
    }

    static string? FindLoot()
    {
        var settings = SkyLinkSettings.Load();
        var candidates = new List<string>();
        if (settings["lootPath"]?.ToString() is string configured && configured.Length > 0) candidates.Add(Path.Combine(configured, "LOOT.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LOOT", "LOOT.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LOOT", "LOOT.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LOOT", "LOOT.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }
}
