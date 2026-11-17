using System.Runtime.Versioning;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SkyrimMCP.Face;

/// <summary>
/// Body measurement (FEAT-255, experimental): MediaPipe's pose landmark model (ONNX; its detector doesn't convert) finds
/// 33 body points and a silhouette mask; the body is located from the face detector (a standing body is about 10 face
/// boxes tall), then the crop only grows to take in what the landmarks find, since a crop that cuts the legs off makes
/// the model put the hips on the chest. A port of scripts/body-model-check.py.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BodyMeasurer
{
    private static readonly Lazy<BodyMeasurer> _shared = new(() => new BodyMeasurer());
    public static BodyMeasurer Shared => _shared.Value;
    private readonly InferenceSession _pose;
    private readonly string _input;
    private readonly object _lock = new();

    private BodyMeasurer()
    {
        _pose = new InferenceSession(Path.Combine(FaceMeasurer.ModelFolder, "pose_landmarks.onnx"));
        _input = _pose.InputMetadata.Keys.First();
    }

    private sealed record Result(double[,] Xy, double[] Visibility, double Presence, float[] Mask, double CX, double CY, double Side);

    public Dictionary<string, object?>? Measure(string path)
    {
        var img = RgbImage.Load(path);
        var face = FaceMeasurer.Shared.FaceBox(img);
        double cx, cy, side;
        if (face is { } f) { cx = f.CX; cy = f.CY + 6.0 * f.Size; side = 15.0 * f.Size; }
        else { cx = img.W / 2.0; cy = img.H / 2.0; side = Math.Max(img.W, img.H); }

        Result? r = null;
        for (var pass = 0; pass < 3; pass++)
        {
            lock (_lock) r = Run(img, cx, cy, side);
            var good = Enumerable.Range(0, 33).Where(i => r.Visibility[i] > 0.3).ToList();
            if (good.Count < 8) break;
            double x0 = good.Min(i => r.Xy[i, 0]), x1 = good.Max(i => r.Xy[i, 0]);
            double y0 = good.Min(i => r.Xy[i, 1]), y1 = good.Max(i => r.Xy[i, 1]);
            double nx0 = Math.Min(x0, cx - side / 2), ny0 = Math.Min(y0, cy - side / 2);
            double nx1 = Math.Max(x1, cx + side / 2), ny1 = Math.Max(y1, cy + side / 2);
            cx = (nx0 + nx1) / 2; cy = (ny0 + ny1) / 2; side = Math.Max(nx1 - nx0, ny1 - ny0);
        }
        if (r == null || r.Presence < 0.3) return null;
        return Ratios(r);
    }

    private Result Run(RgbImage img, double cx, double cy, double side)
    {
        // Axis-aligned square crop to 256 x 256, [0, 1]; area-downscaled first when large.
        var region = img.Crop((int)Math.Floor(cx - side / 2), (int)Math.Floor(cy - side / 2), (int)Math.Ceiling(side), (int)Math.Ceiling(side));
        var crop = region.Resize(256, 256);
        var buffer = new float[256 * 256 * 3];
        for (var i = 0; i < buffer.Length; i++) buffer[i] = crop.Px[i] / 255f;
        using var results = _pose.Run([NamedOnnxValue.CreateFromTensor(_input, new DenseTensor<float>(buffer, [1, 256, 256, 3]))]);
        var outputs = results.ToDictionary(o => o.Name, o => o.AsTensor<float>().ToArray());
        var lm = outputs["Identity"];
        var k = 256.0 / Math.Ceiling(side);
        double ox = Math.Floor(cx - side / 2), oy = Math.Floor(cy - side / 2);
        var xy = new double[39, 2];
        var vis = new double[39];
        for (var i = 0; i < 39; i++)
        {
            xy[i, 0] = ox + lm[i * 5] / k;
            xy[i, 1] = oy + lm[i * 5 + 1] / k;
            vis[i] = 1.0 / (1.0 + Math.Exp(-lm[i * 5 + 3]));
        }
        var presence = 1.0 / (1.0 + Math.Exp(-outputs["Identity_1"][0]));
        return new Result(xy, vis, presence, outputs["Identity_2"], ox, oy, Math.Ceiling(side));
    }

    private static Dictionary<string, object?> Ratios(Result r)
    {
        double D(int a, int b) => Math.Sqrt(Math.Pow(r.Xy[a, 0] - r.Xy[b, 0], 2) + Math.Pow(r.Xy[a, 1] - r.Xy[b, 1], 2));
        (double X, double Y) Mid(int a, int b) => ((r.Xy[a, 0] + r.Xy[b, 0]) / 2, (r.Xy[a, 1] + r.Xy[b, 1]) / 2);
        double Len((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        var shoulders = D(11, 12);
        var hips = D(23, 24);
        var sh = Mid(11, 12);
        var hp = Mid(23, 24);
        var ankles = Mid(27, 28);
        var k = 256.0 / r.Side;
        // Head top from the silhouette mask, straight above the nose.
        var mx = (int)Math.Clamp(Math.Round((r.Xy[0, 0] - r.CX) * k), 0, 255);
        var top = r.Xy[0, 1];
        for (var row = 0; row < 256; row++) if (r.Mask[row * 256 + mx] > 0.5f) { top = r.CY + row / k; break; }
        var height = ankles.Y - top;
        double WidthAt(double y)
        {
            var row = (int)Math.Round((y - r.CY) * k);
            if (row < 0 || row > 255) return double.NaN;
            int first = -1, last = -1;
            for (var c = 0; c < 256; c++) if (r.Mask[row * 256 + c] > 0.5f) { if (first < 0) first = c; last = c; }
            return first < 0 ? double.NaN : (last - first) / k;
        }
        double R(double v) => double.IsNaN(v) ? double.NaN : Math.Round(v, 3);
        var key = new[] { 11, 12, 23, 24, 27, 28 };
        return new Dictionary<string, object?>
        {
            ["ratios"] = new Dictionary<string, double>
            {
                ["shoulder_width / height"] = R(shoulders / height),
                ["hip_width / shoulder_width"] = R(hips / shoulders),
                ["torso / height"] = R(Len(sh, hp) / height),
                ["legs / height"] = R(Len(hp, ankles) / height),
                ["arm / height"] = R((D(11, 13) + D(13, 15) + D(12, 14) + D(14, 16)) / 2 / height),
                ["head_and_neck / height"] = R((sh.Y - top) / height),
                ["silhouette_chest / shoulder_width"] = R(WidthAt(sh.Y + 0.25 * (hp.Y - sh.Y)) / shoulders),
                ["silhouette_waist / shoulder_width"] = R(WidthAt(sh.Y + 0.62 * (hp.Y - sh.Y)) / shoulders),
                ["silhouette_hips / shoulder_width"] = R(WidthAt(hp.Y) / shoulders),
            },
            ["confidence"] = new Dictionary<string, double>
            {
                ["presence"] = R(r.Presence),
                ["lowest_key_visibility"] = R(key.Min(i => r.Visibility[i])),
            },
            ["heightPixels"] = Math.Round(height),
            ["note"] = "Silhouette widths include the arms where they touch the body: compare captures in the same pose.",
        };
    }
}
