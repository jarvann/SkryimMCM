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
        "Look for mod names (.esp/.esm/.esl) and DLL names in the call stack and relevant objects.")]
    public Task<object> GetCrashLog(int index = 0, string? sections = null, int maxCharsPerSection = 6000, string? skyrimPath = null)
    {
        var logs = FindCrashLogs(skyrimPath);
        var recent = logs.Take(10).Select(f => new { file = f.FullName, written = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") }).ToArray();
        if (logs.Count == 0)
        {
            return Task.FromResult<object>(new
            {
                error = "No crash reports found. Crash Logger SSE (recommended) writes crash-*.log to Documents\\My Games\\Skyrim Special Edition\\SKSE. " +
                        "Is it installed? Searched: " + string.Join("; ", SearchFolders(skyrimPath)),
            });
        }
        if (index < 0 || index >= logs.Count)
            return Task.FromResult<object>(new { error = $"index must be 0 to {logs.Count - 1}", recent });

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
            return Task.FromResult<object>(new { error = $"Could not read {file.FullName}: {ex.Message}", recent });
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
        });
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
