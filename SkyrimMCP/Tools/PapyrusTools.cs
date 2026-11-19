using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// MCP Tools for Papyrus VM interaction — discover and call any Papyrus function from any installed mod
/// </summary>
[McpServerToolType]
public class PapyrusTools : ToolBase
{
    public PapyrusTools(IPipeClient pipe) : base(pipe) { }

    [McpServerTool]
    [Description("Get the full Papyrus function catalog — all native functions from all installed mods. " +
        "Scans .psc source files on first call (takes 2-3 seconds), caches for subsequent calls. " +
        "Returns scripts organized with function names, parameter types, return types, and global flags. " +
        "Use this to discover what functions are available from installed mods.")]
    public async Task<object> GetPapyrusCatalog()
    {
        var data = await _pipe.SendRequestAsync("get_papyrus_catalog");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Force rescan of Papyrus source files. Use after installing new mods to update the catalog.")]
    public async Task<object> ScanPapyrusSources()
    {
        var data = await _pipe.SendRequestAsync("scan_papyrus_sources");
        await NotifyInGame("Papyrus catalog updated");
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Get all available functions for a specific Papyrus script class (e.g., 'ConsoleUtil', 'PapyrusUtil', 'JArray'). " +
        "Shows function names, parameter types, return types. Use GetPapyrusCatalog first to see available script classes. " +
        "Classes missing from the catalog (scripts packed in a BSA, such as RaceMenu's NiOverride and CharGen) are read " +
        "from the Papyrus VM instead (source 'vm'), which works for any class the game can load.")]
    public async Task<object> GetScriptFunctions(string className)
    {
        var data = await _pipe.SendRequestAsync("get_script_functions", new JsonObject { ["className"] = className });
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Call any Papyrus function from any installed mod via the Papyrus VM. " +
        "Pass className (script class), functionName, and args (JSON array of values). " +
        "Supports string, int, float and bool arguments, and (1.3+) forms and references: pass a FormID string, \"player\", " +
        "or null for None to an Actor, ObjectReference, Form or other object parameter. Arrays can't be passed. " +
        "Returns the function's return value: arrays come back as lists and forms as FormIDs (1.3+). " +
        "Example: SKSE's WornObject.SetDisplayName(akActor, handSlot, slotMask, name, force) renames a worn item, " +
        "WornObject.SetItemHealthPercent sets tempering (1.0 = none, 1.6 = legendary). " +
        "Only GLOBAL functions can be called. Before sending, the plugin checks the installed script: an unknown class, " +
        "a missing function, a member function, or a wrong argument count or type is rejected with an error and the " +
        "function's real signature, so read the error instead of guessing other names. " +
        "WORKFLOW: Use GetScriptFunctions(className) first to see available functions and parameter types. " +
        "The catalog comes from .psc source files, which can be newer or older than the installed script; the plugin's check is authoritative. " +
        "KEY PLUGINS AVAILABLE: " +
        "ConsoleUtil (6 funcs): ExecuteCommand, ReadMessage, GetVersion, SetSelectedReference. " +
        "PapyrusUtil (21 funcs): MorphManager for face/body morphs, array utilities, file I/O. " +
        "StorageUtil (171 funcs): Persistent key-value storage per form — SetIntValue, GetStringValue, etc. " +
        "JsonUtil (123 funcs): Read/write JSON files — Load, Save, SetStringValue, GetIntValue. " +
        "JContainers (431 funcs): Advanced data structures — JArray, JMap, JValue, JDB for persistent databases. " +
        "Experience (13 funcs): AddExperience, GetSkillCap, ShowNotification for XP-based leveling. " +
        "OActor (40 funcs): PlayExpression, Undress/Redress, EquipObject for character interaction. " +
        "IED (112 funcs): Equipment display management — show weapons/shields on character model. " +
        "DbSkseFunctions (159 funcs): Extended utilities — clipboard, save management, form queries. " +
        "Input (8 funcs): IsKeyPressed, TapKey, HoldKey for input simulation. " +
        "Camera (6 funcs): GetCameraState, SetWorldFieldOfView for camera control. " +
        "EXAMPLES: " +
        "Get ConsoleUtil version: className='ConsoleUtil', functionName='GetVersion', args=[] " +
        "Execute console command: className='ConsoleUtil', functionName='ExecuteCommand', args=['tgm'] " +
        "Add experience: className='Experience', functionName='AddExperience', args=[100, true] " +
        "LIMITATION: Functions taking Form, Actor or array arguments cannot be called yet (they are rejected, not sent). " +
        "target: to call a member (non-global) function on the script attached to a form, the form's ID or editor ID, e.g. " +
        "className='RaceMenu', functionName='SaveTints', target='RaceMenu' (RaceMenu's quest).")]
    public async Task<object> CallPapyrusFunction(string className, string functionName, string? args = null, string? target = null)
    {
        var parms = new JsonObject
        {
            ["className"] = className,
            ["functionName"] = functionName
        };
        if (!string.IsNullOrWhiteSpace(target)) parms["target"] = target.Trim();

        if (!string.IsNullOrEmpty(args))
        {
            try
            {
                parms["args"] = JsonNode.Parse(args);
            }
            catch
            {
                // Treat as single string argument
                var arr = new JsonArray();
                arr.Add(args);
                parms["args"] = arr;
            }
        }
        else
        {
            parms["args"] = new JsonArray();
        }

        var data = await _pipe.SendRequestAsync("call_papyrus_function", parms);
        return DeserializeResponse(data);
    }

    [McpServerTool]
    [Description("Find runaway Papyrus script stacks, the usual cause of script lag and FPS loss in a heavy load order (FEAT-268). " +
        "Samples the live script engine several times (samples, default 6, intervalMs apart, default 1000) and reports, per " +
        "entry point (the mod's event handler where each stack started): how many stacks, whether the count is growing, and the " +
        "plugins that own the objects it runs on. Each pile-up is classed: burst (one peak of 20+ that drains, e.g. after a load), " +
        "periodic (rises to 20+ again and again: usually a mod sweeping nearby NPCs) or runaway (stays high or keeps growing: " +
        "a loop that never finishes). Samples during a loading screen are skipped, not fatal. Overstress and waiting function " +
        "messages are shown beside them. Plugins read 'defined in X, last changed by Y' " +
        "when another mod overrides the object (the overriding mod is often the cause). Read-only: nothing in the game changes. " +
        "Clearing stacks needs a save edit and reload (planned: CleanAndReload); until then the fix is the mod itself " +
        "(update, configure, or stop the feature that loops).")]
    public async Task<object> StackReport(int samples = 6, int intervalMs = 1000, int top = 12)
    {
        samples = Math.Clamp(samples, 2, 20);
        intervalMs = Math.Clamp(intervalMs, 200, 5000);
        var perEntry = new Dictionary<string, List<int>>();
        var owners = new Dictionary<string, JsonNode?>();
        var running = new List<int>();
        var waitingMessages = new List<int>();
        var overstressed = 0;
        var skipped = 0;
        var taken = 0;
        for (var i = 0; i < samples; i++)
        {
            if (i > 0) await Task.Delay(intervalMs);
            JsonNode? data;
            // FEAT-268: during a loading screen the game thread doesn't answer; skip that sample and keep going (the load is
            // exactly the window worth catching).
            try { data = await _pipe.SendRequestAsync("get_script_stats", new JsonObject { ["top"] = 200 }); }
            catch (Exception) { skipped++; continue; }
            if (data?["error"] != null) { skipped++; continue; }
            taken++;
            running.Add(data?["runningStacks"]?.GetValue<int>() ?? 0);
            waitingMessages.Add(data?["waitingFunctionMessages"]?.GetValue<int>() ?? 0);
            if (data?["overstressed"]?.GetValue<bool>() == true) overstressed++;
            var seen = new HashSet<string>();
            foreach (var e in (data?["topEntryPoints"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var name = e["name"]?.ToString() ?? "?";
                if (!perEntry.TryGetValue(name, out var counts)) perEntry[name] = counts = Enumerable.Repeat(0, taken - 1).ToList();
                counts.Add(e["stacks"]?.GetValue<int>() ?? 0);
                seen.Add(name);
            }
            foreach (var (name, counts) in perEntry) if (!seen.Contains(name)) counts.Add(0);
            if (data?["entryPointOwners"] is JsonObject o)
                foreach (var (name, list) in o)
                {
                    // Keep the owners from the sample with the most stacks for that handler.
                    var total = (list as JsonArray ?? new JsonArray()).Sum(x => x?["stacks"]?.GetValue<int>() ?? 0);
                    if (!owners.TryGetValue(name, out var prev) || total > (prev as JsonArray ?? new JsonArray()).Sum(x => x?["stacks"]?.GetValue<int>() ?? 0))
                        owners[name] = list?.DeepClone();
                }
        }
        if (taken == 0)
            return new { error = "No sample could be taken: the game didn't answer (a loading screen the whole time, or paused). Try again once it has loaded." };

        var rows = perEntry.Select(kv =>
        {
            var c = kv.Value;
            var first = c.First();
            var last = c.Last();
            var max = c.Max();
            var rising = c.Zip(c.Skip(1), (a, b) => b - a).Count(d => d > 0);
            var falling = c.Zip(c.Skip(1), (a, b) => b - a).Count(d => d < 0);
            // Growing: up by at least 2 and rising in most intervals (3 -> 4 over six samples is jitter).
            var steps = Math.Max(1, c.Count - 1);
            var growing = last - first >= 2 && rising >= Math.Ceiling(steps * 0.6) && rising > falling;
            // FEAT-268: what kind of pile-up. runaway: stays high or keeps growing. periodic: rises to 20+ more than once, draining
            // in between. burst: one big peak that drains.
            var peaks = 0;
            var armed = true;
            foreach (var v in c)
            {
                if (armed && v >= 20) { peaks++; armed = false; }
                else if (!armed && v < 10) armed = true;
            }
            string? kind = (growing && last - first >= 5) || (last >= 20 && last >= 0.75 * max) ? "runaway"
                : peaks >= 2 ? "periodic"
                : max >= 20 ? "burst"
                : null;
            var runaway = kind != null;
            // Headline: every real plugin among the owners; "(created in game)" only when there is nothing else.
            var ownerList = (owners.TryGetValue(kv.Key, out var ow) ? ow as JsonArray : null) ?? new JsonArray();
            var named = ownerList.Select(x => x?["plugin"]?.ToString()).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList();
            var real = named.Where(n => !n.StartsWith("(")).ToList();
            var plugin = real.Count > 0 ? string.Join(", ", real) : named.FirstOrDefault();
            return new { entry = kv.Key, last, max, first, growing, kind, runaway, plugin, owners = owners.TryGetValue(kv.Key, out var ow2) ? ow2 : null };
        }).OrderByDescending(r => r.runaway).ThenByDescending(r => r.max).Take(Math.Clamp(top, 1, 50)).ToList();

        var runaways = rows.Where(r => r.runaway).ToList();
        static string Who(string? plugin) => plugin == null ? "" : plugin.StartsWith("(") ? " " + plugin : $" ({plugin})";
        var summary = runaways.Count == 0
            ? "No pile-ups: no handler reached 20 copies or kept growing."
            : string.Join(" ", runaways.OrderBy(r => r.kind == "runaway" ? 0 : r.kind == "periodic" ? 1 : 2).Select(r => r.kind switch
            {
                "runaway" => $"{r.entry}{Who(r.plugin)} is a runaway: {r.last} copies running now{(r.growing ? $", up from {r.first}" : "")} (peak {r.max}).",
                "periodic" => $"{r.entry}{Who(r.plugin)} rises to {r.max} copies repeatedly and drains in between (periodic: a sweep over nearby objects?).",
                _ => $"{r.entry}{Who(r.plugin)} peaked at {r.max} copies and drained back to {r.last} (a one-time burst).",
            }));
        return new
        {
            summary,
            runningStacks = new { first = running.First(), last = running.Last(), max = running.Max() },
            waitingFunctionMessages = new { last = waitingMessages.Last(), max = waitingMessages.Max() },
            overstressedSamples = $"{overstressed} of {samples}",
            entryPoints = rows,
            sampled = $"{taken} samples, {intervalMs} ms apart" + (skipped > 0 ? $"; {skipped} skipped (loading screen or no answer)" : ""),
            note = "Some waiting stacks are normal (Utility.Wait, update loops). A handler with dozens or hundreds of copies, or one " +
                   "that keeps growing, is a mod looping faster than it finishes: that eats script time every frame. Clearing them " +
                   "needs a save edit and reload; fixing the mod (update, its MCM settings, or disabling the feature) stops it coming back.",
        };
    }

    [McpServerTool]
    [Description("Papyrus script engine diagnostics, to find the mod behind script lag or a save that bloats. " +
        "Reports runningStacks (scripts active right now), how many wait on latent functions, waitingFunctionMessages, " +
        "overstressed, objectsWithScripts (game objects with scripts attached) and arrays, and the busiest scripts: topScripts (scripts on any running stack), " +
        "topEntryPoints (where each call started, usually a mod's event handler) and topCurrentFunctions. " +
        "samples (default 1, max 20) takes several snapshots intervalMs apart (default 500) and adds them up, " +
        "which shows steady load better than one snapshot. top (default 15) is how many scripts to list. " +
        "Map script names to mods with SearchForms or the player's knowledge; a script prefix usually names the mod.")]
    public async Task<object> GetScriptStats(int samples = 1, int intervalMs = 500, int top = 15)
    {
        samples = Math.Clamp(samples, 1, 20);
        intervalMs = Math.Clamp(intervalMs, 100, 5000);
        if (samples == 1)
            return DeserializeResponse(await _pipe.SendRequestAsync("get_script_stats", new JsonObject { ["top"] = top }));

        // Several snapshots: sum the per-script counts and keep min/max of the totals.
        var sums = new Dictionary<string, Dictionary<string, int>>
        {
            ["topScripts"] = new(), ["topEntryPoints"] = new(), ["topCurrentFunctions"] = new(),
        };
        var running = new List<int>();
        var waiting = new List<int>();
        var overstressed = 0;
        JsonNode? last = null;
        for (var i = 0; i < samples; i++)
        {
            if (i > 0) await Task.Delay(intervalMs);
            var data = await _pipe.SendRequestAsync("get_script_stats", new JsonObject { ["top"] = 100 });
            if (data?["error"] != null) return DeserializeResponse(data);
            last = data;
            running.Add(data?["runningStacks"]?.GetValue<int>() ?? 0);
            waiting.Add(data?["waitingFunctionMessages"]?.GetValue<int>() ?? 0);
            if (data?["overstressed"]?.GetValue<bool>() == true) overstressed++;
            foreach (var (key, sum) in sums)
            {
                if (data?[key] is not JsonArray list) continue;
                foreach (var item in list)
                {
                    var name = item?["name"]?.GetValue<string>() ?? "?";
                    sum[name] = sum.GetValueOrDefault(name) + (item?["stacks"]?.GetValue<int>() ?? 0);
                }
            }
        }

        JsonArray Top(Dictionary<string, int> sum) => new(sum.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(Math.Clamp(top, 1, 100))
            .Select(p => (JsonNode)new JsonObject { ["name"] = p.Key, ["stacksSummed"] = p.Value, ["average"] = Math.Round((double)p.Value / samples, 1) })
            .ToArray());

        return DeserializeResponse(new JsonObject
        {
            ["samples"] = samples,
            ["intervalMs"] = intervalMs,
            ["runningStacks"] = new JsonObject { ["min"] = running.Min(), ["max"] = running.Max(), ["average"] = Math.Round(running.Average(), 1) },
            ["waitingFunctionMessages"] = new JsonObject { ["min"] = waiting.Min(), ["max"] = waiting.Max() },
            ["overstressedSamples"] = overstressed,
            ["objectsWithScripts"] = last?["objectsWithScripts"]?.DeepClone(),
            ["arrays"] = last?["arrays"]?.DeepClone(),
            ["topScripts"] = Top(sums["topScripts"]),
            ["topEntryPoints"] = Top(sums["topEntryPoints"]),
            ["topCurrentFunctions"] = Top(sums["topCurrentFunctions"]),
            ["note"] = last?["note"]?.DeepClone(),
        });
    }
}
