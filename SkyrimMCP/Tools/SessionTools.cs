using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// The restart cycle (FEAT-278), starting a new game from the main menu (FEAT-015) and the main-menu content reload
/// experiment (FEAT-277): what a change that needs a fresh game (a new plugin, a patched script, a host DLL) asks for.
/// Launching goes through SKSE's loader, or MO2 when the game is run from MO2 (it has to supply its virtual Data folder).
/// </summary>
[McpServerToolType]
public class SessionTools : ToolBase
{
    public SessionTools(IPipeClient pipe) : base(pipe) { }

    [McpServerTool]
    [Description("Restart Skyrim (FEAT-278): save (saveFirst, default true), quit the way Quit to Desktop does, wait for the game to " +
        "close, and start it again through SKSE (or MO2 when it is in use and running). Returns once the game is launched: then " +
        "call WaitForSkyrim (allow several minutes for a large load order) and LoadSave with the save it names. For changes that " +
        "need a fresh game: a new or patched mod, a new SkyLink plugin DLL. The player must agree first (unsaved progress since " +
        "the save is lost; confirm=true runs it). confirm=false (default) says what would happen and how it would launch.")]
    public async Task<object> RestartGame(bool confirm = false, bool saveFirst = true)
    {
        var running = Process.GetProcessesByName("SkyrimSE").Length > 0;
        // Relaunch the game that is running, wherever it is (Steam or GOG): pin its folder before anything else decides.
        var quitting = SkyLinkSettings.RunningGameFolder();
        if (quitting != null) SkyLinkSettings.GameInstallFolder = quitting;
        var launch = FindLauncher();
        // At the main menu there is no game to save (round B planned a save there).
        if (running && saveFirst)
        {
            try
            {
                var state = await _pipe.SendRequestAsync("get_session_state");
                if (state?["gameLoaded"]?.GetValue<bool>() != true) saveFirst = false;
            }
            catch (Exception) { }
        }
        if (!confirm)
            return new
            {
                dryRun = true,
                gameRunning = running,
                steps = new[] { saveFirst && running ? "save as SkyLink_restart_<time>" : null, running ? "quit to desktop" : null, launch.how, "then WaitForSkyrim and LoadSave" }.Where(s => s != null),
                gameFolder = quitting ?? SkyLinkSettings.SkyrimPath().Path,
                launchExe = launch.exe,
                launchArgs = string.IsNullOrEmpty(launch.args) ? null : launch.args,
                canLaunch = launch.exe != null,
                note = launch.exe == null ? launch.how : null,
            };

        string? savedAs = null;
        if (running)
        {
            if (saveFirst)
            {
                if (await SafetyBlock("move") is string blocked) return new { error = "Not now: " + blocked + " Nothing was saved or closed." };
                savedAs = $"SkyLink_restart_{DateTime.Now:yyyyMMdd-HHmmss}";
                var (error, _) = await SaveTools.RequestSaveEx(_pipe, savedAs);
                if (error != null) return new { error = "Didn't restart: " + error };
            }
            await _pipe.SendRequestAsync("quit_game");
            var closed = false;
            for (var waited = 0; waited < 90_000 && !closed; waited += 1000)
            {
                await Task.Delay(1000);
                closed = Process.GetProcessesByName("SkyrimSE").Length == 0;
            }
            if (!closed)
                return new { error = "Skyrim is still running 90 seconds after the quit request (a mod may hold it, or it hung). Close it by hand, then call RestartGame again with saveFirst=false.", savedAs };
            await Task.Delay(3000);   // let SKSE and the mod manager let go of their files
        }

        if (launch.exe == null) return new { launched = false, savedAs, reason = launch.how };
        // A launcher outside the game that quit would start another game (an MO2 launch runs through MO2's own exe, so only
        // check direct launches).
        if (quitting != null && launch.exe.EndsWith("skse64_loader.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetDirectoryName(launch.exe), quitting, StringComparison.OrdinalIgnoreCase))
            return new { launched = false, savedAs, reason = $"Would have launched {launch.exe}, not the game that quit ({quitting}): not launched. Start it by hand." };
        try
        {
            Process.Start(new ProcessStartInfo(launch.exe, launch.args) { WorkingDirectory = launch.dir, UseShellExecute = true });
        }
        catch (Exception ex) { return new { launched = false, savedAs, reason = $"Couldn't start {launch.exe}: {ex.Message}" }; }
        return new
        {
            launched = true,
            how = launch.how,
            launchExe = launch.exe,
            savedAs,
            next = savedAs != null
                ? $"Call WaitForSkyrim (timeoutSeconds 600 for a large load order), then LoadSave \"{savedAs}\"."
                : "Call WaitForSkyrim (timeoutSeconds 600 for a large load order), then load a save.",
        };
    }

    [McpServerTool]
    [Description("Start a new game from the main menu (FEAT-015). With a cell (editor ID, e.g. RiverwoodSleepingGiantInn) the game " +
        "starts straight there, as the console's coc does from the main menu: no intro, the main quest's opening doesn't run and " +
        "alternate-start mods are skipped too, which suits a test character or a blueprint transfer. Without a cell, the player " +
        "picks New in the main menu (the normal start, with any alternate-start mod). Only at the main menu with no game loaded.")]
    public async Task<object> StartNewGame(string? cell = null)
    {
        var state = await _pipe.SendRequestAsync("get_session_state");
        if (state?["mainMenuOpen"]?.GetValue<bool>() != true || state?["gameLoaded"]?.GetValue<bool>() == true)
            return new { error = "Only at the main menu with no game loaded.", state = DeserializeResponse(state) };
        if (string.IsNullOrWhiteSpace(cell))
            return new { started = false, note = "Ask the player to pick New in the main menu for the normal start (the intro, or their alternate-start mod). Pass a cell to start somewhere directly instead." };
        var trimmed = cell.Trim();
        if (trimmed.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))) return new { error = "cell must be a cell editor ID (letters, digits, underscores)" };
        var run = await _pipe.SendRequestAsync("execute_command", new JsonObject { ["command"] = "coc " + trimmed });
        return new
        {
            started = run?["error"] == null,
            cell = trimmed,
            error = run?["error"]?.ToString(),   // the console text of a whole new game's load (169 KB in round A) isn't returned
            note = "The game loads into the cell; WaitForSkyrim then GetPlayerInfo confirms it. The main quest hasn't started: an alternate-start mod, or ReplayQuests from a blueprint, can set quests up.",
        };
    }

