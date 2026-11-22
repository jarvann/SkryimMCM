using System.Numerics;

namespace SkyrimMCP.Face;

/// <summary>
/// FEAT-304: gives a character's face a family look. The way the player's head differs from its plain head mesh is applied, in part,
/// to another face wherever the two surfaces correspond, so it works between races, sexes and head meshes (same vertex count or not).
/// The relative keeps part of their own features. Eyes, mouth, brows and hair move with the skin under them.
/// </summary>
public static class FaceBlend
{
    public sealed record Result(byte[] Bytes, int HeadVertices, bool OwnFeaturesReduced, float AverageMove, float LargestMove, List<string> MovedWith);

    // Where head-space coordinates sit in character space (the head mesh's own transform in the game files): some head parts store one, some the other.
    private static readonly Vector3 HeadBone = new(0f, -1.548f, 120.343f);

    /// <param name="face">The relative's face file.</param>
    /// <param name="playerHead">The player's exported head.</param>
    /// <param name="playerNeutral">The plain mesh of the player's face part.</param>
    /// <param name="neutralOf">The plain mesh of the relative's face part, by its editor ID; null when unknown.</param>
    public static Result Blend(byte[] face, byte[] playerHead, byte[] playerNeutral, Func<string, byte[]?> neutralOf, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        if (amount == 0) return new Result(face, 0, false, 0, 0, []);
        var player = Head(NifDocument.Read(playerHead), out _) ?? throw new InvalidDataException("the exported head has no face shape");
        var plain = FirstShape(NifDocument.Read(playerNeutral)) ?? throw new InvalidDataException("the player's plain head mesh has no shape");
        if (player.Length != plain.Length) throw new InvalidDataException($"the player's head ({player.Length} vertices) and its plain mesh ({plain.Length}) don't match");
        var difference = player.Zip(plain, (a, b) => a - b).ToArray();

        var nif = NifDocument.Read(face);
        var own = Head(nif, out var headShape) ?? throw new InvalidDataException("the face file has no face shape");
        Vector3[]? ownPlain = null;
        try { if (neutralOf(nif.Name(headShape)) is byte[] n) ownPlain = FirstShape(NifDocument.Read(n)); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
        if (ownPlain?.Length != own.Length) ownPlain = null;

        var moved = new Vector3[own.Length];
        for (var i = 0; i < own.Length; i++)
        {
            var at = ownPlain?[i] ?? own[i];
            var family = Interpolate(at, plain, difference, 1.5f, 4f);
            moved[i] = ownPlain == null ? own[i] + amount * family : at + (1 - amount) * (own[i] - at) + amount * family;
        }
        var shift = moved.Zip(own, (a, b) => a - b).ToArray();
        if (shift.Any(s => !float.IsFinite(s.X + s.Y + s.Z))) throw new InvalidDataException("the blend gave impossible positions");
        Write(nif, headShape, moved);

        List<string> with = [];
        for (var s = 0; s < nif.Blocks.Count; s++)
        {
            if (s == headShape || nif.TypeOf(s) != "BSDynamicTriShape" || nif.Lighting(s) == null || Read(nif, s) is not { Length: > 0 } points) continue;
            var changed = points.Select(p => p + Interpolate(p, own, shift, 1.5f, 9f)).ToArray();
            Write(nif, s, changed);
            with.Add(nif.Name(s));
        }
        return new Result(nif.ToBytes(), own.Length, ownPlain != null, shift.Average(v => v.Length()), shift.Max(v => v.Length()), with);
    }

    /// <summary>
    /// A loaded face file carries its own hair tint, and the colour on the character's record doesn't replace it (2026-11-22). Writes it into
    /// every hair-tinted shape (hair, hairline, brows, beard): the game's tint is twice the colour.
    /// </summary>
    public static byte[] HairColour(byte[] face, byte red, byte green, byte blue)
    {
        var nif = NifDocument.Read(face);
        foreach (var shader in Enumerable.Range(0, nif.Blocks.Count).Where(nif.IsShape).Select(nif.Lighting).OfType<(int Shader, int TextureSet, uint Type)>().Where(l => l.Type == 6).Select(l => l.Shader).Distinct())
        {
            var b = nif.Blocks[shader];
            byte[] rgb = [red, green, blue];
            for (var i = 0; i < 3; i++) BitConverter.GetBytes(Math.Min(2f * rgb[i] / 255f, 1f)).CopyTo(b, b.Length - 12 + 4 * i);
        }
        return nif.ToBytes();
    }

    // The values at the four nearest points, by distance; full within 'near', fading to nothing at 'far'.
    private static Vector3 Interpolate(Vector3 at, Vector3[] points, Vector3[] values, float near, float far)
    {
        Span<float> d = [float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue];
        Span<int> k = [-1, -1, -1, -1];
        for (var i = 0; i < points.Length; i++)
        {
            var q = Vector3.DistanceSquared(at, points[i]);
            if (q >= d[3]) continue;
            var j = 3;
            while (j > 0 && d[j - 1] > q) { d[j] = d[j - 1]; k[j] = k[j - 1]; j--; }
            d[j] = q;
            k[j] = i;
        }
        var sum = Vector3.Zero;
        var weights = 0f;
        for (var j = 0; j < 4; j++)
        {
            if (k[j] < 0) continue;
            var w = 1f / (d[j] + 0.01f);
            sum += values[k[j]] * w;
            weights += w;
        }
        if (weights == 0) return Vector3.Zero;
        var nearest = MathF.Sqrt(d[0]);
        return sum / weights * Math.Clamp((far - nearest) / (far - near), 0f, 1f);
    }

    private static Vector3[]? Head(NifDocument nif, out int shape)
    {
        for (shape = 0; shape < nif.Blocks.Count; shape++)
            if (nif.TypeOf(shape) == "BSDynamicTriShape" && nif.Lighting(shape)?.Type == 4) return Read(nif, shape);
        shape = -1;
        return null;
    }

    private static Vector3[]? FirstShape(NifDocument nif)
    {
        for (var s = 0; s < nif.Blocks.Count; s++) if (nif.TypeOf(s) == "BSDynamicTriShape" && Read(nif, s) is { Length: > 0 } p) return p;
        return null;
    }

    // BSDynamicTriShape ends with its vertex positions (four floats each), after their size; the vertex count follows the triangle count.
    private static (int At, int Count)? Dynamic(NifDocument nif, int shape)
    {
        var b = nif.Blocks[shape];
        var count = BitConverter.ToUInt16(b, nif.AlphaAt(shape) + 4 + 8 + 2);
        var at = b.Length - 16 * count;
        return count > 0 && at >= 4 && BitConverter.ToInt32(b, at - 4) == 16 * count ? (at, count) : null;
    }

    private static Vector3[]? Read(NifDocument nif, int shape)
    {
        if (Dynamic(nif, shape) is not { } d) return null;
        var b = nif.Blocks[shape];
        var points = new Vector3[d.Count];
        for (var i = 0; i < d.Count; i++) points[i] = new Vector3(BitConverter.ToSingle(b, d.At + 16 * i), BitConverter.ToSingle(b, d.At + 16 * i + 4), BitConverter.ToSingle(b, d.At + 16 * i + 8));
        if (InCharacterSpace(points)) for (var i = 0; i < points.Length; i++) points[i] -= HeadBone;
        return points;
    }

    private static bool InCharacterSpace(Vector3[] raw) => raw.Average(p => p.Z) > 60f;

    private static void Write(NifDocument nif, int shape, Vector3[] headSpace)
    {
        var d = Dynamic(nif, shape)!.Value;
        var b = nif.Blocks[shape];
        var raw = Enumerable.Range(0, d.Count).Select(i => new Vector3(0, 0, BitConverter.ToSingle(b, d.At + 16 * i + 8))).ToArray();
        var offset = InCharacterSpace(raw) ? HeadBone : Vector3.Zero;
        for (var i = 0; i < d.Count; i++)
        {
            var p = headSpace[i] + offset;
            BitConverter.GetBytes(p.X).CopyTo(b, d.At + 16 * i);
            BitConverter.GetBytes(p.Y).CopyTo(b, d.At + 16 * i + 4);
            BitConverter.GetBytes(p.Z).CopyTo(b, d.At + 16 * i + 8);
        }
    }
}
