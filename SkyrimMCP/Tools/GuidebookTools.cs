using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// The guidebook (FEAT-221): looks things up on the UESP wiki so help answers come from a game
/// reference rather than the AI's memory. Queried live through UESP's MediaWiki API; nothing is
/// bundled. UESP text is CC BY-SA 2.5, so every reply that carries it names the page and the
/// licence. Works with the game closed: no pipe calls.
/// </summary>
[McpServerToolType]
public class GuidebookTools : ToolBase
{
    internal const string ApiUrl = "https://en.uesp.net/w/api.php";
    internal const string PageUrlBase = "https://en.uesp.net/wiki/";
    internal const string Licence = "UESP, CC BY-SA 2.5";

    /// <summary>Sent to the client as the MCP server instructions (see Program.cs).</summary>
    internal const string ServerInstructions =
        "SkyLink AI connects you to the player's running Skyrim game. " +
        "Before answering a question about Skyrim quests, locations, characters (NPCs), items, ingredients, alchemy, " +
        "smithing, enchanting, skills, perks, spells, shouts or other game mechanics, look it up in the guidebook first: " +
        "SearchGuidebook to find the page, then GetGuidebookPage (ask for one section when the page is long). " +
        "Base the answer on the page, and name the UESP page it came from. " +
        "If the guidebook has no page on it or cannot be reached, say you are answering from memory, which may be wrong. " +
        "The live game tools show this player's own state (inventory, quest stages, position); use them alongside the guidebook. " +
        // FEAT-266/267: slow games and falling FPS lead to the stack and save checks.
        "When the player says the game is slow, stuttering or laggy, or the FPS are dropping, offer to check it: StackReport " +
        "first (live script pile-ups, a few seconds, changes nothing), then CheckSave (saves a copy and scans it for script " +
        "leftovers and queued stacks). Explain what they found in plain words, naming the mods responsible, and start a " +
        "conversation about those mods: what they do, whether the player still needs them, settings or updates that stop " +
        "the pile-up, and known conflicts (look them up). Ask whether the player develops mods; if they do, ask which ones " +
        "they're working on and tie the findings back to that work (their scripts in a pile-up, an event handler that " +
        "registers again without finishing, leftovers from an earlier build of their mod in this save). Cleaning a save " +
        "(CleanAndReload, when it is in your tool list) edits saved game data: say so before offering it, say what could " +
        "break (a quest stuck at a stage, a mod losing its setup), that the original save is kept as the way back, and that " +
        "the pile-up returns if the mod keeps causing it. Only clean after the player agrees; the cleanup saves, cleans a " +
        "copy and loads it in one go.";

    // Namespace ids from meta=siteinfo (checked 2026-11-16): 134 = Skyrim (Dawnguard and Hearthfire pages live
    // here too), 146 = Dragonborn.
    private const string SkyrimNamespaces = "134|146";
    private static readonly string[] KnownPrefixes = { "Skyrim:", "Dragonborn:", "Skyrim Mod:", "Lore:" };

    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(7);
    private static readonly ConcurrentDictionary<string, (DateTime Fetched, string Body)> MemoryCache = new();
    private static readonly HttpClient Http = CreateClient();

    public GuidebookTools(IPipeClient pipe) : base(pipe) { }

    internal static string CacheFolder => Path.Combine(SkyLinkSettings.Folder, "guidebook-cache");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SkyLinkAI/1.3 (Skyrim mod; +https://www.nexusmods.com/skyrimspecialedition/mods/175682)");
        return client;
    }

    [McpServerTool]
    [Description("Search the Skyrim guidebook (the UESP wiki, including Dawnguard, Hearthfire and Dragonborn) for pages about a " +
        "quest, location, NPC, item, ingredient, spell, perk, skill, crafting recipe or game mechanic. " +
        "Use this before answering any help question about the game, then read the best match with GetGuidebookPage, " +
        "so the answer comes from the reference rather than memory. Works while the game is closed; needs internet access. " +
        "query: plain words, such as 'fortify smithing potion' or 'Blessing of Nature'. limit: results to return (default 8, at most 20). " +
        "Returns each page's title, a short snippet and its URL.")]
    public async Task<object> SearchGuidebook(string query, int limit = 8)
    {
        if (string.IsNullOrWhiteSpace(query)) return new { error = "query is empty" };
        limit = Math.Clamp(limit, 1, 20);
        try
        {
            var (results, totalHits, searched) = await Search(query.Trim(), limit);
            return new
            {
                query = query.Trim(),
                searchedFor = searched == query.Trim() ? null : searched,
                totalHits,
                results,
                note = results.Length == 0 ? "No Skyrim pages matched. Try other words, or a shorter name." : null,
                source = "https://en.uesp.net/w/index.php?ns134=1&ns146=1&search=" + Uri.EscapeDataString(query.Trim()),
                licence = Licence,
            };
        }
        catch (InvalidOperationException ex)
        {
            return new { error = ex.Message };
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            return Offline(ex);
        }
    }

