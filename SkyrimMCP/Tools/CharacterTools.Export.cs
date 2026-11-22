using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SkyrimMCP.Face;
using SkyrimMCP.Plugins;

namespace SkyrimMCP.Tools;

/// <summary>
/// FEAT-283: a character as the player's own mod: an NPC record in a light plugin, its face files, its hair colour and outfit, and a small
/// quest that places it at an existing base game marker (no script and no edited cell, so it doesn't clash with mods that change the place).
/// It needs neither SkyLink nor RaceMenu to run.
/// </summary>
public partial class CharacterTools
{
    [McpServerTool]
    [Description("Export a spawned character as the player's own standalone mod (FEAT-283): a light plugin with the character, its face files, hair " +
        "colour, voice and outfit, placed in the world without scripts or cell edits, plus a README with the mods it needs, zipped. Needs neither " +
        "SkyLink nor RaceMenu afterwards. Written to SkyLink's Build folder, never into Data. modName: the plugin's name. outfit: 'worn' (what it " +
        "wears now) or 'none'. follower: can be recruited (default: if it is one now). home: a reference from homeChoices (default the nearest " +
        "marker). textureSize: of the face's skin texture (1024 or 2048). author: shown in the plugin. dryRun (default true) says what would be made, " +
        "with the requirements and whose meshes the face file contains (permission to share those is theirs to give). Player Family membership isn't " +
        "exported (the faction is SkyLink's plugin). To see it: install it, remove the spawned one, start a save.")]
    public async Task<object> ExportCharacter(string refId, string modName, string? author = null, string outfit = "worn", bool? follower = null,
        string? home = null, int textureSize = 1024, bool dryRun = true)
    {
        modName = (modName ?? "").Trim();
        if (modName.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) modName = modName[..^4];
        if (modName.Length is < 3 or > 60 || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return new { error = "modName: 3 to 60 characters that can be in a file name." };
        outfit = outfit.Trim().ToLowerInvariant();
        if (outfit is not ("worn" or "none")) return new { error = "outfit must be 'worn' or 'none'" };
        if (textureSize is not (1024 or 2048)) return new { error = "textureSize must be 1024 or 2048" };
        refId = refId.Trim();
        JsonNode? data, markers;
        try
        {
            data = await _pipe.SendRequestAsync("character_forms", new JsonObject { ["refId"] = refId });
            markers = await _pipe.SendRequestAsync("home_markers", new JsonObject { ["refId"] = refId });
        }
        catch (McpException ex) { return new { error = ex.Message }; }
        if (data?["slot"] is not JsonValue slot) return new { error = "Only characters in SkyLink's NPC slots can be exported (SkyLinkAI.esp). This one is a preview or not SkyLink's." };
        if (SlotRegistry.Load()[slot.ToString()] is not JsonObject entry || SlotRegistry.FaceFiles(entry["objectId"]!.ToString()) is not { } face || !File.Exists(face.mesh) || !File.Exists(face.tint))
            return new { error = "This character has no face files of its own, so its face would be wrong in the exported mod." };

        var forms = (data["forms"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        string? One(string kind) => forms.FirstOrDefault(f => f["kind"]?.ToString() == kind)?["stableId"]?.ToString();
        var isFollower = follower ?? forms.Any(f => f["kind"]?.ToString() == "faction" && f["stableId"]?.ToString() == "Skyrim.esm|05C84D");
        var marry = forms.Any(f => f["kind"]?.ToString() == "faction" && f["stableId"]?.ToString() == "Skyrim.esm|019809") && entry["family"]?.GetValue<string?>() == null;
        var choices = (markers?["markers"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var homeRef = home?.Trim() ?? (choices.FirstOrDefault(m => m["marker"]?.GetValue<bool>() == true) ?? choices.FirstOrDefault())?["stableId"]?.ToString();
        if (homeRef == null) return new { error = "No lasting base game reference was found in this place to put the character at. Move it somewhere else (an inn, a town) and export again." };
        if (home != null && choices.All(m => m["stableId"]?.ToString() != homeRef)) return new { error = "home isn't one of homeChoices.", homeChoices = choices };

        var id = new string(modName.Where(char.IsLetterOrDigit).ToArray());
        if (id.Length == 0 || !char.IsLetter(id[0])) id = "Char" + id;
        var look = data["faceData"] as JsonObject;
        (byte, byte, byte)? Rgb(string key) => look?[key]?.ToString() is { Length: 7 } h ? (Convert.ToByte(h[1..3], 16), Convert.ToByte(h[3..5], 16), Convert.ToByte(h[5..7], 16)) : null;
        var records = new List<NewRecord>();
        List<(string, sbyte)> factions = [];
        if (isFollower) { factions.Add(("Skyrim.esm|0005C84D", 0)); factions.Add(("Skyrim.esm|0005C84E", -1)); }
        if (marry) factions.Add(("Skyrim.esm|00019809", 0));
        var worn = outfit == "worn" ? forms.Where(f => f["kind"]?.ToString() == "worn").Select(f => f["stableId"]?.ToString()).OfType<string>().Distinct().ToList() : [];
        var hair = Rgb("hairColor");
        records.Add(new NewNpc
        {
            EditorId = id, ObjectId = 0x800, Name = data["name"]?.ToString(), Female = data["sex"]?.ToString() == "female", Unique = true,
            Essential = data["essential"]?.GetValue<bool>() == true, Protected = isFollower || data["protected"]?.GetValue<bool>() == true,
            Race = One("race")!, Voice = One("voice"), Class = One("class"), CombatStyle = One("combat style"), Skin = One("skin"),
            Packages = [isFollower ? "Skyrim.esm|0x000956B8" : One("package") ?? "Skyrim.esm|0x000956B8"], Factions = factions,
            HeadParts = forms.Where(f => f["kind"]?.ToString() == "head part").Select(f => f["stableId"]?.ToString()).OfType<string>().ToList(),
            HairColour = hair != null ? id + "HairColour" : null, HeadTexture = One("head texture set"), BodyTint = Rgb("bodyTint"),
            Outfit = worn.Count > 0 ? id + "Outfit" : null, LevelsWithPlayer = isFollower, Level = (ushort)Math.Clamp(data["level"]?.GetValue<int>() ?? 1, 1, 200),
            Aggression = (byte)(isFollower ? 1 : 0), Confidence = (byte)(isFollower ? 4 : 2), Assistance = (byte)(isFollower ? 2 : 0),
            Height = data["height"]?.GetValue<float>() is float tall && tall > 0.1f ? tall : 1f, Weight = look?["weight"]?.GetValue<float>() ?? 50f,
        });
        if (hair is { } hc) records.Add(new NewColour { EditorId = id + "HairColour", ObjectId = 0x801, Red = hc.Item1, Green = hc.Item2, Blue = hc.Item3 });
        if (worn.Count > 0) records.Add(new NewOutfit { EditorId = id + "Outfit", ObjectId = 0x802, Items = worn });
        // The recruit line asks for an ally: the relationship is written, as vanilla does for housecarls (an exported one had none).
        if (isFollower || entry["family"]?.GetValue<string?>() != null)
            records.Add(new NewPlayerRelationship { EditorId = id + "PlayerRelationship", ObjectId = 0x804, Npc = id, Rank = 1 });
        // Starts with the game and keeps running: the character is made once, at the marker.
        records.Add(new NewQuest
        {
            EditorId = id + "Home", ObjectId = 0x803, StartGameEnabled = true, RunOnce = true, Priority = 10,
            Aliases = [new NewAlias(0, "Home", Forced: homeRef), new NewAlias(1, "Character", Create: id, CreateAt: 0)],
        });

        var plugin = modName + ".esp";
        BuiltPlugin built;
        try
        {
            LoadOrderState? lo = null;
            try { lo = LoadOrder.Read(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            int? Position(string m) { var b = Array.FindIndex(LoadOrder.BaseMasters, x => x.Equals(m, StringComparison.OrdinalIgnoreCase)); return b >= 0 ? b : lo?.IndexOf(m); }
            built = PluginWriter.Build(new NewPlugin { Name = plugin, Author = author ?? "", Description = $"{data["name"]}, made with SkyLink AI.", Light = true, Masters = ["Skyrim.esm"], Records = records }, Position);
        }
        catch (ArgumentException ex) { return new { error = ex.Message }; }
        if (built.Masters.Any(m => m.Equals("SkyLinkAI.esp", StringComparison.OrdinalIgnoreCase)))
            return new { error = "Something in this character still depends on SkyLinkAI.esp, so it wouldn't stand alone. Nothing was written.", masters = built.Masters };

        const string objectId = "00000800";
        byte[] mesh, tint;
        try
        {
            mesh = NifTextures.Replace(File.ReadAllBytes(face.mesh), p => p.Contains(@"facetint\", StringComparison.OrdinalIgnoreCase)
                ? $@"data\textures\actors\character\facegendata\facetint\{plugin}\{objectId}.dds" : null).Bytes;
            tint = TintTexture.Shrink(File.ReadAllBytes(face.tint), textureSize);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return new { error = "The face files couldn't be read: " + ex.Message }; }

        var requirements = JsonSerializer.SerializeToNode(await Task.Run(() => Requirements(data!))) as JsonObject ?? new JsonObject();
        if (outfit == "none") requirements["optional"] = new JsonArray();
        // A mod whose plugin is a master has to be there, worn or not.
        if (requirements["optional"] is JsonArray optional && requirements["required"] is JsonArray needed)
            foreach (var m in optional.OfType<JsonObject>().Where(m => (m["plugins"] as JsonArray ?? []).Any(p => built.Masters.Contains(p?.ToString() ?? "", StringComparer.OrdinalIgnoreCase))).ToList())
            {
                optional.Remove(m);
                needed.Add(m);
            }
        var embedded = EmbeddedMeshes(mesh, forms);
        var folder = Path.Combine(SkyLinkSettings.Folder, "Build", modName);
        var files = new Dictionary<string, byte[]>
        {
            [plugin] = built.Bytes,
            [$@"meshes\actors\character\facegendata\facegeom\{plugin}\{objectId}.nif"] = mesh,
            [$@"textures\actors\character\facegendata\facetint\{plugin}\{objectId}.dds"] = tint,
            [$"{modName} README.txt"] = Encoding.UTF8.GetBytes(Readme(modName, data, built, requirements, embedded, worn.Count, isFollower)),
        };
        if (!dryRun)
        {
            try
            {
                foreach (var (rel, bytes) in files)
                {
                    var target = Path.Combine(folder, "Data", rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                }
                var zip = Path.Combine(folder, modName + ".zip");
                if (File.Exists(zip)) File.Delete(zip);
                using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
                foreach (var (rel, bytes) in files)
                {
                    using var s = archive.CreateEntry(rel.Replace('\\', '/'), CompressionLevel.Optimal).Open();
                    s.Write(bytes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new { error = "Couldn't write the mod: " + ex.Message }; }
        }
        return new
        {
            dryRun, character = data["name"]?.ToString(), plugin, masters = built.Masters, light = (built.Flags & 0x200) != 0,
            follower = isFollower, marryable = marry, outfitItems = worn.Count,
            placedAt = new { place = markers?["cell"]?.ToString(), reference = homeRef, what = choices.FirstOrDefault(m => m["stableId"]?.ToString() == homeRef)?["what"]?.ToString() },
            homeChoices = choices.Take(6),
            files = files.Select(f => new { file = f.Key, kb = f.Value.Length / 1024 }),
            zip = dryRun ? null : Path.Combine(folder, modName + ".zip"),
            requirements,
            meshesInTheFaceFile = embedded,
            sharing = "Before sharing it (Nexus included): the face file contains other authors' meshes (meshesInTheFaceFile), so check each mod's permissions and credit them; list the required mods. For your own game no permission is needed.",
            next = dryRun ? "dryRun: false writes it to SkyLink's Build folder." : "Install the zip with the mod manager, start the game, load a save: the character appears at its place. Remove the spawned one (RemoveSpawnedNpc) so there aren't two.",
        };
    }

    /// <summary>Head parts of other mods whose meshes are inside the face file (hair, brows, eyes...).</summary>
    private static List<object> EmbeddedMeshes(byte[] mesh, List<JsonObject> forms)
    {
        var nif = NifDocument.Read(mesh);
        var names = Enumerable.Range(0, nif.Blocks.Count).Where(b => nif.IsShape(b) && nif.Lighting(b) != null).Select(nif.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return forms.Where(f => f["kind"]?.ToString() == "head part" && names.Contains(f["editorId"]?.ToString() ?? ""))
            .Where(f => !BaseGame.Contains(f["plugin"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase))
            .Select(f => (object)new { part = f["editorId"]?.ToString(), plugin = f["plugin"]?.ToString() }).ToList();
    }

    private static string Readme(string modName, JsonNode data, BuiltPlugin built, JsonObject requirements, List<object> embedded, int wornItems, bool isFollower)
    {
        var t = new StringBuilder();
        t.AppendLine(modName).AppendLine(new string('=', modName.Length)).AppendLine();
        t.AppendLine($"{data["name"]}: a standalone character{(isFollower ? " and follower" : "")} made with SkyLink AI. Neither SkyLink AI nor RaceMenu is needed to use it.").AppendLine();
        t.AppendLine("Required plugins (masters): " + string.Join(", ", built.Masters)).AppendLine();
        void List(string title, string key)
        {
            if (requirements[key] is not JsonArray { Count: > 0 } mods) return;
            t.AppendLine(title);
            foreach (var m in mods.OfType<JsonObject>())
                t.AppendLine($"- {m["mod"]}{(m["link"] is JsonNode link ? " " + link : "")}: {string.Join("; ", (m["why"] as JsonArray ?? []).Take(3).Select(w => w?.ToString()))}");
            t.AppendLine();
        }
        List("Needed mods:", "required");
        if (wornItems > 0) List("Worn items and textures (optional unless listed as a master):", "optional");
        if (embedded.Count > 0)
        {
            t.AppendLine("The face file contains meshes from these mods. Check their permissions and credit their authors before sharing:");
            foreach (var p in embedded.Select(e => JsonSerializer.SerializeToNode(e)).OfType<JsonObject>().Select(e => e["plugin"]?.ToString()).Distinct()) t.AppendLine("- " + p);
            t.AppendLine();
        }
        t.AppendLine("Install with a mod manager. The character appears at their place the next time a save is loaded with the plugin on.");
        return t.ToString();
    }
}
