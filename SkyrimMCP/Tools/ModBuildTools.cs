using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using SkyrimMCP.Saves;

namespace SkyrimMCP.Tools;

/// <summary>
/// Patching a mod's script as an override mod (FEAT-270..272): find the Creation Kit's Papyrus compiler, read a mod's
/// script source, compile a fixed copy with its imports worked out, and package the result as a small mod that wins over
/// the original. Everything is written under Documents\...\SkyLinkAI\Build; the game's Data folder and the original mod are
/// never changed. The first run of this chain was the wendigo update-loop fix (2026-11-20), done by hand.
/// </summary>
[McpServerToolType]
public partial class ModBuildTools : ToolBase
{
    public ModBuildTools(IPipeClient pipe) : base(pipe) { }

    internal static string BuildRoot => Path.Combine(SkyLinkSettings.Folder, "Build");

    internal sealed record CreationKit(string Folder, string Compiler, string Edition);

    const string FlagsFile = "TESV_Papyrus_Flags.flg";

    [McpServerTool]
    [Description("Find the Creation Kit and what SkyLink can do with it (FEAT-271): its Papyrus compiler, where script sources are, " +
        "whether the base game's sources are unpacked, and the build folder. Without the Creation Kit SkyLink can still diagnose " +
        "scripts but not compile fixes: say what installing it would unlock and how (it's free), and never install it yourself. " +
        "Read-only.")]
    public Task<object> GetCreationKit()
    {
        var ck = FindCreationKit(out var searched);
        var sources = BaseSourceFolders(ck);
        var vanilla = VanillaState(ck);
        var flags = FindFlags(sources.Select(s => s.Path));
        object result = ck == null
            ? new
            {
                found = false,
                searched,
                unlocks = Unlocks,
                howToInstall = "Skyrim Special Edition owners get the Creation Kit free: in Steam it's listed under Tools as " +
                    "\"Skyrim Special Edition: Creation Kit\" (install it into the game folder it offers). The older Skyrim (2011) " +
                    "Creation Kit also works for compiling scripts. If it is somewhere SkyLink doesn't look, set its folder with " +
                    "SetSkyLinkSetting creationKitPath.",
                note = "Nothing is installed or changed by SkyLink; the player installs it.",
            }
            : new
            {
                found = true,
                folder = ck.Folder,
                compiler = ck.Compiler,
                edition = ck.Edition,
                compilesForSE = true,
                editionNote = ck.Edition == "LE"
                    ? "This is the 2011 Skyrim Creation Kit. Its compiler produces the same .pex format Skyrim SE loads, so it works for script fixes."
                    : null,
                sourceFolders = sources.Select(s => new { path = s.Path, what = s.What, scripts = CountPsc(s.Path) }),
                vanillaSources = vanilla,
                flagsFile = flags,
                buildFolder = BuildRoot,
                unlocks = Unlocks,
                next = vanilla.ready ? null : "Run PrepareScriptSources to unpack the base game's script sources (compiling needs them).",
            };
        return Task.FromResult(result);
    }

    static readonly string[] Unlocks =
    [
        "Fix a mod's script and compile it (CompileScript), packaged as a small override mod (PackageOverrideMod)",
        "Stop a runaway script for good, after StopRunaway stops it for the session",
        "Later: build small plugins and followers from inside the game",
    ];

    [McpServerTool]
    [Description("Unpack the base game's Papyrus sources from the Creation Kit's archive (Scripts.zip for Skyrim SE, Scripts.rar for " +
        "the 2011 Creation Kit) into SkyLink's build folder, so CompileScript can resolve vanilla types (Actor, Quest...). Never " +
        "writes into the game folder. dryRun (default true) says what would be unpacked where.")]
    public async Task<object> PrepareScriptSources(bool dryRun = true)
    {
        var ck = FindCreationKit(out _);
        if (ck == null) return new { error = "No Creation Kit found. GetCreationKit explains how to install it." };
        var state = VanillaState(ck);
        if (state.ready) return new { alreadyReady = true, folder = state.folder };
        if (state.archive == null)
            return new { error = "No script source archive found (Data\\Scripts.zip or Data\\Scripts.rar). Start the Creation Kit once: it offers to unpack them." };
        var target = VanillaFolder(ck);
        if (dryRun) return new { dryRun = true, archive = state.archive, unpackTo = target };

        Directory.CreateDirectory(target);
        // Windows' own tar (bsdtar) reads both zip and RAR, so nothing extra has to be installed.
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        var run = await RunProcess(tar, $"-x -f \"{state.archive}\" -C \"{target}\"", target, TimeSpan.FromMinutes(5));
        var after = VanillaState(ck);
        return new
        {
            unpacked = after.ready,
            folder = after.folder ?? target,
            scripts = after.folder != null ? CountPsc(after.folder) : 0,
            error = after.ready ? null : $"tar exited {run.ExitCode}: {Tail(run.Output, 600)}",
        };
    }

