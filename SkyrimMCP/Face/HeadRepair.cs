using System.Text.RegularExpressions;

namespace SkyrimMCP.Face;

/// <summary>
/// BUG-102: makes RaceMenu's exported head (CharGen.ExportHead) into a face file the game accepts on its own, without RaceMenu. Found
/// on Steam, 2026-11-22: (1) some shapes got the face's texture set; (2) a head part with a mesh but no shape in the file makes the game
/// ignore the whole file and build the head without its skin texture (KS Hairdos SMP names a mesh it doesn't contain); (3) physics bones
/// are renamed and all put at the origin, so loose hair bunches up; (4) a hair's physics marker is left out (BUG-107).
/// </summary>
public static partial class HeadRepair
{
    public sealed record Result(byte[] Bytes, List<string> Textures, List<string> StandIns, List<string> Bones, List<string> Problems);

    [GeneratedRegex(@"^hdtSSEPhysics_AutoRename_\w+?_[0-9A-Fa-f]{8} ")]
    private static partial Regex RenamedBone();

    /// <param name="parts">The character's head parts: editor ID and mesh path (null or empty when it has none).</param>
    /// <param name="readMesh">A mesh by its path, or null when no such file exists.</param>
    public static Result Repair(byte[] mesh, IReadOnlyList<(string EditorId, string? Model)> parts, Func<string, byte[]?> readMesh)
    {
        var nif = NifDocument.Read(mesh);
        List<string> textures = [], standIns = [], bones = [], problems = [];
        var models = parts.Where(p => !string.IsNullOrEmpty(p.Model)).GroupBy(p => p.EditorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Model!, StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, NifDocument?>(StringComparer.OrdinalIgnoreCase);
        NifDocument? Source(string model)
        {
            if (sources.TryGetValue(model, out var known)) return known;
            NifDocument? read = null;
            try { if (readMesh(model) is byte[] b) read = NifDocument.Read(b); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or IOException) { }
            return sources[model] = read;
        }
        var shapes = Enumerable.Range(0, nif.Blocks.Count).Where(nif.IsShape).ToList();

        // 1. Shapes given the face's textures take their own part's.
        var face = shapes.Select(s => nif.Lighting(s)).FirstOrDefault(l => l?.Type == 4);
        if (face is { } f)
        {
            var faceTextures = nif.Textures(f.TextureSet);
            foreach (var shape in shapes)
            {
                if (nif.Lighting(shape) is not { } light || light.Type == 4 || light.TextureSet == f.TextureSet) continue;
                if (!nif.Textures(light.TextureSet).SequenceEqual(faceTextures)) continue;
                var name = nif.Name(shape);
                var source = models.TryGetValue(name, out var model) ? Source(model) : null;
                var theirs = source == null ? [] : Enumerable.Range(0, source.Blocks.Count).Where(source.IsShape).Select(s => source.Lighting(s)).OfType<(int Shader, int TextureSet, uint Type)>().ToList();
                if (theirs.Count == 0) { problems.Add($"{name}: has the face's textures and its own weren't found"); continue; }
                var from = theirs.Any(t => t.Type == light.Type) ? theirs.First(t => t.Type == light.Type) : theirs[0];
                nif.SetTextures(light.TextureSet, source!.Textures(from.TextureSet));
                textures.Add(name);
            }
        }

        // 2. An empty, invisible shape for each part the export left out.
        var present = shapes.Select(nif.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var faceNode = Enumerable.Range(0, nif.Blocks.Count).FirstOrDefault(i => nif.IsNode(i) && nif.Name(i) == "BSFaceGenNiNodeSkinned", -1);
        var smallest = shapes.Where(s => nif.TypeOf(nif.Int(s, nif.SkinAt(s))) is "NiSkinInstance" or "BSDismemberSkinInstance").OrderBy(s => nif.Blocks[s].Length).FirstOrDefault(-1);
        foreach (var (editorId, model) in models)
        {
            if (present.Contains(editorId)) continue;
            if (faceNode < 0 || smallest < 0) { problems.Add($"{editorId}: not in the exported head and no shape to stand in for it"); continue; }
            StandIn(nif, faceNode, smallest, editorId);
            standIns.Add(editorId);
            problems.Add(Source(model) == null
                ? $"{editorId}: its mesh ({model}) isn't installed, so it shows nothing in any face"
                : $"{editorId}: RaceMenu didn't export it, so it won't be seen on this character");
        }

        // 3. Physics markers: a hair's mesh names its physics file in an "HDT Skinned Mesh Physics Object" string on its root, and the
        //    export leaves it out, so FSMP gave a clone's KS hair no physics (BUG-107, GOG 2026-11-23). Copied onto the face file's root.
        var root = faceNode >= 0 ? faceNode : 0;
        var have = nif.ExtraData(root).Select(nif.StringExtraData).OfType<(string Name, string Text)>().ToHashSet();
        foreach (var (editorId, model) in models)
        {
            if (Source(model) is not { } source) continue;
            for (var i = 0; i < source.Blocks.Count; i++)
            {
                if (source.StringExtraData(i) is not { } marker || !marker.Name.StartsWith("HDT", StringComparison.OrdinalIgnoreCase)) continue;
                if (!have.Add(marker)) continue;
                nif.AddExtraData(root, nif.AddStringExtraData(marker.Name, marker.Text));
                bones.Add($"{editorId}: physics {marker.Text}");
            }
        }

        // 4. Physics bones: their own names, at rest where their part's mesh has them relative to the head.
        var taken = nif.Strings.ToHashSet();
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (!nif.IsNode(i)) continue;
            var name = nif.Name(i);
            var own = RenamedBone().Replace(name, "");
            if (own == name) continue;
            if (!taken.Add(own)) { problems.Add($"{name}: another bone is already called {own}"); continue; }
            nif.SetInt(i, 0, nif.AddString(own));
            bones.Add(own);
            foreach (var model in models.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Source(model) is not { } source || RelativeToHead(source, own) is not { } rest) continue;
                nif.SetTransform(i, rest.Translation, rest.Rotation);
                break;
            }
        }
        var untouched = textures.Count + standIns.Count + bones.Count == 0;
        return new Result(untouched ? mesh : nif.ToBytes(), textures, standIns, bones, problems);
    }

