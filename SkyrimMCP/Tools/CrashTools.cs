using System.Text.Json;
using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// Reads crash reports so the AI can troubleshoot a crash without the player copying files
/// (FEAT-217). Works with the game closed: it reads files, not the pipe.
/// </summary>
[McpServerToolType]
public class CrashTools : ToolBase
{
    public CrashTools(IPipeClient pipe) : base(pipe) { }

    // Sections most useful for finding the culprit, in the order Crash Logger SSE writes them.
    private static readonly string[] DefaultSections =
    {
        "header", "ACCESS VIOLATION ANALYSIS", "PROBABLE CALL STACK", "POSSIBLE RELEVANT OBJECTS", "STACK", "GAME STATE",
    };

    [McpServerTool]
    [Description("Read the newest Skyrim crash report (Crash Logger SSE crash-*.log, or NetScriptFramework Crash_*.txt), " +
        "so you can work out what crashed. Works while the game is closed. " +
        "index: 0 = newest (default), 1 = the one before, and so on. " +
        "sections: comma-separated Crash Logger section names, or 'all'. Default: header (exception and faulting module), " +
        "ACCESS VIOLATION ANALYSIS, PROBABLE CALL STACK, POSSIBLE RELEVANT OBJECTS, STACK and GAME STATE. " +
        "Other sections: PROCESS INFO, SYSTEM SPECS, REGISTERS, MODULES, SKSE PLUGINS, PLUGINS. " +
        "maxCharsPerSection (default 6000) trims long sections. Also lists the most recent reports with their times. " +
        "skyrimPath (optional) is the game folder, used to find NetScriptFramework reports under Data. " +
        "Look for mod names (.esp/.esm/.esl) and DLL names in the call stack and relevant objects; 'analysis' lists known " +
        "crash signatures that match (with their fix and how sure the match is) and the plugins, meshes and DLL the report names. " +
        "skylinkRecorder: SkyLink's own crash recorder (Documents\\My Games\\<game>\\SkyLinkAI\\crash-recorder*.bin), " +
        "the last things SkyLink and the game did before the session ended: AI tool calls ('>' when a request started, " +
        "'<' with ok/error and time when it returned; a '>' with no '<' after it was still running when the game died), " +
        "Papyrus calls, game events, the player's cell and location, loading screens, saves and loads. " +
        "Each session shows its start time, its last entry and whether it exited cleanly: match it to the crash report by time. " +
        "previousCrash is the last session that did not exit cleanly (kept when the next session started); lastSession is " +
        "the newest session, included when the game is not running or recorderCurrent is true. " +
        "recorderEntries (default 60, 0 = header only) is how many of the newest entries to show per session. " +
        "windowsDump: when Windows wrote a crash dump (LocalDumps) newer than the newest report, as for crashes while quitting or " +
        "returning to the main menu that Crash Logger misses, its exception, faulting module and a stack scan.")]
    public async Task<object> GetCrashLog(int index = 0, string? sections = null, int maxCharsPerSection = 6000, string? skyrimPath = null,
        int recorderEntries = 60, bool recorderCurrent = false)
    {
        // A freshly (re)started server hasn't asked the game which one it is (Steam or GOG): connecting does. Brief, since the
        // game may be hung; without an answer every game's folders are searched.
        if (SkyLinkSettings.GameDocumentsFolder == null && System.Diagnostics.Process.GetProcessesByName("SkyrimSE").Length > 0)
        {
            try { await Task.WhenAny(_pipe.SendRequestAsync("ping"), Task.Delay(3000)); } catch (Exception) { }
        }
        return Report(index, sections, maxCharsPerSection, skyrimPath, recorderEntries, recorderCurrent);
    }

