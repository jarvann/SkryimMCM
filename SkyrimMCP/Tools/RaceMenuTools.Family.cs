using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// FEAT-308: the family story. Cory, 2026-11-23: when the player starts building the family, the MCP walks them through narrating
/// the family's history, and the family home fits their homeland ("near the border of their home land"), standing and wealth.
/// The story is kept per player character (family.json) for the AI and later dialogue.
/// </summary>
public partial class RaceMenuTools
{
    /// <summary>The story's questions in the order they're asked: key, question, options (empty: the player's own words).</summary>
    private static readonly (string Key, string Question, string[] Options)[] StoryQuestions =
    [
        ("homeland", "Where does the family come from?",
            ["Skyrim", "High Rock", "Orsinium", "Hammerfell", "Cyrodiil", "Morrowind", "Black Marsh", "Elsweyr", "Valenwood", "Summerset Isles", "somewhere else (name it)"]),
        ("arrival", "How did the family come to be in Skyrim, or how long has it been here? (In the player's own words; 'always' for an old Skyrim family.)", []),
        ("standing", "What is the family's standing?", ["outcast", "common", "trade", "landed", "noble"]),
        ("wealth", "How well off are they now?", ["poor", "modest", "comfortable", "rich"]),
        ("living", "Who in the family is alive? (Names and how they're related; anyone can be left out.)", []),
        ("lost", "Who has the family lost, and how? ('none' is fine.)", []),
        ("rivalries", "Any feuds or rivals? ('none' is fine.)", []),
        ("secrets", "Any family secret? ('none' is fine; the AI keeps it to itself unless the player brings it up.)", []),
    ];