    [McpServerTool]
    [Description("Read a script's Papyrus source (.psc) by name, from loose files in the Data folder, MO2 mods (highest priority " +
        "first) and the unpacked base-game sources. Says which folder (mod) it came from, and whether only the compiled .pex exists " +
        "(many mods ship no source; SkyLink has no decompiler). Read-only. maxChars caps the text returned.")]
    public Task<object> GetScriptSource(string scriptName, int maxChars = 20000)
    {
        var name = Path.GetFileNameWithoutExtension(scriptName.Trim());
        var ck = FindCreationKit(out _);
        foreach (var folder in AllSourceFolders(ck))
        {
            var path = Path.Combine(folder, name + ".psc");
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            return Task.FromResult<object>(new
            {
                script = name,
                path,
                from = DescribeFolder(folder),
                lines = text.Count(c => c == '\n') + 1,
                truncated = text.Length > maxChars,
                source = text.Length > maxChars ? text[..maxChars] : text,
            });
        }
        // No source: say whether the game has the compiled script at all.
        var compiled = SkyLinkSettings.GameFileFolders().Select(f => Path.Combine(f, "Scripts", name + ".pex")).FirstOrDefault(File.Exists);
        return Task.FromResult<object>(new
        {
            script = name,
            found = false,
            compiledOnly = compiled,
            note = compiled != null
                ? "Only the compiled script is installed loose; the mod ships no source. It may be inside the mod's BSA, or on the mod's page."
                : "No loose source or compiled file. The script may be packed in a BSA, or the name may be wrong (StackReport shows script names).",
        });
    }

