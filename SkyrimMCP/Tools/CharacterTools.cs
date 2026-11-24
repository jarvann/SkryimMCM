using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SkyrimMCP.Face;

namespace SkyrimMCP.Tools;

/// <summary>Characters as mods (1.6): what a character is made of and which mods that needs (FEAT-296).</summary>
[McpServerToolType]
public partial class CharacterTools : ToolBase
{
    public CharacterTools(IPipeClient pipe) : base(pipe) { }

    private static readonly string[] BaseGame = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];
    private static bool IsCreation(string plugin) => System.Text.RegularExpressions.Regex.IsMatch(plugin, @"^cc[a-z]{3}sse\d{3}-", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private sealed class Need
    {
        public readonly HashSet<string> Plugins = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Why = [];
        public bool Required;
        public int Files;
    }

    [McpServerTool]
    [Description("Which mods a spawned character needs (FEAT-296), for sharing it as a mod: its race, head parts, hair, voice, skin, spells and the " +
        "textures and physics files its own face file uses, each traced to the plugin and the installed mod (Vortex or MO2) with its Nexus page. " +
        "required: in the character itself. optional: what it wears or carries, which may have been handed out by a distributor (SPID and " +
        "similar) and isn't part of the character. Base game files aren't listed; Creation Club ones are named separately. refId: the character.")]
    public async Task<object> CharacterRequirements(string refId)
    {
        JsonNode? data;
        try { data = await _pipe.SendRequestAsync("character_forms", new JsonObject { ["refId"] = refId.Trim() }); }
        catch (McpException ex) { return new { error = ex.Message }; }
        if (data?["error"] != null) return DeserializeResponse(data);
        return await Task.Run(() => Requirements(data!));
    }

    private static object Requirements(JsonNode data)
    {
        var forms = (data["forms"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var wanted = new Dictionary<string, List<(string Why, bool Required)>>(StringComparer.OrdinalIgnoreCase);   // Data-relative file -> why
        void Want(string file, string why, bool required)
        {
            var key = ModSources.Normal(file);
            if (!wanted.TryGetValue(key, out var list)) wanted[key] = list = [];
            list.Add((why, required));
        }
        // Perks, spells and factions from mods come from distributors (SPID and the like) that give them to every NPC: an exported
        // character leaves them out, and whoever has those mods gets them again.
        var handedOut = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var creations = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in forms)
        {
            var plugin = f["plugin"]?.ToString() ?? "";
            if (BaseGame.Contains(plugin, StringComparer.OrdinalIgnoreCase) || plugin.Equals("SkyLinkAI.esp", StringComparison.OrdinalIgnoreCase)) continue;
            if (f["kind"]?.ToString() is "perk" or "spell" or "faction") { handedOut[plugin] = handedOut.GetValueOrDefault(plugin) + 1; continue; }
            if (IsCreation(plugin)) { creations.Add(plugin); continue; }
            var inRecord = f["need"]?.ToString() == "record";
            // BUG-114: worn but made for another race, so nothing shows; the export leaves it out.
            var kind = f["noModelForRace"]?.GetValue<bool>() == true ? "worn but no model for this race (left out of an export)" : f["kind"]?.ToString();
            Want(plugin, $"{kind}: {f["name"]?.ToString() ?? f["editorId"]?.ToString() ?? f["stableId"]?.ToString()}", inRecord);
        }

        // The face file's own textures, and the physics settings its hair names.
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string? faceNote = null;
        if (data["slot"] is JsonValue slot && SlotRegistry.Load()[slot.ToString()] is JsonObject entry)
        {
            var files = SlotRegistry.FaceFiles(entry["objectId"]?.ToString() ?? "");
            if (files is { } face && File.Exists(face.mesh))
            {
                try
                {
                    var nif = NifDocument.Read(File.ReadAllBytes(face.mesh));
                    for (var s = 0; s < nif.Blocks.Count; s++)
                    {
                        if (!nif.IsShape(s) || nif.Lighting(s) is not { } light) continue;
                        foreach (var t in nif.Textures(light.TextureSet).Where(t => t.Length > 0))
                        {
                            var replaces = BaseGameTexture(DataPath(t));
                            Want(DataPath(t), $"{(replaces ? "replaces a base game texture" : "face file texture")} ({nif.Name(s)})", !replaces);
                        }
                    }
                }
                catch (InvalidDataException ex) { faceNote = "The face file couldn't be read: " + ex.Message; }
            }
            else faceNote = "This character has no face file of its own yet.";
        }
        foreach (var model in forms.Where(f => f["kind"]?.ToString() == "head part").Select(f => f["model"]?.ToString()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Want(Path.Combine("meshes", model), "head part mesh", true);
            if (MeshFiles.Read(model) is not byte[] bytes) { missing.Add(model); continue; }
            try
            {
                var nif = NifDocument.Read(bytes);
                for (var b = 0; b < nif.Blocks.Count; b++)
                    if (nif.TypeOf(b) == "NiStringExtraData" && nif.Name(b).Contains("HDT", StringComparison.OrdinalIgnoreCase) && nif.Int(b, 4) is var i && i >= 0 && i < nif.Strings.Count)
                        Want(DataPath(nif.Strings[i]), "hair physics settings", true);
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException) { }
        }

        var sources = ModSources.Find(wanted.Keys);
        var mods = new Dictionary<string, (ModSources.Mod Mod, Need Need)>(StringComparer.OrdinalIgnoreCase);
        var unknown = new Dictionary<string, Need>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, reasons) in wanted)
        {
            var isPlugin = file.IndexOf('\\') < 0;
            if (!sources.TryGetValue(file, out var mod))
            {
                // Not loose: in an archive (the base game's, or that of a plugin listed here). Only an untraced plugin is worth reporting.
                if (!isPlugin) continue;
                unknown.TryAdd(file, new Need());
                foreach (var (why, req) in reasons) { unknown[file].Why.Add(why); unknown[file].Required |= req; }
                continue;
            }
            if (mod.Folder.Contains("SkyLink", StringComparison.OrdinalIgnoreCase)) continue;
            if (!mods.TryGetValue(mod.Folder, out var known)) mods[mod.Folder] = known = (mod, new Need());
            if (isPlugin) known.Need.Plugins.Add(file); else known.Need.Files++;
            foreach (var (why, req) in reasons) { known.Need.Why.Add(why); known.Need.Required |= req; }
        }
        object Row(ModSources.Mod m, Need n) => new
        {
            mod = m.Name, nexusId = m.NexusId, link = m.Link, plugins = n.Plugins.Count == 0 ? null : n.Plugins.Order(), looseFiles = n.Files,
            why = n.Why.Order().Take(8), moreReasons = n.Why.Count > 8 ? n.Why.Count - 8 : (int?)null,
        };
        var required = mods.Values.Where(m => m.Need.Required).OrderBy(m => m.Mod.Name).ToList();
        var optional = mods.Values.Where(m => !m.Need.Required).OrderBy(m => m.Mod.Name).ToList();
        return new
        {
            character = data["name"]?.ToString(),
            required = required.Select(m => Row(m.Mod, m.Need)),
            optional = optional.Select(m => Row(m.Mod, m.Need)),
            untraced = unknown.Count == 0 ? null : unknown.Select(u => new { plugin = u.Key, required = u.Value.Required, why = u.Value.Why.Order().Take(6) }),
            handedOutByOtherMods = handedOut.Count == 0 ? null : new { perksSpellsAndFactions = handedOut.Values.Sum(), plugins = handedOut.Count,
                note = "Given to every NPC by other mods (SPID and similar). Not part of the character: an export leaves them out." },
            creationClub = creations.Count == 0 ? null : creations.ToList(),
            missingMeshes = missing.Count == 0 ? null : missing.ToList(),
            faceNote,
            note = "optional also has mods that only replace base game textures (skins, mouths, eyes): the character shows them but works without. " +
                   "Files packed in archives count with their plugin.",
        };
    }

    private static List<BsaArchive>? _baseTextures;

    private static bool BaseGameTexture(string dataPath)
    {
        if (!dataPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || SkyLinkSettings.DataPath() is not string data) return false;
        if (_baseTextures == null)
        {
            var list = new List<BsaArchive>();
            foreach (var file in Directory.EnumerateFiles(data, "Skyrim - Textures*.bsa"))
                if (BsaArchive.Open(file) is { } bsa) list.Add(bsa);
            _baseTextures = list;
        }
        return _baseTextures.Any(b => b.Contains(dataPath));
    }

    // Texture and physics paths as meshes write them ("textures\...", "Data\textures\...", "Actors\...").
    private static string DataPath(string path)
    {
        var p = ModSources.Normal(path);
        if (p.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        return p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) ? "textures\\" + p : p;
    }
}
