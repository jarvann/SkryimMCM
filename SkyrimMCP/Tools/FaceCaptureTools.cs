using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

/// <summary>
/// Face builder M2 (FEAT-229): a repeatable face screenshot. The plugin freezes time, puts the free camera
/// at a set angle and distance from the player's head, hides the HUD (and the hair if asked); the server
/// takes the screenshot, crops it to the face, and puts the camera back.
/// </summary>
[McpServerToolType]
public class FaceCaptureTools : ToolBase
{
    public FaceCaptureTools(IPipeClient pipe) : base(pipe) { }

    private const double MinDistance = 28;

    private static readonly Dictionary<string, float> Views = new(StringComparer.OrdinalIgnoreCase)
    {
        ["front"] = 0f,
        ["three-quarter"] = 35f,
        ["three-quarter-left"] = -35f,
        ["profile"] = 90f,
        ["profile-left"] = -90f,
    };

    [McpServerTool]
    [Description("Take a framed screenshot of the player's face, the same way every time, and return a cropped preview image. " +
        "First puts the character in a neutral pose (sheathes a drawn weapon or spell, default idle, neutral expression; neutralPose=false " +
        "skips it), then freezes time, puts " +
        "the camera in front of the face, aimed from the way the head faces (aimFromHead), hides the HUD, takes the shot and puts " +
        "the camera back. view: front (default), three-quarter, three-quarter-left, profile, profile-left; or angle in degrees " +
        "(0 front, 90 the character's right, -90 their left). distance from the head (default 32; below 28 the camera can end up " +
        "inside the head, so it is raised to 28), heightOffset from the head node (default -3, a straight-on portrait). hideHair " +
        "hides the hair for the shot so brows, forehead and ears show, and makes captures match pixel for pixel (hair physics can still be moving when time stops). crop 'x,y,width,height' in fractions of the frame; by " +
        "default a square on the middle of the screen. The game must be the active window. Compare captures only when they use " +
        "the same view, distance and heightOffset.")]
    public async Task<CallToolResult> CaptureFace(string view = "front", double? angle = null, double distance = 32,
        double heightOffset = -3, bool hideHair = false, bool neutralPose = true, bool aimFromHead = true, string? crop = null,
        int previewWidth = 1200, int waitSeconds = 10)
    {
        float angleDeg;
        if (angle.HasValue) angleDeg = (float)angle.Value;
        else if (!Views.TryGetValue(view.Trim(), out angleDeg))
            return Error($"view must be one of {string.Join(", ", Views.Keys)}, or pass angle");

        string? distanceNote = null;
        if (distance < MinDistance)
        {
            distanceNote = $"distance {distance} was raised to {MinDistance}: closer, the camera can end up inside the head.";
            distance = MinDistance;
        }

        JsonNode? pose = null;
        var sheathed = false;
        if (neutralPose)
        {
            // With a weapon or spell out, prepare only sheathes it (forcing the idle then leaves the hands stuck).
            for (var attempt = 0; attempt < 4; attempt++)
            {
                pose = await _pipe.SendRequestAsync("face_camera", new JsonObject { ["mode"] = "prepare" });
                if (pose?["sheathing"]?.GetValue<bool>() != true) break;
                sheathed = true;
                await Task.Delay(1000);
            }
            if (pose?["sheathing"]?.GetValue<bool>() == true)
                return Error("The weapon or spell is still out after 4 seconds: sheathe it (R) and capture again.");
            await Task.Delay(700);  // let the idle and the face settle before time stops
        }

        var enter = await _pipe.SendRequestAsync("face_camera", new JsonObject { ["mode"] = "enter", ["hideHud"] = true, ["hideHair"] = hideHair });
        if (enter?["error"] is JsonNode enterError) return Error(enterError.ToString());
        try
        {
            await Task.Delay(250);  // the free-camera state exists from the next frame
            var aim = await _pipe.SendRequestAsync("face_camera", new JsonObject
            {
                ["mode"] = "aim",
                ["angleDeg"] = angleDeg,
                ["distance"] = Math.Min(distance, 300),
                ["heightOffset"] = heightOffset,
                ["aimFromHead"] = aimFromHead,
            });
            if (aim?["error"] is JsonNode aimError) return Error(aimError.ToString());
            await Task.Delay(300);

            crop ??= AutoCrop(enter?["screen"]);
            var shot = await new UtilityTools(_pipe).TakeScreenshot(waitSeconds, previewWidth, crop, showImage: true);
            shot.Content.Insert(0, new TextContentBlock
            {
                Text = JsonSerializer.Serialize(new
                {
                    view = angle.HasValue ? null : view,
                    angleDeg,
                    distance,
                    heightOffset,
                    hideHair,
                    crop,
                    pose,
                    sheathed = sheathed ? "A weapon or spell was out: it was sheathed first (draw it again yourself)." : null,
                    camera = aim,
                    distanceNote,
                    warning = aim?["aimed"]?.GetValue<bool>() == false
                        ? "The camera may not be pointing at the face (alignment below 0.95): check the image."
                        : null,
                }),
            });
            return shot;
        }
        finally
        {
            // Never let a failed restore hide the capture's own result or error; ResetFaceCamera retries it.
            try { await _pipe.SendRequestAsync("face_camera", new JsonObject { ["mode"] = "exit" }); }
            catch { /* reported by ResetFaceCamera */ }
        }
    }

    [McpServerTool]
    [Description("Put the camera, HUD and hair back if a face capture was interrupted (for example the game stayed frozen in free camera).")]
    public async Task<object> ResetFaceCamera()
    {
        var data = await _pipe.SendRequestAsync("face_camera", new JsonObject { ["mode"] = "exit" });
        return DeserializeResponse(data);
    }

    // A square on the middle of the screen, 90% of its height: the head sits in the centre at the default distance.
    private static string? AutoCrop(JsonNode? screen)
    {
        var w = screen?["width"]?.GetValue<int>() ?? 0;
        var h = screen?["height"]?.GetValue<int>() ?? 0;
        if (w <= 0 || h <= 0) return null;
        var height = 0.9;
        var width = Math.Min(1.0, height * h / w);
        var x = (1 - width) / 2;
        var y = (1 - height) / 2;
        return string.Join(',', new[] { x, y, width, height }.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)));
    }

    private static CallToolResult Error(string message) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = message }) }],
        IsError = true,
    };
}
