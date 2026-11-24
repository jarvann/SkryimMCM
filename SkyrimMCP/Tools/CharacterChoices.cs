namespace SkyrimMCP.Tools;

/// <summary>
/// The questions the AI must put to the player before a character is made: in the game (SpawnNpc) or in a mod (ExportCharacter).
/// Cory, 2026-11-23: "when making any NPC of any type, the AI should confirm if it needs to be a follower", and whether it's marryable.
/// </summary>
internal static class CharacterChoices
{
    /// <summary>BUG-111: the game counts a house as the player's when its cells are marked so, which many mods do before it's bought.</summary>
    public const string HouseNote = "Houses listed are marked as the player's in the game; many mods (and Hearthfire) mark them before they're bought, so offer only the ones the player says are theirs.";

    /// <summary>
    /// The unanswered questions, or null when every one is answered. familyRole: a Player Family role, never marryable unless roleMarryable.
    /// family: for an export of a relative, whether the role is kept (null: not yet asked). current: what the character is now, shown
    /// to the player as the likely answer.
    /// </summary>
    public static object? Ask(string name, bool? follower, bool? marryable, string? familyRole, string note,
        bool askFamily = false, bool? family = null, (bool follower, bool marryable)? current = null,
        IReadOnlyList<string>? attractionMods = null, string? attraction = null, bool roleMarryable = false,
        IReadOnlyList<string>? homeOptions = null, string? home = null, bool firstFamilyHome = false)
    {
        var asked = new List<string>();
        var questions = new List<object>();
        void Add(string parameter, string question, params string[] options)
        {
            asked.Add(parameter);
            questions.Add(new { parameter, askThePlayer = question, options });
        }

        var familyOpen = askFamily && familyRole != null && family == null;
        if (familyOpen)
            Add("family", $"{name} is the player's {familyRole} (Player Family). Keep that in the exported mod? Keeping it makes the mod " +
                "need SkyLink AI installed (the faction is in SkyLinkAI.esp); dropping it keeps the mod fully standalone.",
                "family: true (keep the role, needs SkyLink AI)", "family: false (fully standalone)");
        if (follower == null)
            Add("follower", $"Should {name} be a follower, recruitable with the game's own dialogue?" + Now(current?.follower, "a follower"),
                "follower: true", "follower: false");
        // A relative is marryable only in a role that allows it (step-siblings); once an export drops the role, the question stands again.
        var relative = familyRole != null && !roleMarryable && !(askFamily && family == false);
        if (marryable == null && (!relative || familyOpen))
            Add("marryable", (familyOpen ? "Only if family is false: c" : "C") + $"an {name} be married (the Amulet of Mara dialogue)? Adults only." +
                Now(current?.marryable, "marryable"), "marryable: true", "marryable: false");
        // Every character lives somewhere (FEAT-307): where it stands, one of the player's houses, the family home or a SkyLink home.
        if (homeOptions is { Count: > 0 } && home == null)
            Add("home", $"Where does {name} live?" + (firstFamilyHome
                    ? " They're family and no family home is set yet: the house chosen becomes the family home (it can be changed with SetFamilyHome)."
                    : "") + " " + HouseNote,
                homeOptions.Select(o => "home: " + o).ToArray());
        // Asked only when a mod uses it (Cory, 2026-11-23); open to every orientation, none assumed.
        if (attractionMods is { Count: > 0 } && attraction == null)
            Add("attraction", $"Who is {name} attracted to? ({string.Join(" and ", attractionMods)} use it.)",
                "attraction: women", "attraction: men", "attraction: everyone", "attraction: no one", "attraction: mods decide");
        return questions.Count == 0 ? null : new { needsChoice = asked, questions, note };
    }

    /// <summary>Null when the attraction is one SkyLink knows (or not given), else why not.</summary>
    public static string? Attraction(string? attraction) =>
        attraction == null || attraction.Trim().ToLowerInvariant() is "women" or "men" or "everyone" or "no one" or "mods decide"
            ? null : "attraction must be women, men, everyone, no one or 'mods decide'";

    private static string Now(bool? now, string what) => now is bool b ? $" (Now {(b ? "" : "not ")}{what}.)" : "";
}