    private object Report(int index, string? sections, int maxCharsPerSection, string? skyrimPath, int recorderEntries, bool recorderCurrent)
    {
        var recorder = ReadRecorder(recorderEntries, recorderCurrent);
        var logs = FindCrashLogs(skyrimPath);
        var recent = logs.Take(10).Select(f => new { file = f.FullName, written = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") }).ToArray();
        // FEAT-279: a crash during shutdown or a return to the main menu leaves no Crash Logger report, only the dump Windows
        // writes when LocalDumps is set up. Summarise the newest dump when it is newer than the newest report.
        object? windowsDump = null;
        try
        {
            // Round B: Windows keeps every game's dumps in one folder; with GOG connected, Steam's dump was shown.
            var dumps = Minidump.FindDumps();
            if (SkyLinkSettings.GameInstallFolder is { Length: > 0 } game)
                dumps = dumps.Where(d => Minidump.GameFolder(d.FullName) is not string f ||
                    string.Equals(f.TrimEnd('\\'), game.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
            if (dumps.Count > 0 && (logs.Count == 0 || dumps[0].LastWriteTime > logs[0].LastWriteTime.AddMinutes(1)))
                windowsDump = Minidump.Summarise(dumps[0].FullName);
        }
        catch (Exception ex) { windowsDump = new { error = "Couldn't read the Windows crash dump: " + ex.Message }; }
        if (logs.Count == 0)
        {
            // BUG-071: say whether a crash reporter is installed, since "no reports" only means something when one is.
            var installed = CrashReporterInstalled();
            return new
            {
                crashReports = "none",
                crashReporterInstalled = installed,
                note = installed
                    ? "No crash reports: Crash Logger is installed and hasn't written one, so the game hasn't crashed since its reports were last cleared."
                    : "No crash reports, and no crash reporter is installed: install Crash Logger SSE so crashes leave a report. Searched: " +
                      string.Join("; ", SearchFolders(skyrimPath)),
                windowsDump,
                skylinkRecorder = recorder,
            };
        }
        if (index < 0 || index >= logs.Count)
            return new { error = $"index must be 0 to {logs.Count - 1}", recent, skylinkRecorder = recorder };

        var file = logs[index];
        string text;
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return new { error = $"Could not read {file.FullName}: {ex.Message}", recent, skylinkRecorder = recorder };
        }

        var parsed = SplitSections(text);
        var wanted = string.IsNullOrWhiteSpace(sections)
            ? DefaultSections
            : sections.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
                ? parsed.Keys.ToArray()
                : sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var output = new Dictionary<string, string>();
        foreach (var name in wanted)
        {
            var key = parsed.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (key == null) continue;
            var body = parsed[key];
            if (maxCharsPerSection > 0 && body.Length > maxCharsPerSection)
                body = body[..maxCharsPerSection] + $"\n... ({body.Length - maxCharsPerSection} more characters; raise maxCharsPerSection or ask for this section alone)";
            output[key] = body;
        }

        return new
        {
            file = file.FullName,
            written = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
            format = file.Name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase) ? "Crash Logger SSE" : "NetScriptFramework or other",
            sectionsAvailable = parsed.Keys.ToArray(),
            // FEAT-279: known signatures and what the report names (plugins, meshes, the faulting DLL).
            analysis = CrashPatterns.Analyse(text),
            // Round A: every report showed the same recorder sessions; name the one that ended when this report was written.
            recorderSessionForThisReport = RecorderSessionNear(file.LastWriteTime),
            sections = output,
            recent,
            windowsDump,
            // FEAT-287: a second opinion from a dedicated analyzer with a much larger signature set (the player pastes the log).
            secondOpinion = file.Name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase)
                ? $"For a second opinion, the player can paste this report into Phostwood's Crash Log Analyzer (https://phostwood.github.io/crash-analyzer/), which knows many more crash signatures: {file.FullName}"
                : null,
            skylinkRecorder = recorder,
        };
    }

    // ---- SkyLink crash recorder (FEAT-219) ----
    // Written by the SKSE plugin's host DLL (SkyrimMCPPlugin/src/CrashRecorder.h has the
    // format; keep the two in step): a 256-byte header, then fixed 256-byte entries in a ring.

    private static readonly byte[] RecorderMagic = "SLCR"u8.ToArray();
    private const int RecorderHeaderSize = 256;
    private const int RecorderEntrySize = 256;
    private const int RecorderTextOffset = 24;