    [McpServerTool]
    [Description("Read a page from the Skyrim guidebook (the UESP wiki) as plain text, with tables as 'cell | cell' rows. " +
        "Use it to answer help questions from the reference, and name the page in your answer. " +
        "title: the page name from SearchGuidebook, with or without the 'Skyrim:' prefix ('Alchemy', 'Skyrim:Diplomatic Immunity', " +
        "'Dragonborn:Raven Rock'); a UESP page URL also works. " +
        "section (optional): one section by name or number from the 'sections' list ('Walkthrough', '3.1'), or '0' for the opening " +
        "summary. Long pages (quests, skills) are best read one section at a time. " +
        "maxChars (default 8000) trims the text; the reply says how much more there is. Works while the game is closed; needs internet access.")]
    public async Task<object> GetGuidebookPage(string title, string? section = null, int maxChars = 8000)
    {
        if (string.IsNullOrWhiteSpace(title)) return new { error = "title is empty" };
        var (candidates, fragment) = CandidateTitles(title);
        if (string.IsNullOrWhiteSpace(section) && fragment != null) section = fragment;

        try
        {
            JsonNode? parse = null;
            foreach (var candidate in candidates)
            {
                var json = await Query("action=parse&prop=text&redirects=1&disableeditsection=1&disabletoc=1&disablelimitreport=1&page=" +
                    Uri.EscapeDataString(candidate));
                var code = json["error"]?["code"]?.GetValue<string>();
                if (code == "missingtitle" || code == "invalidtitle") continue;
                if (json["error"] != null) return new { error = "UESP could not return the page: " + json["error"]?["info"] };
                parse = json["parse"];
                break;
            }

            if (parse == null)
            {
                object? suggestions = null;
                try { suggestions = (await Search(candidates[^1].Split(':', 2)[^1], 5)).Results; }
                catch { /* suggestions are a bonus: leave them out */ }
                return new
                {
                    error = $"The guidebook has no page called \"{title.Trim()}\". Pick one of the suggestions, or use SearchGuidebook.",
                    suggestions,
                    licence = suggestions == null ? null : Licence,
                };
            }

            var pageTitle = parse["title"]?.GetValue<string>() ?? candidates[0];
            var html = parse["text"]?.GetValue<string>() ?? "";
            var redirectedFrom = parse["redirects"] is JsonArray r && r.Count > 0 ? r[0]?["from"]?.GetValue<string>() : null;
            var (text, heads) = HtmlToText(html);
            var sectionNames = heads.Select(h => $"{h.Number} {h.Title}").ToArray();

            string body = text;
            string? sectionShown = null;
            var source = PageUrl(pageTitle);
            if (!string.IsNullOrWhiteSpace(section))
            {
                var wanted = section.Trim();
                if (wanted == "0" || wanted.Equals("intro", StringComparison.OrdinalIgnoreCase) ||
                    wanted.Equals("introduction", StringComparison.OrdinalIgnoreCase))
                {
                    body = heads.Count > 0 ? text[..heads[0].Start].TrimEnd() : text;
                    sectionShown = "0 (opening summary)";
                }
                else
                {
                    var match = heads.FirstOrDefault(h => h.Number == wanted)
                        ?? heads.FirstOrDefault(h => h.Title.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                        ?? heads.FirstOrDefault(h => h.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        return new
                        {
                            error = $"\"{pageTitle}\" has no section \"{wanted}\". Pick one from 'sections'.",
                            title = pageTitle,
                            sections = sectionNames,
                            source,
                            licence = Licence,
                        };
                    }
                    body = text[match.Start..match.End].TrimEnd();
                    sectionShown = $"{match.Number} {match.Title}";
                    source += "#" + EscapeTitle(match.Title);
                }
            }

            var totalChars = body.Length;
            string? note = null;
            if (maxChars > 0 && body.Length > maxChars)
            {
                body = body[..maxChars];
                note = $"Trimmed: {totalChars - maxChars} more characters. Ask for one section from 'sections', or raise maxChars.";
            }

            return new
            {
                title = pageTitle,
                redirectedFrom,
                section = sectionShown,
                sections = sectionNames,
                text = body,
                totalChars,
                note,
                source,
                licence = Licence,
            };
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            return Offline(ex);
        }
    }

    // ---- UESP API -------------------------------------------------------------------------------

    /// <summary>Searches the Skyrim namespaces. With no hits, retries once with UESP's spelling suggestion.</summary>
    private static async Task<(object[] Results, int TotalHits, string SearchedFor)> Search(string query, int limit)
    {
        var json = await Query($"action=query&list=search&srprop=snippet&srinfo=totalhits|suggestion&srnamespace={SkyrimNamespaces}" +
            $"&srlimit={limit}&srsearch=" + Uri.EscapeDataString(query));
        if (json["error"] != null) throw new InvalidOperationException("UESP search failed: " + json["error"]?["info"]);
        var hits = json["query"]?["search"] as JsonArray ?? new JsonArray();
        var suggestion = json["query"]?["searchinfo"]?["suggestion"]?.GetValue<string>();
        if (hits.Count == 0 && !string.IsNullOrWhiteSpace(suggestion) && !suggestion.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            var retry = await Query($"action=query&list=search&srprop=snippet&srinfo=totalhits&srnamespace={SkyrimNamespaces}" +
                $"&srlimit={limit}&srsearch=" + Uri.EscapeDataString(suggestion));
            if (retry["error"] == null && retry["query"]?["search"] is JsonArray { Count: > 0 } corrected)
            {
                json = retry;
                hits = corrected;
                query = suggestion;
            }
        }
        var results = hits.Select(h =>
        {
            var t = h?["title"]?.GetValue<string>() ?? "";
            return (object)new { title = t, snippet = CleanSnippet(h?["snippet"]?.GetValue<string>() ?? ""), url = PageUrl(t) };
        }).ToArray();
        var total = json["query"]?["searchinfo"]?["totalhits"]?.GetValue<int>() ?? results.Length;
        return (results, total, query);
    }

    /// <summary>Calls the API, answering from the memory or disk cache when a copy under 7 days old exists.</summary>
    private static async Task<JsonNode> Query(string parameters)
    {
        var url = $"{ApiUrl}?{parameters}&format=json&formatversion=2";
        if (TryCache(url, out var cached))
        {
            var node = JsonNode.Parse(cached);
            if (node != null) return node;
        }

        using var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body) ?? throw new HttpRequestException("UESP returned an empty reply");
        if (json["error"] == null) Store(url, body);
        return json;
    }

