using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkyrimMCP.Face;

/// <summary>
/// Hair picker (FEAT-236): reads each hair head part's mesh from disk (never loading it in game), measures its shape
/// against the player's head, and draws front and side silhouettes on a contact sheet, so a hairstyle can be chosen
/// by shape instead of by cycling hairs in game. Measures are cached per mesh file (size and date).
/// </summary>
[SupportedOSPlatform("windows")]
public static class HairCatalog
{
    public sealed record HeadBox(float Top, float Bottom, float Front, float Back, float HalfWidth)
    {
        public float Height => Top - Bottom;
        // Vanilla female head (femalehead.nif): z 109.9 to 131.7, y -6.6 to 9.7, x +-6.3.
        public static HeadBox Default => new(131.7f, 109.9f, 9.7f, -6.6f, 6.3f);
    }

    public sealed record Shape(
        string Length, double LengthRatio, double Fringe, double Behind, double Width, double Swept, int Vertices);

    private static string CacheFile => Path.Combine(SkyLinkSettings.Folder, "hair_catalog.json");
    private static JsonObject? _cache;
    private static readonly object CacheLock = new();

    public static HeadBox Head(string? faceModel)
    {
        var bytes = faceModel == null ? null : MeshFiles.Read(faceModel);
        if (bytes == null) return HeadBox.Default;
        try
        {
            var p = NifShape.Positions(bytes);
            if (p.Count < 50) return HeadBox.Default;
            return new HeadBox(p.Max(v => v.Z), p.Min(v => v.Z), p.Max(v => v.Y), p.Min(v => v.Y), p.Max(v => Math.Abs(v.X)));
        }
        catch (Exception) { return HeadBox.Default; }
    }

    /// <summary>The shape of one hair mesh, from the cache when the file hasn't changed; null when the file isn't loose.</summary>
    public static Shape? Measure(string model, HeadBox head)
    {
        var path = MeshFiles.Find(model);
        string stamp;
        if (path != null)
        {
            var info = new FileInfo(path);
            stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{head.Top:0.0}:{head.Bottom:0.0}";
        }
        else if (MeshFiles.Source(model) == "bsa") stamp = $"bsa:{head.Top:0.0}:{head.Bottom:0.0}";
        else return null;
        lock (CacheLock)
        {
            _cache ??= LoadCache();
            if (_cache[model] is JsonObject hit && hit["stamp"]?.GetValue<string>() == stamp)
                return hit["shape"].Deserialize<Shape>();
        }
        Shape shape;
        try
        {
            var bytes = MeshFiles.Read(model);
            if (bytes == null) return null;
            shape = Compute(NifShape.Positions(bytes), head);
        }
        catch (Exception) { return null; }
        lock (CacheLock)
        {
            _cache![model] = new JsonObject { ["stamp"] = stamp, ["shape"] = JsonSerializer.SerializeToNode(shape) };
        }
        return shape;
    }

    public static void SaveCache()
    {
        lock (CacheLock)
        {
            if (_cache == null) return;
            Directory.CreateDirectory(SkyLinkSettings.Folder);
            File.WriteAllText(CacheFile, _cache.ToJsonString());
        }
    }

    private static JsonObject LoadCache()
    {
        try { if (File.Exists(CacheFile)) return JsonNode.Parse(File.ReadAllText(CacheFile)) as JsonObject ?? new JsonObject(); }
        catch (Exception) { }
        return new JsonObject();
    }

    private static Shape Compute(List<(float X, float Y, float Z)> p, HeadBox h)
    {
        if (p.Count == 0) return new Shape("unknown", 0, 0, 0, 0, 0, 0);
        // Length: how far below the top of the head the hair reaches, in head heights (top to neck).
        var minZ = p.Min(v => v.Z);
        var ratio = (h.Top - minZ) / h.Height;
        var length = ratio switch
        {
            < 0.75f => "short",
            < 1.15f => "bob",
            < 1.6f => "shoulder",
            < 2.4f => "long",
            _ => "very long",
        };
        // Fringe: how much of the forehead (the band above the brows, in front of the face) the hair covers.
        var browZ = h.Bottom + 0.6f * h.Height;
        var hairline = h.Bottom + 0.85f * h.Height;
        var front = h.Front - 0.35f * (h.Front - h.Back);  // the forehead sits behind the nose tip
        var cells = new HashSet<(int, int)>();
        foreach (var v in p)
            if (v.Y >= front && v.Z >= browZ && v.Z <= hairline && Math.Abs(v.X) <= h.HalfWidth * 0.6f)
                cells.Add(((int)Math.Floor(v.X), (int)Math.Floor(v.Z * 2)));
        var cellTotal = Math.Max(1, (int)Math.Ceiling(h.HalfWidth * 1.2f) * (int)Math.Ceiling((hairline - browZ) * 2));
        var fringe = Math.Min(1.0, (double)cells.Count / cellTotal);
        // Behind: share of the hair behind the head between neck and crown (buns, ponytails, hair down the back).
        var behind = p.Count(v => v.Y < h.Back - 1 && v.Z > h.Bottom - 5) / (double)p.Count;
        var minX = p.Min(v => v.X);
        var maxX = p.Max(v => v.X);
        var width = (maxX - minX) / (2 * h.HalfWidth);
        var swept = Math.Abs(maxX + minX) / Math.Max(1e-3, maxX - minX);
        return new Shape(length, Math.Round(ratio, 2), Math.Round(fringe, 2), Math.Round(behind, 2), Math.Round(width, 2), Math.Round(swept, 2), p.Count);
    }