    [McpServerTool]
    [Description("Compile a Papyrus script with the Creation Kit's compiler (FEAT-272). Give the script's full source in 'source' " +
        "(or a .psc path in 'sourcePath'); it is saved in the build project's Source\\Scripts and compiled to its Scripts folder. " +
        "Imports are worked out automatically: the project, 'imports' (extra folders, ';'-separated), the sources of mod scripts " +
        "it refers to (found in MO2 mods and copied in), the Data folder's sources and the base game's. Returns the compiler's " +
        "errors with line numbers. project defaults to the script name and groups the files PackageOverrideMod packs. " +
        "Never touches the game's files.")]
    public async Task<object> CompileScript(string scriptName, string? source = null, string? sourcePath = null, string? project = null, string? imports = null)
    {
        var name = Path.GetFileNameWithoutExtension(scriptName.Trim());
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return new { error = "scriptName isn't a valid script name" };
        var ck = FindCreationKit(out _);
        if (ck == null) return new { error = "No Creation Kit found, so scripts can't be compiled. GetCreationKit explains how to install it." };

        var projectName = SafeName(project ?? name);
        var dir = Path.Combine(BuildRoot, projectName);
        var srcDir = Path.Combine(dir, "Source", "Scripts");
        var outDir = Path.Combine(dir, "Scripts");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(outDir);
        var psc = Path.Combine(srcDir, name + ".psc");
        if (source != null) await File.WriteAllTextAsync(psc, source);
        else if (sourcePath != null)
        {
            if (!File.Exists(sourcePath)) return new { error = $"sourcePath not found: {sourcePath}" };
            File.Copy(sourcePath, psc, overwrite: true);
        }
        else if (!File.Exists(psc)) return new { error = $"No source given and none in {srcDir}. Pass source or sourcePath." };

        var text = await File.ReadAllTextAsync(psc);
        var header = ScriptHeader().Match(text);
        if (header.Success && !header.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            return new { error = $"The source says Scriptname {header.Groups["name"].Value}, but scriptName is {name}: the compiler needs them to match." };

        var extra = (imports ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(Directory.Exists).ToList();
        var baseFolders = BaseSourceFolders(ck).Select(s => s.Path).ToList();
        var depsDir = Path.Combine(dir, "deps");
        var staged = StageModDependencies(psc, srcDir, extra.Concat(baseFolders).ToList(), depsDir);

        var importList = new List<string> { srcDir };
        importList.AddRange(extra);
        if (staged.Count > 0) importList.Add(depsDir);
        importList.AddRange(baseFolders);
        importList = importList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var flags = FindFlags(importList);
        if (flags == null)
            return new { error = $"{FlagsFile} wasn't found in any source folder. Run PrepareScriptSources first (it comes with the base game's sources).", importsTried = importList };

        var before = File.Exists(Path.Combine(outDir, name + ".pex")) ? File.GetLastWriteTimeUtc(Path.Combine(outDir, name + ".pex")) : DateTime.MinValue;
        var args = $"\"{psc}\" -f=\"{flags}\" -i=\"{string.Join(';', importList)}\" -o=\"{outDir}\"";
        var run = await RunProcess(ck.Compiler, args, Path.GetDirectoryName(ck.Compiler)!, TimeSpan.FromMinutes(2));
        var pex = Path.Combine(outDir, name + ".pex");
        var built = File.Exists(pex) && File.GetLastWriteTimeUtc(pex) > before;

        var errors = new List<object>();
        var warnings = new List<string>();
        foreach (var line in run.Output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var m = CompilerMessage().Match(line);
            if (m.Success)
                errors.Add(new { file = Path.GetFileName(m.Groups["file"].Value), line = int.Parse(m.Groups["line"].Value), column = int.Parse(m.Groups["col"].Value), message = m.Groups["msg"].Value.Trim() });
            else if (line.Contains("warning", StringComparison.OrdinalIgnoreCase)) warnings.Add(line.Trim());
        }
        return new
        {
            compiled = built && run.ExitCode == 0,
            pex = built ? pex : null,
            project = projectName,
            projectFolder = dir,
            errors = errors.Take(30),
            moreErrors = Math.Max(0, errors.Count - 30),
            warnings = warnings.Take(10),
            dependenciesCopied = staged,
            imports = importList,
            compilerExit = run.ExitCode,
            output = built ? null : Tail(run.Output, 1500),
            timedOut = run.TimedOut,
        };
    }

    [McpServerTool]
    [Description("Package a build project as a small override mod (FEAT-270): its compiled scripts (Scripts\\*.pex), their sources " +
        "(Source\\Scripts) and any plugin in the project folder, with a readme naming what it overrides, zipped in the Data layout " +
        "so Vortex and MO2 install it without questions. overrides: the mod it patches (named in the readme; the patch must win " +
        "file conflicts with it). installToMo2 also copies it into the MO2 instance's mods folder and enables it at top priority " +
        "(MO2 must be closed; modlist.txt is backed up first). Vortex: the player installs the zip (Install From File) and lets it " +
        "win over the original. dryRun (default true) lists what would be packed.")]
    public Task<object> PackageOverrideMod(string project, string? overrides = null, string? description = null, bool installToMo2 = false, bool dryRun = true)
    {
        var projectName = SafeName(project);
        var dir = Path.Combine(BuildRoot, projectName);
        if (!Directory.Exists(dir)) return Task.FromResult<object>(new { error = $"No build project '{projectName}' in {BuildRoot}. CompileScript creates one." });

        // Data-relative path -> source file.
        var files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddAll(string sub, string pattern)
        {
            var from = Path.Combine(dir, sub);
            if (!Directory.Exists(from)) return;
            foreach (var f in Directory.EnumerateFiles(from, pattern)) files[Path.Combine(sub, Path.GetFileName(f))] = f;
        }
        AddAll("Scripts", "*.pex");
        AddAll(Path.Combine("Source", "Scripts"), "*.psc");
        foreach (var ext in new[] { "*.esp", "*.esl", "*.esm" })
            foreach (var f in Directory.EnumerateFiles(dir, ext)) files[Path.GetFileName(f)] = f;
        if (!files.Keys.Any(k => k.EndsWith(".pex", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".esp") || k.EndsWith(".esl") || k.EndsWith(".esm")))
            return Task.FromResult<object>(new { error = "Nothing to package: compile a script (CompileScript) or create a plugin first." });

        var readmeName = projectName + " - README.txt";
        var readme = new StringBuilder()
            .AppendLine(projectName)
            .AppendLine(new string('=', projectName.Length))
            .AppendLine()
            .AppendLine(description ?? "A patch built with SkyLink AI.")
            .AppendLine()
            .AppendLine(overrides != null ? $"Overrides: {overrides}. Install it after that mod and let this one win the file conflicts." : "")
            .AppendLine("Contents:")
            .AppendJoin(Environment.NewLine, files.Keys.Select(k => "  " + k)).AppendLine()
            .AppendLine()
            .AppendLine("To undo: uninstall or disable this mod; the original mod's files are untouched.")
            .ToString();

        var zip = Path.Combine(dir, projectName + ".zip");
        var (mo2, profile) = SkyLinkSettings.Mo2Instance();
        if (dryRun)
            return Task.FromResult<object>(new { dryRun = true, zip, files = files.Keys, readme = readmeName, installToMo2 = installToMo2 ? (mo2 != null ? Path.Combine(mo2, "mods", projectName) : "no MO2 instance found") : null });

        if (File.Exists(zip)) File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            // Entry names with '/' so every extractor reads the folders (see build_release.ps1 for the '\' trouble).
            foreach (var (rel, src) in files) archive.CreateEntryFromFile(src, rel.Replace('\\', '/'));
            using var w = new StreamWriter(archive.CreateEntry(readmeName).Open());
            w.Write(readme);
        }

        object? mo2Result = null;
        if (installToMo2)
        {
            if (mo2 == null) mo2Result = new { installed = false, reason = "No MO2 instance found for this game (set mo2Path with SetSkyLinkSetting)." };
            else if (Process.GetProcessesByName("ModOrganizer").Length > 0)
                mo2Result = new { installed = false, reason = "MO2 is running: it rewrites modlist.txt when it closes. Close MO2 and run this again, or install the zip from MO2." };
            else mo2Result = InstallToMo2(mo2, profile, projectName, files, readmeName, readme);
        }

        return Task.FromResult<object>(new
        {
            zip,
            files = files.Keys,
            overrides,
            mo2 = mo2Result,
            howToInstall = installToMo2 ? null :
                "Vortex: Mods > Install From File, pick the zip, enable it and set it to load after the original (Vortex asks when files " +
                "conflict). MO2: Install a new mod from an archive, then give it a higher priority than the original. Then restart the game.",
        });
    }

    static object InstallToMo2(string mo2, string? profile, string name, SortedDictionary<string, string> files, string readmeName, string readme)
    {
        var target = Path.Combine(mo2, "mods", name);
        foreach (var (rel, src) in files)
        {
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
        }
        File.WriteAllText(Path.Combine(target, readmeName), readme);

        if (profile == null)
        {
            var all = Directory.Exists(Path.Combine(mo2, "profiles")) ? Directory.GetDirectories(Path.Combine(mo2, "profiles")) : [];
            profile = all.Length == 1 ? Path.GetFileName(all[0]) : "Default";
        }
        var modlist = Path.Combine(mo2, "profiles", profile, "modlist.txt");
        if (!File.Exists(modlist)) return new { installed = true, folder = target, enabled = false, reason = $"{modlist} not found: enable it in MO2." };
        var lines = File.ReadAllLines(modlist).ToList();
        var backup = modlist + $".skylink-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        File.Copy(modlist, backup);
        lines.RemoveAll(l => l.Length > 1 && l[1..].Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        // modlist.txt runs from highest priority to lowest, after any comment lines: top priority wins file conflicts.
        var at = lines.FindIndex(l => !l.StartsWith('#'));
        lines.Insert(at < 0 ? lines.Count : at, "+" + name);
        File.WriteAllLines(modlist, lines);
        return new { installed = true, folder = target, enabled = true, priority = "highest", modlist, backup };
    }

    // --- Finding the Creation Kit and sources ---

    internal static CreationKit? FindCreationKit(out List<string> searched)
    {
        searched = [];
        var candidates = new List<string>();
        var settings = SkyLinkSettings.Load();
        if (settings["creationKitPath"] is JsonValue v && v.TryGetValue<string>(out var configured) && !string.IsNullOrWhiteSpace(configured))
            candidates.Add(configured.Trim());
        if (SkyLinkSettings.SkyrimPath().Path is string game) candidates.Add(game);
        foreach (var lib in SteamLibraries())
        {
            candidates.Add(Path.Combine(lib, "steamapps", "common", "Skyrim Special Edition"));
            candidates.Add(Path.Combine(lib, "steamapps", "common", "Skyrim"));
        }
        foreach (var folder in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            searched.Add(folder);
            var compiler = Path.Combine(folder, "Papyrus Compiler", "PapyrusCompiler.exe");
            if (!File.Exists(compiler)) continue;
            var edition = File.Exists(Path.Combine(folder, "SkyrimSE.exe")) || folder.Contains("Special Edition", StringComparison.OrdinalIgnoreCase) ? "SE" : "LE";
            return new CreationKit(folder, compiler, edition);
        }
        return null;
    }

    static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string p) roots.Add(p.Replace('/', '\\'));
            }
            catch (Exception) { /* no Steam */ }
        }
        roots.Add(@"C:\Program Files (x86)\Steam");
        var libs = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            libs.Add(root);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf))) libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        return libs.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record SourceFolder(string Path, string What);

    /// <summary>Folders always passed to the compiler whole, highest priority first.</summary>
    internal static List<SourceFolder> BaseSourceFolders(CreationKit? ck)
    {
        var list = new List<SourceFolder>();
        if (SkyLinkSettings.DataPath() is string data)
        {
            // SE's Creation Kit and newer mods use Source\Scripts; SKSE and older mods use Scripts\Source.
            list.Add(new(Path.Combine(data, "Source", "Scripts"), "Data\\Source\\Scripts (installed mods' sources)"));
            list.Add(new(Path.Combine(data, "Scripts", "Source"), "Data\\Scripts\\Source (SKSE and older mods' sources)"));
        }
        if (ck != null)
        {
            list.Add(new(Path.Combine(ck.Folder, "Data", "Source", "Scripts"), "Creation Kit sources"));
            list.Add(new(Path.Combine(ck.Folder, "Data", "Scripts", "Source"), "Creation Kit sources"));
            if (VanillaState(ck).folder is string vanilla) list.Add(new(vanilla, "base game sources (unpacked by SkyLink)"));
        }
        return list.Where(s => Directory.Exists(s.Path)).GroupBy(s => Path.GetFullPath(s.Path), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>Every folder a .psc may be in, highest priority first: MO2 overwrite and mods, then the base folders.</summary>
    static List<string> AllSourceFolders(CreationKit? ck)
    {
        var list = new List<string>();
        foreach (var f in Enumerable.Reverse(SkyLinkSettings.GameFileFolders()))
        {
            list.Add(Path.Combine(f, "Source", "Scripts"));
            list.Add(Path.Combine(f, "Scripts", "Source"));
        }
        list.AddRange(BaseSourceFolders(ck).Select(s => s.Path));
        return list.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string VanillaFolder(CreationKit ck) => Path.Combine(BuildRoot, "vanilla-sources-" + ck.Edition);

    static (bool ready, string? folder, string? archive) VanillaState(CreationKit? ck)
    {
        if (ck == null) return (false, null, null);
        // Already unpacked by the Creation Kit itself, or by PrepareScriptSources.
        foreach (var root in new[] { Path.Combine(ck.Folder, "Data", "Source", "Scripts"), Path.Combine(ck.Folder, "Data", "Scripts", "Source"), VanillaFolder(ck) })
        {
            var found = FolderWith(root, "Actor.psc", depth: 3);
            if (found != null && File.Exists(Path.Combine(found, "Quest.psc"))) return (true, found, null);
        }
        var archive = new[] { Path.Combine(ck.Folder, "Data", "Scripts.zip"), Path.Combine(ck.Folder, "Data", "Scripts.rar") }.FirstOrDefault(File.Exists);
        return (false, null, archive);
    }

    static string? FolderWith(string root, string file, int depth)
    {
        if (!Directory.Exists(root)) return null;
        if (File.Exists(Path.Combine(root, file))) return root;
        if (depth == 0) return null;
        foreach (var sub in Directory.EnumerateDirectories(root))
            if (FolderWith(sub, file, depth - 1) is string hit) return hit;
        return null;
    }

    static string? FindFlags(IEnumerable<string> folders) =>
        folders.Select(f => Path.Combine(f, FlagsFile)).FirstOrDefault(File.Exists);

    /// <summary>
    /// MO2 keeps each mod's sources in its own folder, which the compiler can't take whole (hundreds of folders overflow the
    /// command line). Copy in only the mod scripts this one names, and theirs in turn. Matching every identifier against
    /// script names over-collects a little (a variable named like a script), which costs nothing.
    /// </summary>
    static List<string> StageModDependencies(string psc, string projectSrc, List<string> passedWhole, string depsDir)
    {
        var modFolders = new List<string>();
        foreach (var f in Enumerable.Reverse(SkyLinkSettings.GameFileFolders()))
        {
            modFolders.Add(Path.Combine(f, "Source", "Scripts"));
            modFolders.Add(Path.Combine(f, "Scripts", "Source"));
        }
        var whole = new HashSet<string>(passedWhole.Select(p => Path.GetFullPath(p)), StringComparer.OrdinalIgnoreCase);
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in modFolders.Where(Directory.Exists).Where(f => !whole.Contains(Path.GetFullPath(f))))
            foreach (var file in Directory.EnumerateFiles(folder, "*.psc"))
                index.TryAdd(Path.GetFileNameWithoutExtension(file), file);   // highest priority first wins
        var staged = new List<string>();
        if (index.Count == 0) return staged;

        bool Covered(string script) =>
            File.Exists(Path.Combine(projectSrc, script + ".psc")) || passedWhole.Any(p => File.Exists(Path.Combine(p, script + ".psc")));

        var queue = new Queue<string>([psc]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0 && staged.Count < 500)
        {
            foreach (Match m in Identifier().Matches(File.ReadAllText(queue.Dequeue())))
            {
                var word = m.Value;
                if (!seen.Add(word) || !index.TryGetValue(word, out var file) || Covered(word)) continue;
                Directory.CreateDirectory(depsDir);
                var dest = Path.Combine(depsDir, word + ".psc");
                File.Copy(file, dest, overwrite: true);
                staged.Add($"{word} (from {DescribeFolder(Path.GetDirectoryName(file)!)})");
                queue.Enqueue(dest);
            }
        }
        return staged;
    }

    static string DescribeFolder(string folder)
    {
        var (mo2, _) = SkyLinkSettings.Mo2Instance();
        if (mo2 != null)
        {
            var mods = Path.Combine(mo2, "mods") + Path.DirectorySeparatorChar;
            if (folder.StartsWith(mods, StringComparison.OrdinalIgnoreCase)) return "MO2 mod " + folder[mods.Length..].Split(Path.DirectorySeparatorChar)[0];
            if (folder.StartsWith(Path.Combine(mo2, "overwrite"), StringComparison.OrdinalIgnoreCase)) return "MO2 overwrite";
        }
        return folder;
    }

    static int CountPsc(string folder) { try { return Directory.EnumerateFiles(folder, "*.psc").Count(); } catch (IOException) { return 0; } }

    static string SafeName(string s) => string.Concat(s.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    static string Tail(string s, int n) => s.Length <= n ? s : "..." + s[^n..];

    internal sealed record ProcessResult(int ExitCode, string Output, bool TimedOut);

    internal static async Task<ProcessResult> RunProcess(string exe, string args, string workingDir, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        var timedOut = false;
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { timedOut = true; try { proc.Kill(entireProcessTree: true); } catch (Exception) { } }
        return new ProcessResult(timedOut ? -1 : proc.ExitCode, (await stdout) + (await stderr), timedOut);
    }

    [GeneratedRegex(@"^\s*Scriptname\s+(?<name>\w+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ScriptHeader();

    [GeneratedRegex(@"^(?<file>.+?\.psc)\((?<line>\d+),(?<col>\d+)\):\s*(?<msg>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CompilerMessage();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"""path""\s+""([^""]+)""")]
    private static partial Regex VdfPath();
}
