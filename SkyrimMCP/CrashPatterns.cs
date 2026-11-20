using System.Text.RegularExpressions;

namespace SkyrimMCP;

/// <summary>
/// Known crash signatures matched against Crash Logger SSE reports (FEAT-279), plus general reading rules that name what a
/// report points at. Specific signatures come from crashes seen on real setups; each says where it came from and how sure
/// the match is, so the AI can present it as a lead rather than a verdict. Add new ones as crashes are worked out.
/// Address Library IDs ("107719+0x21A") are used rather than raw offsets because they stay the same across game updates
/// of one edition (they differ between SE 1.5.97 and AE).
/// </summary>
public static class CrashPatterns
{
    public sealed record Pattern(string Id, string Title, string[] AllOf, string Cause, string Fix, string Confidence, string Source);

    public static readonly Pattern[] Known =
    [
        new("inner-warmth-scroll", "Scroll of Inner Warmth in the barter menu",
            [@"Scroll of Inner Warmth", @"BarterMenu"],
            "Viewing or selling the Scroll of Inner Warmth from a Reducks synthesis patch reads a missing effect.",
            "Install the InnerWarmthScrollFix from the Reducks patch author, or rebuild the synthesis patch; avoid the scroll in shops until then.",
            "high", "Seen 2026-11-14 (DOMAINREDUCKSSynthPatch.esp, Skyrim 1.7.104); fix named by the patch author."),
        new("community-shaders-snow", "Community Shaders, snow and fresnel features",
            [@"CommunityShaders\.dll\+", @"(SnowDeformation|VanillaFresnel)\.cpp"],
            "Community Shaders crashed while drawing snow-covered or fresnel-lit geometry. Seen together with Better Dynamic Snow.",
            "Update Community Shaders and its snow-related add-ons together; if it keeps happening, turn off the snow feature in the Community Shaders menu or try without Better Dynamic Snow.",
            "medium", "Seen 2026-11-20 on Skyrim 1.7.104."),
        new("ostim-scene-menu", "OStim scene menu",
            [@"OStim\.dll\+", @"SceneMenu\.cpp"],
            "OStim's scene menu touched a scene node that was already gone, typically when something else interrupted or changed the scene.",
            "Update OStim; avoid stopping idles or moving actors while a scene runs (SkyLink's own tools refuse during OStim scenes).",
            "medium", "Seen 2026-11-20 on Skyrim 1.7.104."),
        new("racemenu-async-morph", "RaceMenu applying body morphs on a background thread",
            [@"skee64\.dll\+", @"(MorphFileCache::ApplyMorph|NormalApplicator::Apply|NIOVTaskUpdateSkinPartition)"],
            "RaceMenu (skee64) applies body morphs and normal maps to an actor's body and worn meshes on a worker thread; when the actor's 3D or equipment changes mid-task it reads freed data. A body-preset mod (OBody and the like) assigning morphs as NPCs load triggers it; it is timing-dependent, so a retry often loads fine.",
            "Update RaceMenu for the game version. If one NPC keeps triggering it (named in POSSIBLE RELEVANT OBJECTS), exclude that NPC or its plugin from the body-preset mod's distribution.",
            "medium", "Seen 2026-11-20 on a community report (Skyrim 1.7.104, OBody NG 5.2.1, an NPC from an added-NPC mod wearing an amulet)."),
        new("souls-map-terrain-lod", "Skyrim Souls RE's map menu updating distant terrain",
            [@"SkyrimSoulsRE\.dll\+", @"MapMenuEx", @"BGSTerrainManager"],
            "With the map menu unpaused, Skyrim Souls RE keeps the terrain manager updating; the crash came while distant terrain (LOD) models were loading through that hook.",
            "Set the map menu back to paused in Skyrim Souls RE's settings and see if these stop. Plugins named in the report (often an occlusion plugin) are likely only the last to edit the worldspace.",
            "medium", "Seen 2026-11-18 on a community report (Skyrim 1.7.104)."),
        new("mesh-material-load", "A mesh's material or textures failed to load",
            [@"107719\+0x", @"BSLightingShaderMaterial", @"BSShaderTextureSet"],
            "The game crashed loading a model's shader material: usually a broken or mismatched mesh (a .nif pointing at a bad texture set) in a mod that replaces landscape or architecture.",
            "Look for .nif paths and plugins in POSSIBLE RELEVANT OBJECTS and STACK; the cell being loaded (the crash recorder's last cell) narrows the mod down. Reinstall or remove that mesh replacer.",
            "medium", "Seen 2026-11-19 on Skyrim 1.7.104 (a city overhaul's tree mesh)."),
    ];

    static readonly string[] GraphicsDrivers = ["nvwgf2umx.dll", "nvwgf2um.dll", "atidxx64.dll", "amdxx64.dll", "igd10iumd64.dll", "d3d11.dll", "dxgi.dll"];
    static readonly string[] SystemModules = ["SkyrimSE.exe", "SkyrimVR.exe", "ntdll.dll", "KERNELBASE.dll", "kernel32.dll", "ucrtbase.dll", "VCRUNTIME140.dll", "MSVCP140.dll"];

    /// <summary>Matches and plain reading notes for a report's text.</summary>
    public static object Analyse(string text)
    {
        var matches = Known.Where(p => p.AllOf.All(rx => Regex.IsMatch(text, rx, RegexOptions.IgnoreCase)))
            .Select(p => new { p.Id, p.Title, p.Cause, p.Fix, p.Confidence, p.Source }).ToList();

