using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SkyrimMCP.Face;

/// <summary>
/// Face measurement (FEAT-230): finds 478 face landmarks with Google's MediaPipe Face Landmarker models
/// (ONNX, in Models\) and turns them into scale-free proportions, lid positions, head pose and colours.
/// A port of scripts/face-model-check.py, step for step: change both together, and check with
/// scripts/face-model-check.py on the same image.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FaceMeasurer : IDisposable
{
    private static readonly Lazy<FaceMeasurer> _shared = new(() => new FaceMeasurer(), LazyThreadSafetyMode.ExecutionAndPublication);
    public static FaceMeasurer Shared => _shared.Value;

    public static string ModelFolder => Path.Combine(AppContext.BaseDirectory, "Models");

    private readonly InferenceSession _detector;
    private readonly InferenceSession _landmarker;
    private readonly string _landmarkInput;
    private readonly object _lock = new();

    // Crops averaged per measurement: the landmark model moves a few percent with small crop changes,
    // and averaging these 27 cut the spread to about a third in testing.
    private static readonly double[] TtaScales = [0.93, 1.0, 1.07];
    private static readonly double[] TtaShifts = [-0.03, 0.0, 0.03];
    private const double PitchLevel = 13.0;  // raw pitch of a level, frontal face (forehead top sits behind the chin)

    private static readonly (double X, double Y)[] Anchors = MakeAnchors();

    private FaceMeasurer()
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _detector = new InferenceSession(Path.Combine(ModelFolder, "face_detector.onnx"), options);
        _landmarker = new InferenceSession(Path.Combine(ModelFolder, "face_landmarks.onnx"), options);
        _landmarkInput = _landmarker.InputMetadata.Keys.First();
    }

    public void Dispose()
    {
        _detector.Dispose();
        _landmarker.Dispose();
    }

    public sealed record Landmarks(double[,] Points, double DetectorScore, double LandmarkScore);

    /// <summary>Measures the face in an image file. Null when no face is found.</summary>
    public Dictionary<string, object?>? Measure(string path)
    {
        var image = RgbImage.Load(path);
        Landmarks? found;
        lock (_lock) found = FindLandmarks(image);
        if (found == null) return null;
        var L = found.Points;
        return new Dictionary<string, object?>
        {
            ["confidence"] = new Dictionary<string, double> { ["detector"] = R(found.DetectorScore), ["landmarks"] = R(found.LandmarkScore) },
            ["pose"] = PoseAndExpression(L),
            ["ratios"] = Ratios(L).ToDictionary(kv => kv.Key, kv => R(kv.Value)),
            ["eyes"] = LidGaps(L).ToDictionary(kv => kv.Key, kv => R(kv.Value)),
            ["colour"] = Colours(image, L),
            ["imageSize"] = $"{image.W}x{image.H}",
        };
    }

    /// <summary>
    /// Quick framing check (detector only, no landmarks): where the face is in the picture, as fractions of its
    /// width and height. Null when no face is found.
    /// </summary>
    public (double CenterX, double CenterY, double Size)? LocateFace(string path)
    {
        var image = RgbImage.Load(path);
        Detection? det;
        lock (_lock) det = Detect(image);
        return det == null ? null : (det.CX / image.W, det.CY / image.H, Math.Max(det.W / image.W, det.H / image.H));
    }

    private static double R(double v) => Math.Round(v, 3);

    // ---- detection --------------------------------------------------------------------------------

    private static (double, double)[] MakeAnchors()
    {
        // SSD anchors for face_detection_short_range: strides 8 (2 per cell) and 16 (6 per cell).
        var list = new List<(double, double)>();
        foreach (var (stride, perCell) in new[] { (8, 2), (16, 6) })
        {
            var n = 128 / stride;
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                    for (var i = 0; i < perCell; i++)
                        list.Add(((x + 0.5) / n, (y + 0.5) / n));
        }
        return list.ToArray();
    }

    private sealed record Detection(double Score, double CX, double CY, double W, double H, (double X, double Y)[] Keypoints);

    private Detection? DetectSquare(RgbImage img)
    {
        var s = 128.0 / Math.Max(img.W, img.H);
        var nw = Math.Max(1, (int)Math.Round(img.W * s));
        var nh = Math.Max(1, (int)Math.Round(img.H * s));
        var small = img.Resize(nw, nh);
        int px = (128 - nw) / 2, py = (128 - nh) / 2;
        var buffer = new float[128 * 128 * 3];
        for (var y = 0; y < 128; y++)
            for (var x = 0; x < 128; x++)
                for (var c = 0; c < 3; c++)
                {
                    int sx = x - px, sy = y - py;
                    var v = sx >= 0 && sy >= 0 && sx < nw && sy < nh ? small.Px[(sy * nw + sx) * 3 + c] : 0f;
                    buffer[(y * 128 + x) * 3 + c] = v / 127.5f - 1f;
                }
        var input = new DenseTensor<float>(buffer, [1, 128, 128, 3]);
        using var results = _detector.Run([NamedOnnxValue.CreateFromTensor("input", input)]);
        var reg = results.First(r => r.Name == "regressors").AsTensor<float>();
        var cls = results.First(r => r.Name == "classificators").AsTensor<float>();

        var count = Anchors.Length;
        var scores = new double[count];
        var best = 0;
        for (var i = 0; i < count; i++)
        {
            scores[i] = 1.0 / (1.0 + Math.Exp(-Math.Clamp((double)cls[0, i, 0], -100, 100)));
            if (scores[i] > scores[best]) best = i;
        }
        if (scores[best] < 0.5) return null;

        double[] Box(int i)
        {
            var b = new double[16];
            for (var k = 0; k < 16; k++) b[k] = reg[0, i, k] / 128.0;
            b[0] += Anchors[i].X; b[1] += Anchors[i].Y;
            for (var k = 0; k < 6; k++) { b[4 + 2 * k] += Anchors[i].X; b[5 + 2 * k] += Anchors[i].Y; }
            return b;
        }
        static double Iou(double[] a, double[] b)
        {
            double ax0 = a[0] - a[2] / 2, ay0 = a[1] - a[3] / 2, ax1 = a[0] + a[2] / 2, ay1 = a[1] + a[3] / 2;
            double bx0 = b[0] - b[2] / 2, by0 = b[1] - b[3] / 2, bx1 = b[0] + b[2] / 2, by1 = b[1] + b[3] / 2;
            var inter = Math.Max(0, Math.Min(ax1, bx1) - Math.Max(ax0, bx0)) * Math.Max(0, Math.Min(ay1, by1) - Math.Max(ay0, by0));
            return inter / (a[2] * a[3] + b[2] * b[3] - inter + 1e-9);
        }

        // Weighted NMS around the best candidate.
        var bestBox = Box(best);
        var sum = new double[16];
        var weight = 0.0;
        for (var i = 0; i < count; i++)
        {
            if (scores[i] < 0.5) continue;
            var b = Box(i);
            if (Iou(b, bestBox) <= 0.3) continue;
            for (var k = 0; k < 16; k++) sum[k] += b[k] * scores[i];
            weight += scores[i];
        }
        for (var k = 0; k < 16; k++) sum[k] /= weight;
        (double, double) ToImage(double x, double y) => ((x * 128 - px) / s, (y * 128 - py) / s);
        var (cx, cy) = ToImage(sum[0], sum[1]);
        var keypoints = Enumerable.Range(0, 6).Select(k => ToImage(sum[4 + 2 * k], sum[5 + 2 * k])).ToArray();
        return new Detection(scores[best], cx, cy, sum[2] * 128 / s, sum[3] * 128 / s, keypoints);
    }

    private Detection? Detect(RgbImage img)
    {
        // A wide frame (32:9) leaves the face too small once letterboxed: fall back to the middle square.
        var det = DetectSquare(img);
        if (det != null || img.W <= img.H * 1.2) return det;
        var x0 = (img.W - img.H) / 2;
        det = DetectSquare(img.Crop(x0, 0, img.H, img.H));
        return det == null ? null : det with
        {
            CX = det.CX + x0,
            Keypoints = det.Keypoints.Select(p => (p.X + x0, p.Y)).ToArray(),
        };
    }

    // ---- landmarks --------------------------------------------------------------------------------

    private Landmarks? FindLandmarks(RgbImage img)
    {
        var det = Detect(img);
        if (det == null) return null;
        var angle = Math.Atan2(det.Keypoints[1].Y - det.Keypoints[0].Y, det.Keypoints[1].X - det.Keypoints[0].X);
        var side = Math.Max(det.W, det.H) * 1.5;

        // Work on a region around the face, area-downscaled so a 256 crop of it isn't aliased.
        var half = side * 0.85;
        int rx = (int)Math.Floor(det.CX - half), ry = (int)Math.Floor(det.CY - half), rs = (int)Math.Ceiling(half * 2);
        var region = img.Crop(rx, ry, rs, rs);
        var f = Math.Min(1.0, 512.0 / side);
        if (f < 1.0) region = region.Resize(Math.Max(1, (int)Math.Round(rs * f)), Math.Max(1, (int)Math.Round(rs * f)));
        var fx = (double)region.W / rs;

        var acc = new double[478, 3];
        var minPresence = 1.0;
        var runs = 0;
        foreach (var sc in TtaScales)
            foreach (var dx in TtaShifts)
                foreach (var dy in TtaShifts)
                {
                    var cx = (det.CX + dx * side - rx) * fx;
                    var cy = (det.CY + dy * side - ry) * fx;
                    var (pts, presence) = RunLandmarks(region, cx, cy, side * sc * fx, angle);
                    for (var i = 0; i < 478; i++)
                    {
                        acc[i, 0] += pts[i, 0] / fx + rx;
                        acc[i, 1] += pts[i, 1] / fx + ry;
                        acc[i, 2] += pts[i, 2] / fx;
                    }
                    minPresence = Math.Min(minPresence, presence);
                    runs++;
                }
        for (var i = 0; i < 478; i++)
            for (var c = 0; c < 3; c++)
                acc[i, c] /= runs;
        return new Landmarks(acc, det.Score, minPresence);
    }

    private (double[,] Points, double Presence) RunLandmarks(RgbImage img, double cx, double cy, double side, double angle)
    {
        // Affine from the image to the 256 crop: rotate by -angle about the centre, scale side to 256.
        var k = 256.0 / side;
        double c = Math.Cos(-angle) * k, s = Math.Sin(-angle) * k;
        double tx = 128 - (c * cx - s * cy), ty = 128 - (s * cx + c * cy);
        var det = c * c + s * s;
        (double X, double Y) ToImage(double u, double v)
        {
            double du = u - tx, dv = v - ty;
            return ((c * du + s * dv) / det, (-s * du + c * dv) / det);
        }

        var buffer = new float[256 * 256 * 3];
        Span<float> rgb = stackalloc float[3];
        for (var v = 0; v < 256; v++)
            for (var u = 0; u < 256; u++)
            {
                var (x, y) = ToImage(u, v);
                img.Bilinear(x, y, rgb);
                var o = (v * 256 + u) * 3;
                buffer[o] = rgb[0] / 255f;
                buffer[o + 1] = rgb[1] / 255f;
                buffer[o + 2] = rgb[2] / 255f;
            }
        var input = new DenseTensor<float>(buffer, [1, 256, 256, 3]);
        using var results = _landmarker.Run([NamedOnnxValue.CreateFromTensor(_landmarkInput, input)]);
        var outputs = results.ToDictionary(r => r.Name, r => r.AsTensor<float>());
        var raw = outputs["Identity"].ToArray();
        var presence = 1.0 / (1.0 + Math.Exp(-outputs["Identity_1"].ToArray()[0]));
        var pts = new double[478, 3];
        for (var i = 0; i < 478; i++)
        {
            var (x, y) = ToImage(raw[i * 3], raw[i * 3 + 1]);
            pts[i, 0] = x;
            pts[i, 1] = y;
            pts[i, 2] = raw[i * 3 + 2] / k;
        }
        return (pts, presence);
    }

    // ---- measurements -----------------------------------------------------------------------------

    private static double D(double[,] L, int a, int b) => Math.Sqrt(Math.Pow(L[a, 0] - L[b, 0], 2) + Math.Pow(L[a, 1] - L[b, 1], 2));

    public static Dictionary<string, double> Ratios(double[,] L)
    {
        double faceW = D(L, 234, 454), faceH = D(L, 10, 152);
        var eyeW = (D(L, 33, 133) + D(L, 362, 263)) / 2;
        var inner = D(L, 133, 362);
        var tl = Math.Atan2(L[133, 1] - L[33, 1], Math.Abs(L[133, 0] - L[33, 0])) * 180 / Math.PI;
        var tr = Math.Atan2(L[362, 1] - L[263, 1], Math.Abs(L[263, 0] - L[362, 0])) * 180 / Math.PI;
        return new Dictionary<string, double>
        {
            ["eye_width / face_width"] = eyeW / faceW,
            ["eye_opening / eye_width"] = (D(L, 159, 145) + D(L, 386, 374)) / 2 / eyeW,
            ["eye_spacing / eye_width"] = inner / eyeW,
            ["outer_corner_tilt_deg"] = (tl + tr) / 2,
            ["brow_to_eye / eye_width"] = (D(L, 105, 159) + D(L, 334, 386)) / 2 / eyeW,
            ["nose_length / face_height"] = D(L, 168, 2) / faceH,
            ["nose_width / eye_spacing"] = D(L, 129, 358) / inner,
            ["mouth_width / face_width"] = D(L, 61, 291) / faceW,
            ["lip_height / mouth_width"] = D(L, 0, 17) / D(L, 61, 291),
            ["jaw_width / face_width"] = D(L, 172, 397) / faceW,
            ["chin_height / face_height"] = D(L, 17, 152) / faceH,
            ["face_width / face_height"] = faceW / faceH,
            ["iris_diameter / eye_width"] = (D(L, 469, 471) + D(L, 474, 476)) / 2 / eyeW,
        };
    }

    /// <summary>
    /// How far each lid sits from the iris centre, in iris radii, across the eye. Under 1: the lid covers
    /// part of the iris. Over 1: white shows between lid and iris (a stare, for the upper lid).
    /// </summary>
    public static Dictionary<string, double> LidGaps(double[,] L)
    {
        double up = 0, low = 0;
        foreach (var (outer, inner, upper, lower, iris, rimA, rimB) in new[] { (33, 133, 159, 145, 468, 469, 471), (263, 362, 386, 374, 473, 474, 476) })
        {
            double ax = L[inner, 0] - L[outer, 0], ay = L[inner, 1] - L[outer, 1];
            var len = Math.Sqrt(ax * ax + ay * ay);
            ax /= len; ay /= len;
            double nx = -ay, ny = ax;
            if (ny < 0) { nx = -nx; ny = -ny; }  // pointing down the image
            var r = D(L, rimA, rimB) / 2;
            up += ((L[iris, 0] - L[upper, 0]) * nx + (L[iris, 1] - L[upper, 1]) * ny) / r;
            low += ((L[lower, 0] - L[iris, 0]) * nx + (L[lower, 1] - L[iris, 1]) * ny) / r;
        }
        // Lid positions from the corner-to-corner line, in eye widths: these don't move when the eyes look up or
        // down, unlike the gaps to the iris centre.
        double lidUp = 0, lidLow = 0;
        foreach (var (outer, inner, upper, lower) in new[] { (33, 133, 159, 145), (263, 362, 386, 374) })
        {
            double ax = L[inner, 0] - L[outer, 0], ay = L[inner, 1] - L[outer, 1];
            var width = Math.Sqrt(ax * ax + ay * ay);
            double nx = -ay / width, ny = ax / width;
            if (ny < 0) { nx = -nx; ny = -ny; }
            lidUp += ((L[outer, 0] - L[upper, 0]) * nx + (L[outer, 1] - L[upper, 1]) * ny) / width;
            lidLow += ((L[lower, 0] - L[outer, 0]) * nx + (L[lower, 1] - L[outer, 1]) * ny) / width;
        }
        return new Dictionary<string, double>
        {
            ["upper_lid_gap / iris_radius"] = up / 2, ["lower_lid_gap / iris_radius"] = low / 2,
            ["upper_lid_height / eye_width"] = lidUp / 2, ["lower_lid_depth / eye_width"] = lidLow / 2,
        };
    }

    /// <summary>Head angles from the 3D landmarks and simple expression signs: a turned head or a smile skews the ratios.</summary>
    public static Dictionary<string, object> PoseAndExpression(double[,] L)
    {
        double earX = L[454, 0] - L[234, 0], earZ = L[454, 2] - L[234, 2];
        double vertY = L[152, 1] - L[10, 1], vertZ = L[152, 2] - L[10, 2];
        var yaw = Math.Atan2(earZ, earX) * 180 / Math.PI;
        var pitch = Math.Atan2(-vertZ, vertY) * 180 / Math.PI + PitchLevel;
        var roll = Math.Atan2(L[263, 1] - L[33, 1], L[263, 0] - L[33, 0]) * 180 / Math.PI;
        var mouthW = D(L, 61, 291);
        var lipMidY = (L[0, 1] + L[17, 1]) / 2;
        var cornerLift = (lipMidY - (L[61, 1] + L[291, 1]) / 2) / mouthW;
        var mouthOpen = D(L, 13, 14) / mouthW;
        var flags = new List<string>();
        if (Math.Abs(yaw) > 8) flags.Add("head turned: width ratios are skewed");
        if (Math.Abs(pitch) > 10) flags.Add("head tilted up or down: height ratios are skewed");
        if (cornerLift > 0.08) flags.Add("smiling: mouth width, lip height and eye opening are not neutral");
        if (mouthOpen > 0.08) flags.Add("mouth open: lip height and chin height are not neutral");
        return new Dictionary<string, object>
        {
            ["yaw_deg"] = R(yaw), ["pitch_deg"] = R(pitch), ["roll_deg"] = R(roll),
            ["mouth_corner_lift"] = R(cornerLift), ["mouth_open"] = R(mouthOpen), ["flags"] = flags,
        };
    }

    // Sample spots: a landmark, or a pair whose midpoint is sampled.
    private static readonly Dictionary<string, (int A, int B)[]> ColourSpots = new()
    {
        ["skin"] = [(199, 199), (200, 200), (208, 208), (428, 428)],  // chin: no makeup (blush bands can cross the nose)
        ["cheeks"] = [(50, 50), (280, 280), (50, 205), (280, 425), (101, 101), (330, 330), (118, 118), (347, 347), (205, 205), (425, 425)],  // cheekbones to the apples
        ["lips"] = [(0, 13), (14, 17), (37, 82), (267, 312), (84, 87), (314, 317)],
        ["brows"] = [(63, 53), (105, 52), (66, 65), (293, 283), (334, 282), (296, 295)],
    };

    private static double[] Linear(double[] p)
    {
        static double Lin(double v) { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return [Lin(p[0]), Lin(p[1]), Lin(p[2])];
    }

    private static double Luminance(double[] lin) => 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2];

    private static double[] Median(List<double[]> values)
    {
        if (values.Count == 0) return [0, 0, 0];
        return Enumerable.Range(0, 3).Select(c =>
        {
            var sorted = values.Select(v => v[c]).OrderBy(v => v).ToArray();
            var m = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2;
        }).ToArray();
    }

    // numpy's default (linear) percentile.
    private static double Percentile(IEnumerable<double> values, double pct)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        var pos = (sorted.Length - 1) * pct / 100.0;
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(sorted.Length - 1, lo + 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private static double[] MedianLab(List<double[]> rgbSamples) =>
        rgbSamples.Count == 0 ? [0, 0, 0] : Median(rgbSamples.Select(p => Color.SrgbToLab(p[0], p[1], p[2])).ToList());

    private static List<double[]> IrisPixels(RgbImage img, double[,] L)
    {
        // Points at 60% of the radius on the lower half and the sides (lids and lashes shade the top).
        var samples = new List<double[]>();
        foreach (var (iris, rimA, rimB) in new[] { (468, 469, 471), (473, 474, 476) })
        {
            var r = D(L, rimA, rimB) / 2;
            foreach (var deg in new[] { 0, 45, 90, 135, 180 })
            {
                var t = deg * Math.PI / 180;
                img.SampleDisc(L[iris, 0] + 0.6 * r * Math.Cos(t), L[iris, 1] + 0.6 * r * Math.Sin(t), Math.Max(1.0, r * 0.15), samples);
            }
        }
        return samples;
    }

    /// <summary>
    /// The white of the eye on both sides of each iris, a third of the way to the corner, brightest half kept
    /// (lashes and the lid shadow darken parts of it). Null when the iris radius is under 5 pixels: in a small
    /// picture the white is a pixel or two.
    /// </summary>
    private static List<double[]>? ScleraPixels(RgbImage img, double[,] L)
    {
        var samples = new List<double[]>();
        foreach (var (iris, rimA, rimB, c1, c2) in new[] { (468, 469, 471, 33, 133), (473, 474, 476, 263, 362) })
        {
            var r = D(L, rimA, rimB) / 2;
            if (r < 5) return null;
            foreach (var corner in new[] { c1, c2 })
            {
                double vx = L[corner, 0] - L[iris, 0], vy = L[corner, 1] - L[iris, 1];
                var dist = Math.Sqrt(vx * vx + vy * vy);
                if (dist <= r * 1.2) continue;
                var k = (r + (dist - r) * 0.3) / dist;
                img.SampleDisc(L[iris, 0] + vx * k, L[iris, 1] + vy * k, Math.Max(1.0, r * 0.18), samples);
            }
        }
        if (samples.Count == 0) return null;
        var lums = samples.Select(p => Luminance(Linear(p))).OrderBy(v => v).ToArray();
        var median = lums[lums.Length / 2];
        return samples.Where(p => Luminance(Linear(p)) >= median).ToList();
    }

    private static Dictionary<string, object> Colours(RgbImage img, double[,] L)
    {
        var eyeW = (D(L, 33, 133) + D(L, 362, 263)) / 2;
        var radius = Math.Max(1.0, eyeW * 0.06);
        var regions = new Dictionary<string, List<double[]>>();
        foreach (var (name, spots) in ColourSpots)
        {
            var samples = new List<double[]>();
            foreach (var (a, b) in spots)
                img.SampleDisc((L[a, 0] + L[b, 0]) / 2, (L[a, 1] + L[b, 1]) / 2, radius, samples);
            regions[name] = samples;
        }
        regions["iris"] = IrisPixels(img, L);
        if (ScleraPixels(img, L) is { } sclera) regions["eye_white"] = sclera;

        var lab = regions.ToDictionary(kv => kv.Key, kv => MedianLab(kv.Value));
        var lin = regions.ToDictionary(kv => kv.Key, kv => Median(kv.Value.Select(Linear).ToList()).Select(v => v + 1e-4).ToArray());
        var skin = lin["skin"];
        static double Lg(double v) => Math.Log2(v);

        // Light-independent colour (FEAT-242): ratios of linear RGB between two parts of the same face cancel the
        // light's brightness and colour, since both parts are lit alike. log2: 0 = same as the skin, 1 = twice.
        var rel = new Dictionary<string, double>
        {
            ["skin_hue_deg (lighting-dependent)"] = R((Math.Atan2(lab["skin"][2], lab["skin"][1]) * 180 / Math.PI + 360) % 360),
            ["brow_lightness (brow / skin luminance)"] = R(Luminance(lin["brows"]) / Luminance(skin)),
            ["lip_redness (log2 R/G vs skin)"] = R(Lg(lin["lips"][0] / lin["lips"][1]) - Lg(skin[0] / skin[1])),
            ["lip_lightness (lip / skin luminance)"] = R(Luminance(lin["lips"]) / Luminance(skin)),
            // The reddest fifth of the cheek pixels: a blush band is found wherever it sits on the cheek.
            ["blush (log2 R/G cheek vs skin)"] = R(Percentile(regions["cheeks"].Select(p => { var l = Linear(p); return Lg((l[0] + 1e-4) / (l[1] + 1e-4)); }), 80) - Lg(skin[0] / skin[1])),
            ["iris_lightness (iris / skin luminance)"] = R(Luminance(lin["iris"]) / Luminance(skin)),
        };
        // Against the white of the eye (a near-neutral white under the same light): the skin's and the iris's own colour.
        if (lin.TryGetValue("eye_white", out var w))
        {
            var iris = lin["iris"];
            rel["skin_warmth (log2 R/B vs eye white)"] = R(Lg(skin[0] / w[0]) - Lg(skin[2] / w[2]));
            rel["skin_pink (log2 R/G vs eye white)"] = R(Lg(skin[0] / w[0]) - Lg(skin[1] / w[1]));
            rel["iris_warmth (log2 R/B vs eye white)"] = R(Lg(iris[0] / w[0]) - Lg(iris[2] / w[2]));
            rel["iris_green (log2 G/(R+B)/2 vs eye white)"] = R(Lg(iris[1] / w[1]) - (Lg(iris[0] / w[0]) + Lg(iris[2] / w[2])) / 2);
        }

        // Exposure: clipped pixels can't be compared; a washed-out (hazy) picture shrinks every colour difference.
        var clipped = new[] { "skin", "cheeks", "lips" }.ToDictionary(k => k, k =>
            regions[k].Count == 0 ? 0.0 : R(regions[k].Count(p => Math.Max(p[0], Math.Max(p[1], p[2])) >= 250) / (double)regions[k].Count));
        var sides = new double[2];
        foreach (var (i, idx) in new[] { (0, 50), (1, 280) })
        {
            var px = new List<double[]>();
            img.SampleDisc(L[idx, 0], L[idx, 1], radius * 2, px);
            sides[i] = MedianLab(px)[0];
        }
        var contrast = (Luminance(skin) - Luminance(lin["iris"])) / Luminance(skin);
        var warnings = new List<string>();
        if (Math.Abs(sides[0] - sides[1]) > 12) warnings.Add("lit from one side: shading differs from an evenly lit capture");
        if (clipped.Values.Max() > 0.05) warnings.Add("overexposed: some skin or lip pixels are clipped to white");
        if (contrast < 0.5) warnings.Add("washed out (haze or fog): colour differences read smaller than they are");
        var lighting = new Dictionary<string, object>
        {
            ["skin_L"] = R(lab["skin"][0]),
            ["side_balance (image-left cheek L - image-right cheek L)"] = R(sides[0] - sides[1]),
            ["clipped"] = clipped,
            ["contrast (skin vs iris)"] = R(contrast),
            ["warnings"] = warnings,
        };

        // The face area's overall cast (mean a*, b*): strong casts (sepia, purple light) shift every absolute colour.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var i = 0; i < 478; i++)
        {
            minX = Math.Min(minX, L[i, 0]); maxX = Math.Max(maxX, L[i, 0]);
            minY = Math.Min(minY, L[i, 1]); maxY = Math.Max(maxY, L[i, 1]);
        }
        int x0 = Math.Max(0, (int)minX), y0 = Math.Max(0, (int)minY);
        int x1 = Math.Min(img.W - 1, (int)maxX), y1 = Math.Min(img.H - 1, (int)maxY);
        var width = x1 - x0 + 1;
        var total = Math.Max(1L, (long)width * (y1 - y0 + 1));
        var step = Math.Max(1, (int)(total / 20000));
        double castA = 0, castB = 0, castA2 = 0, castB2 = 0, hueCos = 0, hueSin = 0;
        var n = 0;
        var coloured = 0;
        for (long idx = 0; idx < total; idx += step)
        {
            var p = img.Pixel(x0 + (int)(idx % width), y0 + (int)(idx / width));
            var l = Color.SrgbToLab(p[0], p[1], p[2]);
            castA += l[1]; castB += l[2]; castA2 += l[1] * l[1]; castB2 += l[2] * l[2]; n++;
            var chroma = Math.Sqrt(l[1] * l[1] + l[2] * l[2]);
            if (chroma > 4) { hueCos += l[1] / chroma; hueSin += l[2] / chroma; coloured++; }
        }
        castA /= Math.Max(1, n); castB /= Math.Max(1, n);
        // BUG-042: a sepia or black-and-white picture has almost no colour variation across the face (a*/b* spread
        // 0.8/1.9 on a sepia photo, 5.1/5.1 on a colour render, 3.5/3.7 on a game capture): its colour ratios mean nothing.
        var spreadA = Math.Sqrt(Math.Max(0, castA2 / Math.Max(1, n) - castA * castA));
        var spreadB = Math.Sqrt(Math.Max(0, castB2 / Math.Max(1, n) - castB * castB));
        // The spread alone also caught a pale face under white studio light (2.73/2.93, BUG-044): a sepia picture
        // also has a single hue (circular spread 3.5 deg), where a real face does not (17.7 deg on a game capture).
        var hueR = coloured > 0 ? Math.Sqrt(hueCos * hueCos + hueSin * hueSin) / coloured : 1.0;
        var hueSpread = Math.Sqrt(-2 * Math.Log(Math.Max(hueR, 1e-9))) * 180 / Math.PI;
        var monochrome = Math.Max(spreadA, spreadB) < 3.0 && (coloured < 50 || hueSpread < 6.0);
        if (monochrome) warnings.Add("monochrome or sepia picture: its colours can't be compared (use it for shape only)");

        return new Dictionary<string, object>
        {
            ["usable"] = !monochrome,
            ["spread"] = new[] { R(spreadA), R(spreadB) },
            ["hueSpreadDeg"] = R(hueSpread),
            ["hex"] = lab.ToDictionary(kv => kv.Key, kv => Color.LabToHex(kv.Value)),
            ["relative"] = rel,
            ["lighting"] = lighting,
            ["cast"] = new Dictionary<string, object?>
            {
                ["a"] = R(castA), ["b"] = R(castB),
                ["warning"] = Math.Sqrt(castA * castA + castB * castB) > 25 ? "strong colour cast: compare relative values, not hex" : null,
            },
        };
    }

    // ---- colour maths -----------------------------------------------------------------------------

    private static class Color
    {
        public static double[] SrgbToLab(double r, double g, double b)
        {
            static double Lin(double v) { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            double lr = Lin(r), lg = Lin(g), lb = Lin(b);
            var x = (0.4124564 * lr + 0.3575761 * lg + 0.1804375 * lb) / 0.95047;
            var y = 0.2126729 * lr + 0.7151522 * lg + 0.0721750 * lb;
            var z = (0.0193339 * lr + 0.1191920 * lg + 0.9503041 * lb) / 1.08883;
            const double e = 6.0 / 29;
            static double F(double t) => t > e * e * e ? Math.Cbrt(t) : t / (3 * e * e) + 4.0 / 29;
            double fx = F(x), fy = F(y), fz = F(z);
            return [116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)];
        }

        public static string LabToHex(double[] lab)
        {
            var fy = (lab[0] + 16) / 116;
            double fx = fy + lab[1] / 500, fz = fy - lab[2] / 200;
            const double e = 6.0 / 29;
            static double Inv(double t) => t > e ? t * t * t : 3 * e * e * (t - 4.0 / 29);
            double x = Inv(fx) * 0.95047, y = Inv(fy), z = Inv(fz) * 1.08883;
            double[] lin =
            [
                3.2404542 * x - 1.5371385 * y - 0.4985314 * z,
                -0.9692660 * x + 1.8760108 * y + 0.0415560 * z,
                0.0556434 * x - 0.2040259 * y + 1.0572252 * z,
            ];
            return "#" + string.Concat(lin.Select(v =>
            {
                v = Math.Clamp(v, 0, 1);
                var srgb = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
                return ((int)Math.Round(srgb * 255)).ToString("X2");
            }));
        }
    }
}