    private static void StandIn(NifDocument nif, int faceNode, int from, string name)
    {
        var skin = nif.Int(from, nif.SkinAt(from));
        var (data, partition) = (nif.Int(skin, 0), nif.Int(skin, 4));
        int Copy(int block) => nif.Add(nif.BlockTypes[block], (byte[])nif.Blocks[block].Clone());
        var shape = Copy(from);
        var skinCopy = Copy(skin);
        if (data >= 0) nif.SetInt(skinCopy, 0, Copy(data));
        if (partition >= 0) nif.SetInt(skinCopy, 4, Copy(partition));
        nif.SetInt(shape, 0, nif.AddString(name));
        nif.SetInt(shape, nif.SkinAt(shape), skinCopy);
        nif.SetInt(shape, nif.ShaderAt(shape), -1);   // nothing to draw with: invisible
        nif.SetInt(shape, nif.AlphaAt(shape), -1);
        nif.AddChild(faceNode, shape);
    }

    private static (float[] Translation, float[] Rotation)? RelativeToHead(NifDocument nif, string bone)
    {
        var parent = new Dictionary<int, int>();
        var node = -1;
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (!nif.IsNode(i)) continue;
            if (nif.Name(i) == bone) node = i;
            foreach (var c in nif.Children(i)) parent[c] = i;
        }
        if (node < 0) return null;
        float[] t = [0, 0, 0], r = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        for (var steps = 0; steps < 64; steps++)
        {
            if (nif.Name(node) == "NPC Head [Head]") return (t, r);
            var (nt, nr) = nif.Transform(node);
            t = Enumerable.Range(0, 3).Select(a => nt[a] + nr[3 * a] * t[0] + nr[3 * a + 1] * t[1] + nr[3 * a + 2] * t[2]).ToArray();
            r = Enumerable.Range(0, 9).Select(k => nr[3 * (k / 3)] * r[k % 3] + nr[3 * (k / 3) + 1] * r[3 + k % 3] + nr[3 * (k / 3) + 2] * r[6 + k % 3]).ToArray();
            if (!parent.TryGetValue(node, out node)) return null;
        }
        return null;
    }
}