    private static string CacheFile(string url) =>
        Path.Combine(CacheFolder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".json");

    private static bool TryCache(string url, out string body)
    {
        body = "";
        if (MemoryCache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.Fetched < CacheAge)
        {
            body = hit.Body;
            return true;
        }
        try
        {
            var file = new FileInfo(CacheFile(url));
            if (file.Exists && DateTime.UtcNow - file.LastWriteTimeUtc < CacheAge)
            {
                body = File.ReadAllText(file.FullName);
                MemoryCache[url] = (file.LastWriteTimeUtc, body);
                return true;
            }
        }
        catch { /* unreadable cache file: fetch again */ }
        return false;
    }

    private static void Store(string url, string body)
    {
        if (MemoryCache.Count > 300) MemoryCache.Clear();
        MemoryCache[url] = (DateTime.UtcNow, body);
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var path = CacheFile(url);
            var temp = path + ".tmp";
            File.WriteAllText(temp, body);
            SkyLinkSettings.ReplaceFile(temp, path);
        }
        catch { /* the disk cache is optional */ }
    }

    private static bool IsNetworkError(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException or System.Text.Json.JsonException;

    private static object Offline(Exception ex) => new
    {
        error = "The guidebook (UESP wiki) could not be reached: " + ex.Message + ". The guidebook needs internet access. " +
                "Tell the player you are answering from memory, which may be wrong.",
    };

    // ---- Titles and URLs ------------------------------------------------------------------------

    /// <summary>
    /// Titles to try, in order. A title with a known namespace prefix is used as given; without one,
    /// Skyrim: then Dragonborn:. A UESP URL is reduced to its title, and its #fragment returned as a section.
    /// </summary>
    internal static (List<string> Titles, string? Fragment) CandidateTitles(string raw)
    {
        var t = raw.Trim();
        string? fragment = null;
        var wiki = t.IndexOf("/wiki/", StringComparison.OrdinalIgnoreCase);
        if (t.StartsWith("http", StringComparison.OrdinalIgnoreCase) && wiki >= 0)
        {
            t = t[(wiki + 6)..];
            var hash = t.IndexOf('#');
            if (hash >= 0)
            {
                fragment = Uri.UnescapeDataString(t[(hash + 1)..]).Replace('_', ' ');
                t = t[..hash];
            }
            t = Uri.UnescapeDataString(t);
        }
        t = t.Replace('_', ' ').Trim();
        if (KnownPrefixes.Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return (new List<string> { t }, fragment);
        return (new List<string> { "Skyrim:" + t, "Dragonborn:" + t }, fragment);
    }

    private static string EscapeTitle(string title) =>
        Uri.EscapeDataString(title.Replace(' ', '_'))
            .Replace("%3A", ":").Replace("%2F", "/").Replace("%28", "(").Replace("%29", ")").Replace("%27", "'").Replace("%2C", ",");

    internal static string PageUrl(string title) => PageUrlBase + EscapeTitle(title);

    private static string CleanSnippet(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")), @"\s+", " ").Trim();

    // ---- HTML to text ---------------------------------------------------------------------------

    internal sealed record Heading(int Level, string Title, string Number, int Start, int End);

    private static readonly Regex TagRx = new(@"<!--.*?-->|<(/?)([a-zA-Z][a-zA-Z0-9]*)\b([^>]*?)(/?)>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ClassRx = new(@"\bclass\s*=\s*""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex AltRx = new(@"\balt\s*=\s*""([^""]*)""", RegexOptions.Compiled);
    private static readonly HashSet<string> VoidTags = new() { "br", "img", "hr", "input", "meta", "link", "wbr", "source", "col", "area", "embed", "param", "track" };
    private static readonly HashSet<string> SkipTags = new() { "script", "style", "img", "input", "noscript", "audio", "video", "map" };
    private static readonly HashSet<string> SkipClasses = new()
    {
        "toc", "mw-editsection", "navbox", "thumb", "reference", "references", "mw-references-wrap", "noprint", "magnify",
    };
    private const char HeadMark = '\u0001';

    /// <summary>
    /// Turns MediaWiki parser output into plain text: headings as markdown '#' lines, list items as '- ',
    /// table rows on one line with cells joined by ' | '. Images, edit links, the table of contents,
    /// footnote markers and navigation boxes are dropped. Returns the text and its headings, numbered
    /// like the page's table of contents, with the span each one's section covers.
    /// </summary>
    internal static (string Text, List<Heading> Headings) HtmlToText(string html)
    {
        var sb = new StringBuilder(html.Length / 3);
        var rowCells = new List<int>();   // one entry per open table: cells written in its current row
        var cells = new List<(int Start, string? Alt)>();  // open td/th: where its text starts, first image's alt text
        int cellDepth = 0, listDepth = 0, skipDepth = 0;
        string? skipTag = null;
        int pos = 0;

        void Break() => sb.Append(cellDepth > 0 ? ' ' : '\n');
        bool CellHasText() => cells.Count > 0 && sb.ToString(cells[^1].Start, sb.Length - cells[^1].Start).Trim().Length > 0;

        foreach (Match m in TagRx.Matches(html))
        {
            if (skipDepth == 0 && m.Index > pos) AppendText(sb, html.AsSpan(pos, m.Index - pos));
            pos = m.Index + m.Length;
            if (m.Value.StartsWith("<!--")) continue;

            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value.ToLowerInvariant();
            var attrs = m.Groups[3].Value;
            var isVoid = m.Groups[4].Value == "/" || VoidTags.Contains(name);

            if (skipDepth > 0)
            {
                if (name == skipTag && !isVoid) skipDepth += closing ? -1 : 1;
                continue;
            }
            // An icon alone in a table cell ("Value", "Weight") is the cell's label: keep its alt text for that case.
            // Alt text that is just a file name ("SR-mapicon-Raven Rock.png") says nothing, so it is left out.
            if (name == "img" && cells.Count > 0 && cells[^1].Alt == null && AltRx.Match(attrs) is { Success: true } alt &&
                !Regex.IsMatch(alt.Groups[1].Value, @"\.(png|jpe?g|gif|svg|webp)$", RegexOptions.IgnoreCase))
                cells[^1] = (cells[^1].Start, WebUtility.HtmlDecode(alt.Groups[1].Value));
            if (!closing && (SkipTags.Contains(name) || IsSkipped(attrs)))
            {
                if (!isVoid) { skipTag = name; skipDepth = 1; }
                continue;
            }

            switch (name)
            {
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    if (cellDepth > 0) sb.Append(' ');
                    else if (!closing) sb.Append("\n\n").Append(HeadMark).Append(name[1]).Append(' ');
                    else sb.Append('\n');
                    break;
                case "p": case "div": case "dl": case "blockquote": case "pre": case "center": case "caption": case "br": case "hr":
                    Break();
                    break;
                case "ul": case "ol":
                    listDepth = Math.Max(0, listDepth + (closing ? -1 : 1));
                    Break();
                    break;
                case "li":
                    if (closing) break;
                    if (cellDepth > 0 && CellHasText())
                    {
                        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                        sb.Append("; ");
                    }
                    else if (cellDepth > 0) sb.Append(' ');
                    else sb.Append('\n').Append(' ', 2 * Math.Max(0, listDepth - 1)).Append("- ");
                    break;
                case "dt":
                    if (!closing) Break();
                    break;
                case "dd":
                    if (!closing) sb.Append(cellDepth > 0 ? " " : "\n  ");
                    break;
                case "table":
                    if (closing) { if (rowCells.Count > 0) rowCells.RemoveAt(rowCells.Count - 1); }
                    else rowCells.Add(0);
                    Break();
                    break;
                case "tr":
                    if (closing) break;
                    if (rowCells.Count > 0) rowCells[^1] = 0;
                    Break();
                    break;
                case "td": case "th":
                    if (closing)
                    {
                        if (cells.Count > 0)
                        {
                            if (!CellHasText() && !string.IsNullOrWhiteSpace(cells[^1].Alt)) sb.Append(cells[^1].Alt);
                            cells.RemoveAt(cells.Count - 1);
                        }
                        cellDepth = Math.Max(0, cellDepth - 1);
                        break;
                    }
                    if (rowCells.Count > 0)
                    {
                        if (rowCells[^1] > 0) sb.Append(" | ");
                        rowCells[^1]++;
                    }
                    cells.Add((sb.Length, null));
                    cellDepth++;
                    break;
                case "sup":
                    sb.Append(closing ? "]" : "[");
                    break;
            }
        }
        if (skipDepth == 0 && pos < html.Length) AppendText(sb, html.AsSpan(pos));

        // Tidy lines, drop empty table rows, keep at most one blank line, and pick out the headings.
        var output = new StringBuilder(sb.Length);
        var raw = new List<(int Level, string Title, int Start)>();
        var blank = true;
        foreach (var line0 in sb.ToString().Split('\n'))
        {
            var line = Regex.Replace(line0, @"[ \t]+", " ").Replace("[]", "").Replace("[ ]", "").Trim();
            if (line.Trim('|', ' ').Length == 0)
            {
                if (!blank && output.Length > 0) { output.Append('\n'); blank = true; }
                continue;
            }
            if (line[0] == HeadMark)
            {
                var level = line.Length > 1 && char.IsDigit(line[1]) ? line[1] - '0' : 2;
                var title = line.Length > 2 ? line[2..].Trim() : "";
                if (title.Length == 0) continue;
                if (!blank && output.Length > 0) output.Append('\n');
                raw.Add((level, title, output.Length));
                output.Append('#', level).Append(' ').Append(title).Append("\n\n");
                blank = true;
                continue;
            }
            output.Append(line).Append('\n');
            blank = false;
        }

        var text = output.ToString().Trim('\n');
        var headings = new List<Heading>();
        if (raw.Count > 0)
        {
            var top = raw.Min(h => h.Level);
            var counters = new int[8];
            for (var i = 0; i < raw.Count; i++)
            {
                var (level, title, start) = raw[i];
                counters[level]++;
                for (var d = level + 1; d < counters.Length; d++) counters[d] = 0;
                var number = string.Join(".", counters[top..(level + 1)].Select(c => Math.Max(c, 1)));
                var end = text.Length;
                for (var j = i + 1; j < raw.Count; j++)
                {
                    if (raw[j].Level <= level) { end = raw[j].Start; break; }
                }
                headings.Add(new Heading(level, title, number, Math.Min(start, text.Length), Math.Min(end, text.Length)));
            }
        }
        return (text, headings);
    }

    private static void AppendText(StringBuilder sb, ReadOnlySpan<char> raw)
    {
        var decoded = WebUtility.HtmlDecode(raw.ToString());
        foreach (var c in decoded)
        {
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ' && sb[^1] != '\n') sb.Append(' ');
            }
            else sb.Append(c);
        }
    }

    private static bool IsSkipped(string attrs)
    {
        if (attrs.Contains("display:none", StringComparison.OrdinalIgnoreCase) ||
            attrs.Contains("display: none", StringComparison.OrdinalIgnoreCase)) return true;
        var cls = ClassRx.Match(attrs);
        return cls.Success && cls.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(SkipClasses.Contains);
    }
}