/// <summary>An RGB image as floats (0-255), with the resampling the face pipeline needs.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RgbImage
{
    public readonly float[] Px;
    public readonly int W, H;

    public RgbImage(int w, int h) : this(w, h, new float[w * h * 3]) { }
    private RgbImage(int w, int h, float[] px) { W = w; H = h; Px = px; }

    public static RgbImage Load(string path)
    {
        using var bitmap = new Bitmap(path);
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var img = new RgbImage(bitmap.Width, bitmap.Height);
            var row = new byte[Math.Abs(data.Stride)];
            for (var y = 0; y < img.H; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < img.W; x++)
                {
                    var o = (y * img.W + x) * 3;
                    img.Px[o] = row[x * 3 + 2];      // GDI+ stores BGR
                    img.Px[o + 1] = row[x * 3 + 1];
                    img.Px[o + 2] = row[x * 3];
                }
            }
            return img;
        }
        finally { bitmap.UnlockBits(data); }
    }

    public float[] Pixel(int x, int y) { var o = (y * W + x) * 3; return [Px[o], Px[o + 1], Px[o + 2]]; }

    /// <summary>A rectangle of the image; parts outside it are black.</summary>
    public RgbImage Crop(int x0, int y0, int w, int h)
    {
        var img = new RgbImage(w, h);
        for (var y = 0; y < h; y++)
        {
            var sy = y0 + y;
            if (sy < 0 || sy >= H) continue;
            for (var x = 0; x < w; x++)
            {
                var sx = x0 + x;
                if (sx < 0 || sx >= W) continue;
                Array.Copy(Px, (sy * W + sx) * 3, img.Px, (y * w + x) * 3, 3);
            }
        }
        return img;
    }

    /// <summary>Area-average resize (like OpenCV's INTER_AREA) when shrinking; bilinear when growing.</summary>
    public RgbImage Resize(int nw, int nh)
    {
        if (nw >= W && nh >= H)
        {
            var up = new RgbImage(nw, nh);
            Span<float> rgb = stackalloc float[3];
            double sx = (double)W / nw, sy = (double)H / nh;
            for (var y = 0; y < nh; y++)
                for (var x = 0; x < nw; x++)
                {
                    BilinearClamped((x + 0.5) * sx - 0.5, (y + 0.5) * sy - 0.5, rgb);
                    var o = (y * nw + x) * 3;
                    up.Px[o] = rgb[0]; up.Px[o + 1] = rgb[1]; up.Px[o + 2] = rgb[2];
                }
            return up;
        }
        var cols = AreaWeights(W, nw);
        var rows = AreaWeights(H, nh);
        // horizontal pass
        var tmp = new float[H * nw * 3];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < nw; x++)
            {
                double r = 0, g = 0, b = 0;
                foreach (var (src, wt) in cols[x])
                {
                    var o = (y * W + src) * 3;
                    r += Px[o] * wt; g += Px[o + 1] * wt; b += Px[o + 2] * wt;
                }
                var t = (y * nw + x) * 3;
                tmp[t] = (float)r; tmp[t + 1] = (float)g; tmp[t + 2] = (float)b;
            }
        // vertical pass
        var img = new RgbImage(nw, nh);
        for (var y = 0; y < nh; y++)
            for (var x = 0; x < nw; x++)
            {
                double r = 0, g = 0, b = 0;
                foreach (var (src, wt) in rows[y])
                {
                    var o = (src * nw + x) * 3;
                    r += tmp[o] * wt; g += tmp[o + 1] * wt; b += tmp[o + 2] * wt;
                }
                var t = (y * nw + x) * 3;
                img.Px[t] = (float)r; img.Px[t + 1] = (float)g; img.Px[t + 2] = (float)b;
            }
        return img;
    }

    private static List<(int Src, double Weight)>[] AreaWeights(int from, int to)
    {
        var scale = (double)from / to;
        var result = new List<(int, double)>[to];
        for (var i = 0; i < to; i++)
        {
            double start = i * scale, end = Math.Min(from, (i + 1) * scale);
            var list = new List<(int, double)>();
            for (var s = (int)Math.Floor(start); s < end; s++)
            {
                var overlap = Math.Min(end, s + 1) - Math.Max(start, s);
                if (overlap > 0) list.Add((s, overlap / (end - start)));
            }
            result[i] = list;
        }
        return result;
    }

    /// <summary>Bilinear sample at pixel-centre coordinates; outside the image counts as black (like cv2.BORDER_CONSTANT).</summary>
    public void Bilinear(double x, double y, Span<float> rgb)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        float fx = (float)(x - x0), fy = (float)(y - y0);
        rgb.Clear();
        for (var dy = 0; dy <= 1; dy++)
            for (var dx = 0; dx <= 1; dx++)
            {
                int sx = x0 + dx, sy = y0 + dy;
                if (sx < 0 || sy < 0 || sx >= W || sy >= H) continue;
                var wt = (dx == 1 ? fx : 1 - fx) * (dy == 1 ? fy : 1 - fy);
                var o = (sy * W + sx) * 3;
                rgb[0] += Px[o] * wt; rgb[1] += Px[o + 1] * wt; rgb[2] += Px[o + 2] * wt;
            }
    }

    private void BilinearClamped(double x, double y, Span<float> rgb) =>
        Bilinear(Math.Clamp(x, 0, W - 1), Math.Clamp(y, 0, H - 1), rgb);

    /// <summary>Adds every pixel within r of (x, y) to samples.</summary>
    public void SampleDisc(double x, double y, double r, List<double[]> samples)
    {
        int x0 = Math.Max(0, (int)Math.Floor(x - r)), x1 = Math.Min(W, (int)Math.Ceiling(x + r) + 1);
        int y0 = Math.Max(0, (int)Math.Floor(y - r)), y1 = Math.Min(H, (int)Math.Ceiling(y + r) + 1);
        for (var py = y0; py < y1; py++)
            for (var px = x0; px < x1; px++)
            {
                if ((px - x) * (px - x) + (py - y) * (py - y) > r * r) continue;
                var o = (py * W + px) * 3;
                samples.Add([Px[o], Px[o + 1], Px[o + 2]]);
            }
    }
}
