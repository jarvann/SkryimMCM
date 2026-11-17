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

    // The game's expressions (SetExpressionOverride indices): the Dialogue set reads clearest in a still.
    private static readonly Dictionary<string, int> Expressions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["neutral"] = -1, ["angry"] = 0, ["afraid"] = 1, ["happy"] = 2, ["smile"] = 2, ["sad"] = 3, ["surprised"] = 4,
        ["puzzled"] = 5, ["disgusted"] = 6, ["mood-happy"] = 10, ["mood-sad"] = 11,
    };

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
        "Waits (up to 4 s) while the player is walking, then refuses: only the player can stop. " +
        "First puts the character in a neutral pose (sheathes a drawn weapon or spell, default idle, neutral expression; neutralPose=false " +
        "skips it), then freezes time, puts " +
        "the camera in front of the face, aimed from the way the head faces (aimFromHead), hides the HUD, takes the shot and puts " +
        "the camera back. view: front (default), three-quarter, three-quarter-left, profile, profile-left; or angle in degrees " +
        "(0 front, 90 the character's right, -90 their left). distance from the head (default 32; below 28 the camera can end up " +
        "inside the head, so it is raised to 28), heightOffset from the head node (default -3, a straight-on portrait). hideHair " +
        "hides the hair for the shot so brows, forehead and ears show, and makes captures match pixel for pixel (hair physics can still be moving when time stops). crop 'x,y,width,height' in fractions of the frame; by " +
        "default a square on the middle of the screen. Waits out a blink before freezing. showImage=false returns only the file paths " +
        "(the preview is still written), to save context when probing; MeasureFace reads the preview. The game must be the active " +
        "window. hideEffects (default true) hides particle effects on the character, such as cold-weather breath across the mouth. " +
        "framing in the result says whether the face was found in the middle of the shot. expression (neutral, happy/smile, sad, angry, surprised, puzzled, disgusted, afraid, mood-happy, mood-sad) with strength " +
        "0-1 (default 0.7) holds that expression for the shot, to compare with a reference that isn't neutral (a smiling photo); " +
        "it needs neutralPose (the default) and is cleared afterwards. mouthOpen 0-1 parts the lips (the game smile keeps them " +
        "closed; for an open-mouthed photo try smile 0.6 with mouthOpen 0.3). Compare captures only when they use the same view, " +
        "distance, heightOffset and expression.")]
    public async Task<CallToolResult> CaptureFace(string view = "front", double? angle = null, double distance = 32,
        double heightOffset = -3, bool hideHair = false, bool neutralPose = true, bool aimFromHead = true, string? crop = null,
        int previewWidth = 1200, int waitSeconds = 10, bool showImage = true, string expression = "neutral", double strength = 0.7,
        bool hideEffects = true, double mouthOpen = 0)
    {
        if (!Expressions.TryGetValue(expression.Trim(), out var expressionIndex))
            return Error($"expression must be one of {string.Join(", ", Expressions.Keys)}");
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

        // The game resets the brow colour on cell changes and loads: put the stored one back first.
        var browColor = await SkyrimMCP.Face.FaceState.ReapplyBrowColor(_pipe);
        JsonNode? studio = null;
        try { studio = await _pipe.SendRequestAsync("studio", new JsonObject { ["mode"] = "status" }); }
        catch (Exception) { /* older plugin: no studio */ }

        JsonNode? pose = null;
        var sheathed = false;
        if (neutralPose)
        {
            // With a weapon or spell out, prepare only sheathes it (forcing the idle then leaves the hands stuck).
            for (var attempt = 0; attempt < 8; attempt++)
            {
                pose = await _pipe.SendRequestAsync("face_camera", new JsonObject
                {
                    ["mode"] = "prepare",
                    ["expression"] = expressionIndex,
                    ["strength"] = Math.Clamp(strength, 0, 1),
                    ["mouthOpen"] = Math.Clamp(mouthOpen, 0, 1),
                });
                var moving = pose?["moving"]?.GetValue<bool>() == true;
                if (pose?["sheathing"]?.GetValue<bool>() != true && !moving) break;
                if (!moving) sheathed = true;
                await Task.Delay(500);
            }
            if (pose?["moving"]?.GetValue<bool>() == true)
                return Error("The character kept moving for 4 seconds: the player needs to stand still, then capture again.");
            if (pose?["sheathing"]?.GetValue<bool>() == true)
                return Error("The weapon or spell is still out after 4 seconds: sheathe it (R) and capture again.");
            await Task.Delay(700);  // let the idle and the face settle before time stops
        }

        // Enter refuses mid-blink (a half-closed lid skews the eye measurements): try again shortly.
        JsonNode? enter = null;
        var blinkWaits = 0;
        for (; blinkWaits < 8; blinkWaits++)
        {
            enter = await _pipe.SendRequestAsync("face_camera", new JsonObject { ["mode"] = "enter", ["hideHud"] = true, ["hideHair"] = hideHair, ["hideEffects"] = hideEffects });
            if (enter?["blinking"]?.GetValue<bool>() != true) break;
            await Task.Delay(120);
        }
        if (enter?["error"] is JsonNode enterError) return Error(enterError.ToString());
        if (enter?["blinking"]?.GetValue<bool>() == true) return Error("The eyes kept blinking for a second: try again.");
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
            var shot = await new UtilityTools(_pipe).TakeScreenshot(waitSeconds, previewWidth, crop, showImage);
            // The face detector only finds faces turned less than about 50 degrees, so a profile can't be checked.
            var framing = Math.Abs(angleDeg) <= 50 ? CheckFraming(shot)
                : "not checked: the face detector only finds faces turned less than about 50 degrees; look at the image";
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
                    browColor,
                    studio = studio == null ? null : new { preset = studio["preset"]?.ToString(), cell = studio["cell"]?.ToString() },
                    effectsHidden = enter?["effectsHidden"],
                    framing,
                    expression = expressionIndex >= 0 ? $"{expression} {Math.Clamp(strength, 0, 1):0.##}" : null,
                    eyes = enter?["eyes"],
                    blinkWaits = blinkWaits > 0 ? blinkWaits : (int?)null,
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

    // FEAT-242: a capture that missed the face (the player moved, the scene changed) is reported, not silently returned.
    private static string? CheckFraming(CallToolResult shot)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? preview = null;
        foreach (var block in shot.Content.OfType<TextContentBlock>())
        {
            try { preview ??= JsonNode.Parse(block.Text)?["preview"]?["file"]?.GetValue<string>(); }
            catch (JsonException) { }
        }
        if (preview == null || !File.Exists(preview)) return null;
        try
        {
            var face = SkyrimMCP.Face.FaceMeasurer.Shared.LocateFace(preview);
            if (face == null) return "No face found in this capture: it probably missed (the character moved or turned). Capture again.";
            var (x, y, size) = face.Value;
            if (Math.Abs(x - 0.5) > 0.2 || Math.Abs(y - 0.5) > 0.25)
                return $"The face is off centre (at {x:0.00}, {y:0.00} of the frame): capture again before comparing.";
            return $"ok (face at {x:0.00}, {y:0.00}, {size:0.00} of the frame)";
        }
        catch (Exception ex) { return "Framing not checked: " + ex.Message; }
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