        var notes = new List<string>();
        var fault = Regex.Match(text, @"Unhandled exception ""[^""]+"" at 0x[0-9A-F]+ (?<module>[^\s+]+)\+(?<offset>[0-9A-F]+)", RegexOptions.IgnoreCase);
        var module = fault.Success ? fault.Groups["module"].Value : null;
        if (module != null)
        {
            if (GraphicsDrivers.Contains(module, StringComparer.OrdinalIgnoreCase))
                notes.Add($"It crashed in the graphics driver ({module}): often running out of video memory (heavy textures, ENB or shader settings) or a driver problem. Check VRAM use and the driver version.");
            else if (!SystemModules.Contains(module, StringComparer.OrdinalIgnoreCase))
                notes.Add($"It crashed inside {module}, an SKSE plugin or other DLL: that mod is the first suspect (or something it was handed). Check for an update to it.");
            else if (module.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase) && text.Contains("None found"))
                notes.Add("It crashed in Windows' ntdll with no game objects named: usually memory damaged earlier by a plugin, so the report can't name the cause. Look at SKSE plugins added or updated recently, and at the crash recorder's last entries.");
        }

        // Plugins and meshes the report names, most mentioned first.
        var objects = Section(text, "POSSIBLE RELEVANT OBJECTS") + "\n" + Section(text, "STACK");
        var plugins = Regex.Matches(objects, @"""(?<p>[^""\\/]+\.(esp|esm|esl))""", RegexOptions.IgnoreCase)
            .Select(m => m.Groups["p"].Value).GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => new { plugin = g.Key, mentions = g.Count() }).Take(8).ToList();
        // Crash Logger cuts long strings, which left fragments ("ock\\warlockrobes_0.nif", "_0.nif"): keep full paths (with a
        // folder), and drop any that is the tail of another.
        var meshList = Regex.Matches(objects, @"""(?<m>[^""]+\.nif)""", RegexOptions.IgnoreCase)
            .Select(m => m.Groups["m"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var meshes = meshList.Where(m => m.Contains('\\') && m.Length > 12 && !meshList.Any(o => o.Length > m.Length && o.EndsWith(m, StringComparison.OrdinalIgnoreCase)))
            .Take(8).ToList();
        if (plugins.Count > 0)
        {
            // Crash Logger names the last plugin that changed an object. Patchers and occlusion plugins edit every cell or the whole
            // worldspace, so they appear next to almost any crash while the world loads without being the cause.
            static bool CatchAll(string p) => Regex.IsMatch(p, @"occlusion|synthesis|bashed patch|smashed|\bpatch\.es[mp]$|^Skyrim\.esm$|^Update\.esm$", RegexOptions.IgnoreCase);
            var leads = plugins.Where(p => !CatchAll(p.plugin)).Select(p => p.plugin).ToList();
            var catchAll = plugins.Where(p => CatchAll(p.plugin)).Select(p => p.plugin).ToList();
            if (leads.Count > 0) notes.Add($"Plugins named near the crash: {string.Join(", ", leads)}. The first is the strongest lead, though a base-game object can appear when a mod changed it.");
            if (catchAll.Count > 0) notes.Add($"{string.Join(", ", catchAll)} {(catchAll.Count == 1 ? "edits" : "edit")} the whole world or every cell, so {(catchAll.Count == 1 ? "it shows" : "they show")} up next to most world-loading crashes: probably not the cause on its own.");
        }
        if (meshes.Count > 0) notes.Add($"Meshes named near the crash: {string.Join(", ", meshes)}. A broken mesh crashes when its cell loads; find which mod provides it.");

        // Memory pressure: Windows' physical memory nearly full makes allocations fail during loads.
        var ram = Regex.Match(text, @"PHYSICAL MEMORY: (?<used>[\d.]+) GB/(?<total>[\d.]+) GB");
        if (ram.Success && double.TryParse(ram.Groups["used"].Value, System.Globalization.CultureInfo.InvariantCulture, out var used)
            && double.TryParse(ram.Groups["total"].Value, System.Globalization.CultureInfo.InvariantCulture, out var total) && total > 0 && used / total >= 0.92)
            notes.Add($"Windows' memory was almost full when it crashed ({used:0.#} of {total:0.#} GB): allocations fail more easily during loads. Close background programs, or lighten textures and the load order.");
        var vram = Regex.Match(text, @"GPU MEMORY: (?<used>[\d.]+)/(?<total>[\d.]+) GB");
        if (vram.Success && double.TryParse(vram.Groups["used"].Value, System.Globalization.CultureInfo.InvariantCulture, out var vu)
            && double.TryParse(vram.Groups["total"].Value, System.Globalization.CultureInfo.InvariantCulture, out var vt) && vt > 0 && vu / vt >= 0.92)
            notes.Add($"Video memory was almost full ({vu:0.#} of {vt:0.#} GB): texture-heavy setups crash in the driver or while loading at this point.");

        var uptime = Regex.Match(text, @"Process Uptime: (?<h>\d+):(?<m>\d+):(?<s>\d+)");
        if (uptime.Success && int.Parse(uptime.Groups["h"].Value) == 0 && int.Parse(uptime.Groups["m"].Value) < 2)
            notes.Add("The game had been running under two minutes: a crash during start-up or the first load points at load-order problems (missing masters, an SKSE plugin built for another game version) rather than gameplay.");

        return new
        {
            knownPatterns = matches,
            faultingModule = module,
            pluginsNamed = plugins,
            meshesNamed = meshes,
            notes,
            caution = "Leads, not proof: a crash report shows where the game fell over, which is often downstream of the real cause.",
        };
    }

    static string Section(string text, string name)
    {
        var start = text.IndexOf(name + ":", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        // Sections end at the next line that starts a new UPPER CASE heading.
        var rest = text[(start + name.Length + 1)..];
        var next = Regex.Match(rest, @"\n[A-Z][A-Z \(\)\[\]/]+:\s*\n");
        return next.Success ? rest[..next.Index] : rest;
    }
}
