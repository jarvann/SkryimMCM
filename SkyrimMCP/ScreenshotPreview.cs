using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.Versioning;

namespace SkyrimMCP;

/// <summary>
/// Makes a small JPEG copy of a screenshot, optionally cropped, so an AI can look at it: the game's own
/// files are full-resolution PNGs (63 MB at 7680x2160), far too big to view or send.
/// </summary>
public static class ScreenshotPreview
{
    public sealed record Result(string Path, int Width, int Height, long Bytes, Rectangle Crop, int SourceWidth, int SourceHeight);

    /// <summary>
    /// Parses "x,y,w,h" as fractions of the image (0..1), e.g. "0.4,0.1,0.2,0.5" for the middle fifth.
    /// Null or empty means the whole image.
    /// </summary>
    public static RectangleF? ParseCrop(string? crop)
    {
        if (string.IsNullOrWhiteSpace(crop)) return null;
        var parts = crop.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) throw new ArgumentException("crop must be four fractions: x,y,width,height (each 0 to 1)");
        var v = parts.Select(p => float.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        if (v.Any(f => f < 0 || f > 1) || v[2] <= 0 || v[3] <= 0 || v[0] + v[2] > 1.0001f || v[1] + v[3] > 1.0001f)
            throw new ArgumentException("crop fractions must lie within the image: 0 <= x, y and x+width, y+height <= 1");
        return new RectangleF(v[0], v[1], v[2], v[3]);
    }

    /// <summary>Writes &lt;name&gt;_preview.jpg next to the source and returns it.</summary>
    [SupportedOSPlatform("windows")]
    public static Result Make(string sourcePath, int maxWidth, RectangleF? cropFraction, long quality = 85)
    {
        using var source = Image.FromFile(sourcePath);
        var f = cropFraction ?? new RectangleF(0, 0, 1, 1);
        var crop = Rectangle.Intersect(
            new Rectangle((int)(f.X * source.Width), (int)(f.Y * source.Height),
                          Math.Max(1, (int)(f.Width * source.Width)), Math.Max(1, (int)(f.Height * source.Height))),
            new Rectangle(0, 0, source.Width, source.Height));

        var scale = Math.Min(1.0, (double)maxWidth / crop.Width);
        var width = Math.Max(1, (int)Math.Round(crop.Width * scale));
        var height = Math.Max(1, (int)Math.Round(crop.Height * scale));

        using var preview = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(preview))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, width, height), crop, GraphicsUnit.Pixel);
        }

        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourcePath)!,
            System.IO.Path.GetFileNameWithoutExtension(sourcePath) + "_preview.jpg");
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using (var parameters = new EncoderParameters(1))
        {
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            preview.Save(target, jpeg, parameters);
        }
        return new Result(target, width, height, new FileInfo(target).Length, crop, source.Width, source.Height);
    }
}