    [McpServerTool]
    [Description("EXPERIMENT (FEAT-277): ask the game to reload its content from the main menu, the way it does after the Creations " +
        "menu installs something, to find out whether a newly installed plugin then loads without restarting Skyrim. Only at the " +
        "main menu with no game loaded. checkPlugin: a plugin file name to look for in the load order before and after. Reports " +
        "what changed; if the plugin doesn't appear, a restart (RestartGame) is still needed. Can crash with some SKSE plugins: " +
        "tell the player it is an experiment and save first.")]
    public async Task<object> ReloadContent(string? checkPlugin = null)
    {
        var state = await _pipe.SendRequestAsync("get_session_state");
        if (state?["mainMenuOpen"]?.GetValue<bool>() != true || state?["gameLoaded"]?.GetValue<bool>() == true)
            return new { error = "Only at the main menu with no game loaded.", state = DeserializeResponse(state) };

        async Task<(int count, bool? has)> LoadOrder()
        {
            try
            {
                var lo = await _pipe.SendRequestAsync("get_load_order");
                var mods = lo?["mods"] as JsonArray ?? new JsonArray();
                bool? has = checkPlugin == null ? null : mods.Any(m => string.Equals(m?["name"]?.ToString(), checkPlugin, StringComparison.OrdinalIgnoreCase));
                return (mods.Count, has);
            }
            catch (Exception) { return (-1, null); }
        }

        var before = await LoadOrder();
        var req = await _pipe.SendRequestAsync("reload_content");
        if (req?["error"] != null) return DeserializeResponse(req);

        // The reload runs on the main menu's next frames; give it time, then wait for the plugin to answer again.
        var answered = false;
        for (var waited = 0; waited < 300_000 && !answered; waited += 2000)
        {
            await Task.Delay(2000);
            try
            {
                var s = await _pipe.SendRequestAsync("get_session_state");
                answered = waited >= 6000 && s?["mainMenuOpen"]?.GetValue<bool>() == true && s?["loadingMenuOpen"]?.GetValue<bool>() != true;
            }
            catch (Exception) { }
        }
        var after = await LoadOrder();
        return new
        {
            experiment = "main-menu content reload",
            backAtMainMenu = answered,
            pluginsBefore = before.count,
            pluginsAfter = after.count,
            checkPlugin,
            pluginBefore = before.has,
            pluginAfter = after.has,
            verdict = checkPlugin == null ? "Compare plugin counts; pass checkPlugin to test a specific new plugin."
                : after.has == true && before.has != true ? "The new plugin loaded without a restart."
                : after.has == true ? "The plugin was already loaded before the reload."
                : "The plugin still isn't loaded: this reload doesn't pick up new plugins here, so restart (RestartGame).",
        };
    }