    /// <summary>A contact sheet: per hair, a front and a side silhouette over the head's outline, labelled.</summary>
    public static string DrawSheet(IReadOnlyList<(string Label, string Model)> hairs, string? faceModel, string outPath)
    {
        const float pxPerUnit = 3.3f;
        const int viewW = 110, viewH = 205, labelH = 30, pad = 6;
        const int tileW = viewW * 2 + pad * 3, tileH = viewH + labelH + pad;
        const int columns = 4;
        var rows = (hairs.Count + columns - 1) / columns;
        var head = HeadBox.Default;
        List<(float X, float Y, float Z)> headPoints = new();
        if (faceModel != null && MeshFiles.Read(faceModel) is byte[] faceBytes)
        {
            try
            {
                headPoints = NifShape.Positions(faceBytes);
                if (headPoints.Count > 50)
                    head = new HeadBox(headPoints.Max(v => v.Z), headPoints.Min(v => v.Z), headPoints.Max(v => v.Y), headPoints.Min(v => v.Y), headPoints.Max(v => Math.Abs(v.X)));
            }
            catch (Exception) { headPoints = new(); }
        }
        var topZ = head.Top + 6;

        using var bmp = new Bitmap(Math.Max(1, columns * tileW), Math.Max(1, rows * tileH), PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(245, 243, 238));
        using var font = new Font("Segoe UI", 8f);
        using var headBrush = new SolidBrush(Color.FromArgb(185, 180, 172));
        using var hairBrush = new SolidBrush(Color.FromArgb(120, 70, 40));
        using var backHairBrush = new SolidBrush(Color.FromArgb(214, 186, 160));
        using var frame = new Pen(Color.FromArgb(215, 210, 200));

        for (var i = 0; i < hairs.Count; i++)
        {
            int ox = (i % columns) * tileW, oy = (i / columns) * tileH;
            g.DrawRectangle(frame, ox + 1, oy + 1, tileW - 3, tileH - 3);
            var frontView = new Rectangle(ox + pad, oy + pad, viewW, viewH);
            var sideView = new Rectangle(ox + pad * 2 + viewW, oy + pad, viewW, viewH);
            var midY = (head.Front + head.Back) / 2;
            void Plot(IEnumerable<(float X, float Y, float Z)> pts, Brush brush, int step, bool front, bool side, Func<(float X, float Y, float Z), bool>? keep = null)
            {
                var k = 0;
                foreach (var v in pts)
                {
                    if (k++ % step != 0 || (keep != null && !keep(v))) continue;
                    var fy = frontView.Y + (topZ - v.Z) * pxPerUnit;
                    if (front)
                    {
                        var fx = frontView.X + viewW / 2f + v.X * pxPerUnit;
                        if (frontView.Contains((int)fx, (int)fy)) g.FillRectangle(brush, fx, fy, 1.2f, 1.2f);
                    }
                    if (side)
                    {
                        // side view: the face to the right
                        var sx = sideView.X + viewW / 2f + (v.Y - midY) * pxPerUnit;
                        if (sideView.Contains((int)sx, (int)fy)) g.FillRectangle(brush, sx, fy, 1.2f, 1.2f);
                    }
                }
            }
            List<(float X, float Y, float Z)> hairPoints;
            var bytes = MeshFiles.Read(hairs[i].Model);
            try { hairPoints = bytes == null ? new() : NifShape.Positions(bytes); } catch (Exception) { hairPoints = new(); }
            var step = Math.Max(1, hairPoints.Count / 9000);
            // Front view: hair behind the face light, then the face, then hair in front of it dark, so a fringe shows.
            Plot(hairPoints, backHairBrush, step, front: true, side: false, keep: v => v.Y < midY);
            Plot(headPoints, headBrush, 1, front: true, side: false);
            Plot(hairPoints, hairBrush, step, front: true, side: false, keep: v => v.Y >= midY);
            // Side view: the hair, then the head over it.
            Plot(hairPoints, hairBrush, step, front: false, side: true);
            Plot(headPoints, headBrush, 2, front: false, side: true);
            var label = hairs[i].Label.Length > 46 ? hairs[i].Label[..46] : hairs[i].Label;
            g.DrawString(bytes == null ? label + "\n(mesh not found)" : label, font, Brushes.Black, new RectangleF(ox + pad, oy + pad + viewH, tileW - pad * 2, labelH));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        bmp.Save(outPath, jpeg, parameters);
        return outPath;
    }
}
