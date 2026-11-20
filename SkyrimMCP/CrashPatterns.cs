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
        var meshes = Regex.Matches(objects, @"""(?<m>[^""]+\.nif)""", RegexOptions.IgnoreCase)
            .Select(m => m.Groups["m"].Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        if (plugins.Count > 0) notes.Add($"Plugins named near the crash: {string.Join(", ", plugins.Select(p => p.plugin))}. The first is the strongest lead, though a base-game object can appear when a mod changed it.");
        if (meshes.Count > 0) notes.Add($"Meshes named near the crash: {string.Join(", ", meshes)}. A broken mesh crashes when its cell loads; find which mod provides it.");

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
