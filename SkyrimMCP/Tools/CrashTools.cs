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
        "Look for mod names (.esp/.esm/.esl) and DLL names in the call stack and relevant objects. " +
        "skylinkRecorder: SkyLink's own crash recorder (Documents\\My Games\\<game>\\SkyLinkAI\\crash-recorder*.bin), " +
        "the last things SkyLink and the game did before the session ended: AI tool calls ('>' when a request started, " +
        "'<' with ok/error and time when it returned; a '>' with no '<' after it was still running when the game died), " +
        "Papyrus calls, game events, the player's cell and location, loading screens, saves and loads. " +
        "Each session shows its start time, its last entry and whether it exited cleanly: match it to the crash report by time. " +
        "previousCrash is the last session that did not exit cleanly (kept when the next session started); lastSession is " +
        "the newest session, included when the game is not running or recorderCurrent is true. " +
        "recorderEntries (default 60, 0 = header only) is how many of the newest entries to show per session.")]
    public Task<object> GetCrashLog(int index = 0, string? sections = null, int maxCharsPerSection = 6000, string? skyrimPath = null,
        int recorderEntries = 60, bool recorderCurrent = false)
    {
        var recorder = ReadRecorder(recorderEntries, recorderCurrent);
        var logs = FindCrashLogs(skyrimPath);
        var recent = logs.Take(10).Select(f => new { file = f.FullName, written = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") }).ToArray();
        if (logs.Count == 0)
        {
            return Task.FromResult<object>(new
            {
                error = "No crash reports found. Crash Logger SSE (recommended) writes crash-*.log to Documents\\My Games\\Skyrim Special Edition\\SKSE. " +
                        "Is it installed? Searched: " + string.Join("; ", SearchFolders(skyrimPath)),
                skylinkRecorder = recorder,
            });
        }
        if (index < 0 || index >= logs.Count)
            return Task.FromResult<object>(new { error = $"index must be 0 to {logs.Count - 1}", recent, skylinkRecorder = recorder });

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
            return Task.FromResult<object>(new { error = $"Could not read {file.FullName}: {ex.Message}", recent, skylinkRecorder = recorder });
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

        return Task.FromResult<object>(new
        {
            file = file.FullName,
            written = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
            format = file.Name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase) ? "Crash Logger SSE" : "NetScriptFramework or other",
            sectionsAvailable = parsed.Keys.ToArray(),
            sections = output,
            recent,
            skylinkRecorder = recorder,
        });
    }

    // ---- SkyLink crash recorder (FEAT-219) ----
    // Written by the SKSE plugin's host DLL (SkyrimMCPPlugin/src/CrashRecorder.h has the
    // format; keep the two in step): a 256-byte header, then fixed 256-byte entries in a ring.

    private static readonly byte[] RecorderMagic = "SLCR"u8.ToArray();
    private const int RecorderHeaderSize = 256;
    private const int RecorderEntrySize = 256;
    private const int RecorderTextOffset = 24;

    private static readonly string[] RecorderKinds = { "?", "request", "event", "cell", "papyrus", "note" };

    internal static IEnumerable<string> RecorderFolders()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var game in new[] { "Skyrim Special Edition", "Skyrim Special Edition GOG", "Skyrim VR" })
            yield return Path.Combine(docs, "My Games", game, "SkyLinkAI");
    }

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

        return new { previousCrash, lastSession, lastSessionNote };
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

        string exit = exitState switch
        {
            1 => "exited cleanly",
            2 => "had started quitting to the desktop but did not reach a normal exit (a crash on exit, or the game ended itself abruptly)",
            _ when running => "still running",
            _ => "did not exit cleanly: it crashed, or was closed by force (Task Manager, power loss). Builds before 1.3 could " +
                 "also show a normal quit this way; check the last entries.",
        };

        return new
        {
            file = file.FullName,
            sessionStart = LocalTime(startMs).ToString("yyyy-MM-dd HH:mm:ss"),
            lastEntry = list.Count > 0 ? LocalTime(list[^1].ms).ToString("yyyy-MM-dd HH:mm:ss.fff") : null,
            exit,
            exitTime = exitMs != 0 ? LocalTime(exitMs).ToString("yyyy-MM-dd HH:mm:ss") : null,
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
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);  // follows OneDrive redirection
        foreach (var game in new[] { "Skyrim Special Edition", "Skyrim Special Edition GOG", "Skyrim VR" })
            yield return Path.Combine(docs, "My Games", game, "SKSE");
        if (!string.IsNullOrEmpty(skyrimPath))
            yield return Path.Combine(skyrimPath, "Data", "NetScriptFramework", "Crash");
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