    /// <summary>The homeland's region of Skyrim: the border nearest it (docs/1.6-plan.md, FEAT-308).</summary>
    private static readonly Dictionary<string, (string Region, string Why)> HomelandRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Skyrim"] = ("Whiterun Hold, the Pale or Winterhold", "the heartland"),
        ["High Rock"] = ("the Reach, near Markarth", "the High Rock border"),
        ["Orsinium"] = ("the Reach, near Markarth", "Orsinium's mountains"),
        ["Hammerfell"] = ("the western Reach or Falkreath Hold", "the Hammerfell border"),
        ["Cyrodiil"] = ("Falkreath Hold or the southern Rift", "the Cyrodiil border (the Jerall Mountains)"),
        ["Morrowind"] = ("Eastmarch, or Raven Rock on Solstheim", "the Morrowind border"),
        ["Black Marsh"] = ("the Rift, near Riften", "the nearest route to Black Marsh"),
        ["Elsweyr"] = ("the southern Rift, or with the caravans", "the route south through Cyrodiil"),
        ["Valenwood"] = ("Falkreath Hold", "forests, and the route south"),
        ["Summerset Isles"] = ("Haafingar, near Solitude", "the port and the Imperial seat"),
    };

    /// <summary>The homeland most players of a race would pick, offered first.</summary>
    private static readonly Dictionary<string, string> RaceHomelands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nord"] = "Skyrim", ["Breton"] = "High Rock", ["Orc"] = "Orsinium", ["Redguard"] = "Hammerfell", ["Imperial"] = "Cyrodiil",
        ["Dark Elf"] = "Morrowind", ["Argonian"] = "Black Marsh", ["Khajiit"] = "Elsweyr", ["Wood Elf"] = "Valenwood", ["High Elf"] = "Summerset Isles",
    };

    /// <summary>The style of home that fits a standing and wealth.</summary>
    private static string HomeStyle(string standing, string wealth) => (standing.ToLowerInvariant(), wealth.ToLowerInvariant()) switch
    {
        ("outcast", _) or (_, "poor") => "a shack or cabin",
        ("noble", _) or (_, "rich") => "a manor or estate",
        ("landed", _) => "a farmhouse or homestead",
        ("trade", "comfortable") => "a town house",
        _ => "a farmhouse or homestead",
    };

    [McpServerTool]
    [Description("The family story (FEAT-308): when the player starts building their family, walk them through narrating its history, " +
        "one question at a time, in their own words. Kept per player character. Without arguments: the story so far and the next question. " +
        "Give answers as they come (homeland, arrival, standing, wealth, living, lost, rivalries, secrets); 'none' is a fine answer. When it's told: " +
        "the region of Skyrim and the style of home that fit, the homes the family can use now, and the relatives to make (SpawnNpc with family, " +
        "each still asked follower, marriage, attraction and home). reset: true forgets the story. Never invent answers: ask the player.")]
    public async Task<object> FamilyStory(string? homeland = null, string? arrival = null, string? standing = null, string? wealth = null,
        string? living = null, string? lost = null, string? rivalries = null, string? secrets = null, bool reset = false)
    {
        var appearance = await _pipe.SendRequestAsync("get_appearance");
        var cid = appearance?["characterId"]?.ToString();
        if (string.IsNullOrEmpty(cid)) return new { error = "No player character (load a game first)." };
        if (reset)
        {
            FamilyHomes.SetStory(cid, null);
            return new { reset = true, note = "The family story is forgotten. Call again to start it." };
        }

        var story = FamilyHomes.Story(cid);
        var given = new Dictionary<string, string?>
        {
            ["homeland"] = homeland, ["arrival"] = arrival, ["standing"] = standing, ["wealth"] = wealth,
            ["living"] = living, ["lost"] = lost, ["rivalries"] = rivalries, ["secrets"] = secrets,
        };
        foreach (var (key, value) in given)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var answer = value.Trim();
            var options = StoryQuestions.First(q => q.Key == key).Options;
            // Fixed choices are checked, except homeland, which also takes a place of the player's own.
            if (key is "standing" or "wealth" && !options.Contains(answer, StringComparer.OrdinalIgnoreCase))
                return new { error = $"{key} must be one of: {string.Join(", ", options)}." };
            if (key is "standing" or "wealth") answer = answer.ToLowerInvariant();
            if (key == "homeland" && options.FirstOrDefault(o => o.Equals(answer, StringComparison.OrdinalIgnoreCase)) is { } known) answer = known;
            story[key] = answer;
        }
        if (given.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            story["told"] = DateTime.Now.ToString("s");
            FamilyHomes.SetStory(cid, story);
        }

        var race = appearance?["race"]?.ToString();
        var open = StoryQuestions.Where(q => story[q.Key] == null).ToList();
        if (open.Count > 0)
        {
            var next = open[0];
            var options = next.Key == "homeland" && race != null && RaceHomelands.TryGetValue(race, out var usual)
                ? next.Options.OrderBy(o => o == usual ? 0 : 1).ToArray()
                : next.Options;
            return new
            {
                story,
                next = new { parameter = next.Key, askThePlayer = next.Question, options },
                left = open.Count,
                note = "Ask the player in conversation, one question at a time, and record their answer. Never answer for them.",
            };
        }

        // Told: where and how they'd live, and who to make.
        var land = story["homeland"]!.ToString();
        var region = HomelandRegions.TryGetValue(land, out var r)
            ? new { region = r.Region, why = r.Why }
            : new { region = "wherever the player chooses", why = $"{land} has no border with Skyrim SkyLink knows" };
        var (homes, _, housesError) = await HomeChoices();
        var familyHomes = FamilyHomes.Of(cid).Select(h => h["name"]?.ToString()).ToList();
        var made = SlotRegistry.Load().Select(kv => kv.Value as JsonObject)
            .Where(e => e?["characterId"]?.ToString() == cid && e?["family"] != null)
            .Select(e => $"{e!["name"]} ({e["family"]})").ToList();
        return new
        {
            story,
            told = true,
            familyHome = new
            {
                region.region,
                region.why,
                style = HomeStyle(story["standing"]!.ToString(), story["wealth"]!.ToString()),
                familyHomes,
                canUseNow = homes.Select(h => h.Kind == "skylink" ? $"{h.Name} (SkyLink home)" : h.Name).ToList(),
                houses = housesError,
                howTo = "Offer the player: one of their own houses or a SkyLink home (SetFamilyHome), or a homestead built where they stand " +
                    "(travel to the region first, then BuildHomeLayout where 'here'). Ready-made homes in each region come with a later SkyLinkAI.esp.",
            },
            relatives = new
            {
                made,
                toMake = story["living"]?.ToString(),
                howTo = "For each living relative the player named who isn't made yet, offer to make them: SpawnNpc with family set to their role " +
                    "(the roles: SetFamilyRole without arguments) and a race that fits the story. SpawnNpc still asks follower, marriage, attraction " +
                    "and home for each; the first home given becomes the family home.",
            },
            note = "Keep the story in mind when the player talks about the family; the secrets stay unspoken unless the player raises them.",
        };
    }
}