    private static readonly System.Text.RegularExpressions.Regex QuitCommand =
        new(@"""command""\s*:\s*""\s*(qqq|quitgame)\s*""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly string[] RecorderKinds = { "?", "request", "event", "cell", "papyrus", "note" };

    // Round B (GOG): with the GOG game connected, GetCrashLog showed the Steam game's reports and recorder sessions (newest
    // across all games). Once the server knows which game it talks to (it keeps that after a crash), only that game's
    // documents folder is searched; before then, every game's.
    internal static IEnumerable<string> GameDocumentFolders()
    {
        if (SkyLinkSettings.GameDocumentsFolder is { Length: > 0 } connected)
        {
            yield return connected;
            yield break;
        }
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);  // follows OneDrive redirection
        foreach (var game in new[] { "Skyrim Special Edition", "Skyrim Special Edition GOG", "Skyrim VR" })
            yield return Path.Combine(docs, "My Games", game);
    }

    internal static IEnumerable<string> RecorderFolders() => GameDocumentFolders().Select(d => Path.Combine(d, "SkyLinkAI"));

    // The newest copy of a recorder file across the game folders (Steam, GOG, VR).
    private static FileInfo? FindRecorderFile(string name)
    {
        FileInfo? newest = null;
        foreach (var folder in RecorderFolders())
        {
            try
            {
                var f = new FileInfo(Path.Combine(folder, name));
                if (f.Exists && (newest == null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc)) newest = f;
            }
            catch { /* unreadable folder: skip it */ }
        }
        return newest;
    }

    /// <summary>The recorder file whose last entry is closest to a crash report's time (within 10 minutes).</summary>
    static object? RecorderSessionNear(DateTime when)
    {
        var names = new[] { "crash-recorder-previous.bin", "crash-recorder.bin" }.Concat(Enumerable.Range(2, 4).Select(n => $"crash-recorder-previous-{n}.bin"));
        (string file, string? start, string? last, double gap)? best = null;
        foreach (var name in names)
        {
            if (FindRecorderFile(name) is not FileInfo f) continue;
            try
            {
                var d = JsonSerializer.SerializeToNode(DecodeRecorder(f, 3, out _));
                if (!DateTime.TryParse(d?["lastEntry"]?.ToString(), out var lastAt)) continue;
                var gap = Math.Abs((lastAt - when).TotalMinutes);
                if (gap <= 10 && (best == null || gap < best.Value.gap)) best = (f.Name, d?["sessionStart"]?.ToString(), d?["lastEntry"]?.ToString(), gap);
            }
            catch (Exception) { }
        }
        return best is { } b ? new { file = b.file, sessionStart = b.start, lastEntry = b.last, note = "This recorder session ended when the report was written: its entries are the run-up to this crash." }
            : new { note = "No recorder session ended within 10 minutes of this report (older than the five kept, or the plugin wasn't loaded)." };
    }

    internal static object ReadRecorder(int entries, bool includeCurrent)
    {
        var previous = FindRecorderFile("crash-recorder-previous.bin");
        var current = FindRecorderFile("crash-recorder.bin");
        if (previous == null && current == null)
        {
            return new
            {
                note = "No SkyLink crash recorder files found. The SKSE plugin writes them once the game has been started " +
                       "with a plugin build that has the recorder. Searched: " +
                       string.Join("; ", RecorderFolders()),
            };
        }

        object? previousCrash = previous != null ? DecodeRecorder(previous, entries, out _) : null;
        // The plugin keeps every session without a normal exit; one SkyLink quit itself (RestartGame) isn't a crash.
        string? previousNote = previousCrash != null && JsonSerializer.SerializeToNode(previousCrash)?["notACrash"]?.GetValue<bool>() == true
            ? "previousCrash is the session RestartGame quit: kept because it had no normal exit, but it wasn't a crash." : null;
        // FEAT-264: older unclean sessions kept by the plugin (previous-2 ... previous-5), summarised.
        var older = new List<object>();
        for (var n = 2; n <= 5; n++)
            if (FindRecorderFile($"crash-recorder-previous-{n}.bin") is FileInfo f)
            {
                var d = JsonSerializer.SerializeToNode(DecodeRecorder(f, 3, out _));
                older.Add(new { file = f.Name, sessionStart = d?["sessionStart"]?.ToString(), lastEntry = d?["lastEntry"]?.ToString(), exit = d?["exit"]?.ToString() });
            }
        object? lastSession = null;
        string? lastSessionNote = null;
        if (current != null)
        {
            var decoded = DecodeRecorder(current, entries, out var running);
            if (!running || includeCurrent)
                lastSession = decoded;
            else
                lastSessionNote = "The game is running, so lastSession is this session; pass recorderCurrent=true to see it.";
        }

        var searched = SkyLinkSettings.GameDocumentsFolder is { Length: > 0 } g ? Path.GetFileName(g.TrimEnd('\\')) + " (the connected game)" : "every game's folder (no game connected since the server started)";
        return new { searched, previousCrash, previousNote, olderUncleanSessions = older.Count > 0 ? older : null, lastSession, lastSessionNote };
    }

    private static object DecodeRecorder(FileInfo file, int maxEntries, out bool running)
    {
        running = false;
        byte[] data;
        try
        {
            // The game keeps crash-recorder.bin mapped while it runs.
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            data = ms.ToArray();
        }
        catch (Exception ex)
        {
            return new { file = file.FullName, error = $"Could not read: {ex.Message}" };
        }

        if (data.Length < RecorderHeaderSize || !data.AsSpan(0, 4).SequenceEqual(RecorderMagic))
            return new { file = file.FullName, error = "Not a SkyLink crash recorder file" };

        var span = data.AsSpan();
        uint version = BitConverter.ToUInt32(span[4..]);
        uint headerSize = BitConverter.ToUInt32(span[8..]);
        uint entrySize = BitConverter.ToUInt32(span[12..]);
        uint capacity = BitConverter.ToUInt32(span[16..]);
        uint exitState = BitConverter.ToUInt32(span[20..]);
        ulong startMs = BitConverter.ToUInt64(span[24..]);
        ulong writeIndex = BitConverter.ToUInt64(span[32..]);
        uint processId = BitConverter.ToUInt32(span[40..]);
        string pluginVersion = ReadCString(span.Slice(48, 32));
        string gameVersion = ReadCString(span.Slice(80, 32));
        ulong exitMs = BitConverter.ToUInt64(span[112..]);

        if (version != 1 || headerSize != RecorderHeaderSize || entrySize != RecorderEntrySize ||
            capacity == 0 || data.Length < (long)headerSize + (long)entrySize * capacity)
        {
            return new { file = file.FullName, error = $"Unsupported recorder format (version {version}); update SkyLink's MCP server" };
        }

        running = exitState == 0 && IsSkyrimProcess(processId);

        var list = new List<(ulong seq, ulong ms, uint thread, byte kind, string text)>();
        for (int i = 0; i < capacity; i++)
        {
            var e = span.Slice(RecorderHeaderSize + i * RecorderEntrySize, RecorderEntrySize);
            ulong seq = BitConverter.ToUInt64(e[16..]);
            if (seq == 0) continue;
            list.Add((seq, BitConverter.ToUInt64(e), BitConverter.ToUInt32(e[8..]), e[12],
                ReadCString(e[RecorderTextOffset..])));
        }
        list.Sort((a, b) => a.seq.CompareTo(b.seq));

        int take = Math.Clamp(maxEntries, 0, list.Count);
        var lines = list.Skip(list.Count - take).Select(x =>
            $"{LocalTime(x.ms):HH:mm:ss.fff} {(x.kind < RecorderKinds.Length ? RecorderKinds[x.kind] : "?"),-7} t{x.thread} {x.text}").ToArray();

        // BUG-063: on 1.7.104 a Quit to Desktop ends the process without any of the signals the recorder watches, so an
        // unmarked session is judged by whether a crash report was written when it ended.
        // A quit SkyLink asked for (RestartGame's quit_game) sets the game's quit flag, so the session is marked "quitting" and
        // then ends without a normal exit like any quit on this game version: not a crash unless a report was written.
        // Also the console's quit commands sent through ExecuteConsoleCommand (round B: a qqq session read as "most likely a
        // normal quit or a forced close").
        var quitBySkyLink = !running && list.Any(x => x.text.StartsWith("> quit_game") ||
            (x.text.StartsWith("> execute_command") && QuitCommand.IsMatch(x.text)));
        string? crashReport = null;
        if ((exitState == 0 || (exitState == 2 && quitBySkyLink)) && !running && list.Count > 0)
        {
            var end = LocalTime(list[^1].ms);
            var report = FindCrashLogs(null).FirstOrDefault(f => f.LastWriteTime >= end.AddSeconds(-5) && f.LastWriteTime <= end.AddMinutes(5));
            crashReport = report?.FullName;
        }
        string exit = exitState switch
        {
            1 => "exited cleanly",
            0 or 2 when quitBySkyLink && crashReport != null => $"crashed while quitting: SkyLink asked the game to quit (RestartGame or a console quit) and a crash report was written ({crashReport})",
            0 or 2 when quitBySkyLink => "quit by SkyLink (RestartGame or a console quit asked the game to quit): a normal quit, not a crash",
            0 when !running && crashReport != null => $"crashed: a crash report was written when it ended ({crashReport})",
            0 when !running && list.Count > 0 && !CrashReporterInstalled() => "ended without an exit mark, and no crash reporter " +
                "(Crash Logger SSE) is installed, so a crash can't be told from a normal quit (this game version ends the process " +
                "without a normal exit, so a quit isn't marked)",
            0 when !running && list.Count > 0 => "ended without a crash report: most likely a normal quit (this game version ends " +
                "the process without a normal exit, so a quit isn't marked) or a forced close. A crash with Crash Logger installed " +
                "leaves a report.",
            2 => "had started quitting to the desktop but did not reach a normal exit (a crash on exit, or the game ended itself abruptly)",
            _ when running => "still running",
            _ => "did not exit cleanly: it crashed, or was closed by force (Task Manager, power loss)",
        };

        return new
        {
            file = file.FullName,
            sessionStart = LocalTime(startMs).ToString("yyyy-MM-dd HH:mm:ss"),
            lastEntry = list.Count > 0 ? LocalTime(list[^1].ms).ToString("yyyy-MM-dd HH:mm:ss.fff") : null,
            exit,
            exitTime = exitMs != 0 ? LocalTime(exitMs).ToString("yyyy-MM-dd HH:mm:ss") : null,
            notACrash = quitBySkyLink && crashReport == null ? true : (bool?)null,
            pluginVersion,
            gameVersion,
            totalEntriesWritten = writeIndex,
            entriesKept = list.Count,
            shown = lines.Length,
            format = "local time, kind, thread id, text. Threads: the pipe thread runs requests; the game thread sends events.",
            entries = lines,
        };
    }

    private static DateTime LocalTime(ulong unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Min(unixMs, 253402300799999UL)).LocalDateTime;

    private static string ReadCString(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes);
    }

    private static bool IsSkyrimProcess(uint processId)
    {
        if (processId == 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)processId);
            return !p.HasExited && p.ProcessName.StartsWith("Skyrim", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;  // no such process
        }
    }

    internal static IEnumerable<string> SearchFolders(string? skyrimPath)
    {
        foreach (var docs in GameDocumentFolders())
            yield return Path.Combine(docs, "SKSE");
        if (!string.IsNullOrEmpty(skyrimPath))
            yield return Path.Combine(skyrimPath, "Data", "NetScriptFramework", "Crash");
    }

    // Crash Logger SSE (CrashLogger.dll) or NetScriptFramework in the SKSE plugins folder, loose or in MO2's mods.
    private static bool CrashReporterInstalled()
    {
        if (FindCrashLogs(null).Count > 0) return true;
        try
        {
            var folders = new List<string>();
            if (SkyLinkSettings.DataPath() is string data) folders.Add(Path.Combine(data, "SKSE", "Plugins"));
            var settings = SkyLinkSettings.Load();
            if (settings["mo2Path"]?.GetValue<string>() is { Length: > 0 } mo2 && Directory.Exists(Path.Combine(mo2, "mods")))
                folders.AddRange(Directory.EnumerateDirectories(Path.Combine(mo2, "mods")).Select(m => Path.Combine(m, "SKSE", "Plugins")));
            return folders.Where(Directory.Exists).Any(f =>
                Directory.EnumerateFiles(f, "CrashLogger*.dll").Any() || Directory.EnumerateFiles(f, "NetScriptFramework*.dll").Any());
        }
        catch (Exception) { return false; }
    }

    internal static List<FileInfo> FindCrashLogs(string? skyrimPath)
    {
        var files = new List<FileInfo>();
        foreach (var folder in SearchFolders(skyrimPath))
        {
            try
            {
                var dir = new DirectoryInfo(folder);
                if (!dir.Exists) continue;
                files.AddRange(dir.EnumerateFiles("crash-*.log"));
                files.AddRange(dir.EnumerateFiles("Crash_*.txt"));
            }
            catch { /* unreadable folder: skip it */ }
        }
        return files.OrderByDescending(f => f.LastWriteTimeUtc).ToList();
    }

    // Crash Logger section headers are unindented lines ending in ':' ("STACK:", "MODULES:").
    // Everything before the first header is the "header" section.
    internal static Dictionary<string, string> SplitSections(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var current = "header";
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 1 && !char.IsWhiteSpace(line[0]) && line.EndsWith(':') && IsHeader(line))
            {
                result[current] = sb.ToString().TrimEnd();
                sb.Clear();
                current = line[..^1];
                continue;
            }
            sb.Append(line).Append('\n');
        }
        result[current] = sb.ToString().TrimEnd();
        return result;
    }

    private static bool IsHeader(string line)
    {
        // "PROBABLE CALL STACK:", "SKSE PLUGINS:" - capitals, spaces and slashes only.
        var name = line[..^1];
        return name.Length >= 4 && name.All(c => char.IsUpper(c) || c == ' ' || c == '/');
    }
}
