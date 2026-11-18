using System.Collections.Generic;

namespace SkyrimMCP;

/// <summary>
/// Memory offsets and FormIDs for Skyrim
/// </summary>
public static class SkyrimOffsets
{
    /// <summary>
    /// Memory offsets for different game versions
    /// NOTE: These are placeholder values and need to be researched/updated
    /// </summary>
    public static class MemoryOffsets
    {
        // Skyrim SE 1.5.97 offsets
        public static class SE_1_5_97
        {
            public static readonly long[] PlayerBase = { 0x2F26A78 };
            public static readonly long[] PlayerLevel = { 0x2F26A78, 0x78, 0xB4 };
            public static readonly long[] PlayerHealth = { 0x2F26A78, 0x78, 0x260 };
            public static readonly long[] PlayerMagicka = { 0x2F26A78, 0x78, 0x268 };
            public static readonly long[] PlayerStamina = { 0x2F26A78, 0x78, 0x270 };
        }

        // Skyrim AE 1.6.x offsets
        public static class AE_1_6_X
        {
            public static readonly long[] PlayerBase = { 0x2F6B948 };
            public static readonly long[] PlayerLevel = { 0x2F6B948, 0x78, 0xB4 };
            public static readonly long[] PlayerHealth = { 0x2F6B948, 0x78, 0x260 };
            public static readonly long[] PlayerMagicka = { 0x2F6B948, 0x78, 0x268 };
            public static readonly long[] PlayerStamina = { 0x2F6B948, 0x78, 0x270 };
        }
    }

    /// <summary>
    /// Known FormIDs for common items
    /// These IDs are consistent across all game versions
    /// </summary>
    public static readonly Dictionary<string, string> Items = new()
    {
        // Currency and basics
        { "gold", "0000000F" },
        { "lockpick", "0000000A" },

        // Weapons
        { "iron_sword", "00012EB7" },
        { "iron_dagger", "0001397E" },
        { "steel_sword", "00013989" },
        { "steel_dagger", "00013983" },

        // Armor
        { "iron_armor", "00012E49" },
        { "iron_helmet", "00012E4D" },
        { "iron_boots", "00012E4B" },
        { "iron_gauntlets", "00012E4C" },

        // Potions
        { "health_potion", "0003EADE" },
        { "health_potion_minor", "0003EADE" },
        { "magicka_potion", "0003EAE1" },
        { "stamina_potion", "0003EAE0" },

        // Ingredients (commonly used)
        { "blue_mountain_flower", "0002E4F4" },
        { "wheat", "0006C5F4" },
        { "salt_pile", "00034CDF" },

        // Soul gems
        { "petty_soul_gem", "0002E4F4" },
        { "lesser_soul_gem", "0002E4FB" },
        { "common_soul_gem", "0002E4FC" },
        { "greater_soul_gem", "0002E4FD" },
        { "grand_soul_gem", "0002E4FF" },
        { "black_soul_gem", "0002E500" },

        // Crafting materials
        { "leather", "000DB5D2" },
        { "leather_strips", "000800E4" },
        { "iron_ingot", "0005ACE4" },
        { "steel_ingot", "0005ACE5" },
        { "gold_ingot", "0005AD9E" },
    };

    /// <summary>
    /// Known cell/location IDs for teleportation
    /// </summary>
    public static readonly Dictionary<string, string> Locations = new()
    {
        // Major cities
        { "whiterun", "whiterun" },
        { "solitude", "solitude" },
        { "windhelm", "windhelm" },
        { "riften", "riften" },
        { "markarth", "markarth" },
        { "winterhold", "winterhold" },
        { "dawnstar", "dawnstar" },
        { "morthal", "morthal" },
        { "falkreath", "falkreath" },

        // Towns and settlements
        { "riverwood", "riverwood" },
        { "rorikstead", "rorikstead" },
        { "ivarstead", "ivarstead" },
        { "dragonbridge", "dragonbridge" },
        { "karthwasten", "karthwasten" },

        // Dungeons and locations
        { "bleak_falls_barrow", "bleakfallsbarrow01" },
        { "helgen", "helgen" },
        { "high_hrothgar", "highhrothgar" },
        { "skyhaven_temple", "skyhaventemple01" },
        { "throat_of_the_world", "throatoftheworldpartb" },

        // Player homes
        { "breezehome", "whiterunbreezehome" },
        { "honeyside", "riftenhoneyside" },
        { "proudspire_manor", "solitudeproudspiremanor" },
        { "vlindrel_hall", "markarthvlindrelhall" },
        { "hjerim", "windhelmhjerim" },
        // Hearthfire homes (BUG-073: "lakeview manor" was refused; BYOHHouse1Exterior worked in game)
        { "lakeview_manor", "byohhouse1exterior" },
        { "lakeview", "byohhouse1exterior" },
        { "windstad_manor", "byohhouse2exterior" },
        { "windstad", "byohhouse2exterior" },
        { "heljarchen_hall", "byohhouse3exterior" },
        { "heljarchen", "byohhouse3exterior" },
    };

    /// <summary>
    /// Try to resolve item name to FormID
    /// </summary>
    public static string? GetItemFormId(string itemName)
    {
        var key = itemName.ToLower().Replace(" ", "_");
        return Items.TryGetValue(key, out var formId) ? formId : null;
    }

    /// <summary>
    /// Try to resolve location name to cell ID
    /// </summary>
    public static string? GetLocationCellId(string locationName)
    {
        var key = locationName.ToLower().Replace(" ", "_");
        return Locations.TryGetValue(key, out var cellId) ? cellId : null;
    }

    /// <summary>
    /// Get all known item names
    /// </summary>
    public static IEnumerable<string> GetAllItems() => Items.Keys;

    /// <summary>
    /// Get all known location names
    /// </summary>
    public static IEnumerable<string> GetAllLocations() => Locations.Keys;
}