    // ---- launching ----

    internal sealed record Launch(string? exe, string? args, string? dir, string how);

    internal static Launch FindLauncher()
    {
        var game = SkyLinkSettings.SkyrimPath().Path;
        var (mo2, _) = SkyLinkSettings.Mo2Instance();
        if (mo2 != null)
        {
            // MO2 has to start the game itself (its virtual Data folder), through the executable entry for SKSE.
            var title = Mo2SkseTitle(mo2);
            var exe = RunningMo2Exe() ?? (File.Exists(Path.Combine(mo2, "ModOrganizer.exe")) ? Path.Combine(mo2, "ModOrganizer.exe") : null);
            var globalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
            var instance = mo2.StartsWith(globalRoot, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(mo2.TrimEnd('\\')) : "";
            if (exe != null && title != null)
                return new(exe, $"\"moshortcut://{instance}:{title}\"", Path.GetDirectoryName(exe), $"through MO2 ({title})");
            return new(null, null, null, title == null
                ? "MO2 is in use but has no executable entry for skse64_loader.exe: ask the player to start the game from MO2."
                : "MO2 is in use but its program wasn't found (start MO2 first, or ask the player to start the game from MO2).");
        }
        if (game != null && File.Exists(Path.Combine(game, "skse64_loader.exe")))
            return new(Path.Combine(game, "skse64_loader.exe"), "", game, "with skse64_loader.exe");
        return new(null, null, null, "skse64_loader.exe wasn't found in the game folder: ask the player to start the game the way they usually do.");
    }

    static string? RunningMo2Exe()
    {
        foreach (var p in Process.GetProcessesByName("ModOrganizer"))
        {
            try { if (p.MainModule?.FileName is string f) return f; } catch (Exception) { }
        }
        return null;
    }

    // ModOrganizer.ini lists executables as customExecutables\N\binary and \N\title.
    static string? Mo2SkseTitle(string mo2)
    {
        var ini = Path.Combine(mo2, "ModOrganizer.ini");
        if (!File.Exists(ini)) return null;
        var binaries = new Dictionary<string, string>();
        var titles = new Dictionary<string, string>();
        foreach (var line in File.ReadLines(ini))
        {
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq];
            var parts = key.Split('\\');
            if (parts.Length != 2) continue;
            if (parts[1] == "binary") binaries[parts[0]] = line[(eq + 1)..];
            else if (parts[1] == "title") titles[parts[0]] = line[(eq + 1)..];
        }
        var n = binaries.FirstOrDefault(b => b.Value.EndsWith("skse64_loader.exe", StringComparison.OrdinalIgnoreCase)).Key;
        return n != null && titles.TryGetValue(n, out var t) ? t : null;
    }
}
