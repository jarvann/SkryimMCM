using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace SkyrimMCP.Tools;

// FEAT-261 follow-up: MCM Recorder (Mrowr Purr's mod) replays recorded MCM settings. Its PlayRecording doesn't return until the
// whole recording has played, so a plain Papyrus call timed out though playback carried on (2026-11-20). These start it
// without waiting and follow it through IsPlayingRecording.
[McpServerToolType]
public class McmRecorderTools : ToolBase
{
    public McmRecorderTools(IPipeClient pipe) : base(pipe) { }

    private async Task<JsonNode?> Call(string cls, string fn, JsonArray args, bool wait = true) =>
        await _pipe.SendRequestAsync("call_papyrus_function", new JsonObject
        {
            ["className"] = cls, ["functionName"] = fn, ["args"] = args, ["wait"] = wait,
        });

    [McpServerTool]
    [Description("List MCM Recorder's recordings (needs the MCM Recorder mod). PlayMcmRecording plays one.")]
    public async Task<object> ListMcmRecordings()
    {
        try
        {
            var r = await Call("McmRecorder_RecordingFiles", "GetRecordingNames", new JsonArray());
            if (r?["error"] != null) return new { error = "MCM Recorder isn't installed or didn't answer: " + r["error"] };
            // Vortex leaves a "__folder_managed_by_vortex" marker in the recordings folder: not a recording.
            var names = (r?["result"] as JsonArray ?? new JsonArray()).Select(n => n?.ToString()).Where(n => !string.IsNullOrEmpty(n) && !n!.StartsWith("__")).ToList();
            return new { count = names.Count, recordings = names };
        }
        catch (ModelContextProtocol.McpException ex) { return new { error = "MCM Recorder isn't installed or didn't answer: " + ex.Message }; }
    }

    [McpServerTool]
    [Description("Play an MCM Recorder recording by name (ListMcmRecordings shows them): it sets the recorded MCM options in each mod's " +
        "menu, step by step. Starts it, then waits up to waitSeconds (default 45) for it to finish and says whether it did; a long " +
        "one keeps playing after the reply. While it plays, moves, captures and face edits are refused. Refused in combat, scenes " +
        "or with a menu open. Changed settings stay only if the player saves afterwards. Sliders and text replay as fixed values, " +
        "but toggles and cycling options are recorded as clicks: each replay clicks them again from wherever they are now, so " +
        "playing the same recording twice can undo or change those (tell the player before replaying one twice).")]
    public async Task<object> PlayMcmRecording(string recordingName, int waitSeconds = 45)
    {
        if (await SafetyBlock("face") is string blocked) return new { error = blocked };
        var list = await ListMcmRecordings();
        var names = (System.Text.Json.JsonSerializer.SerializeToNode(list)?["recordings"] as JsonArray ?? new JsonArray()).Select(n => n?.ToString() ?? "").ToList();
        if (names.Count == 0) return list;
        var name = names.FirstOrDefault(n => n.Equals(recordingName, StringComparison.OrdinalIgnoreCase));
        if (name == null) return new { error = $"No recording named '{recordingName}'.", recordings = names };
        var start = await Call("McmRecorder_Player", "PlayRecording", new JsonArray(name, 0.0, 0.0, true), wait: false);
        if (start?["error"] != null) return new { error = "Couldn't start it: " + start["error"] };
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 5, 70));
        await Task.Delay(1500);
        while (DateTime.UtcNow < until)
        {
            var playing = (await Call("McmRecorder_Player", "IsPlayingRecording", new JsonArray()))?["result"];
            if (playing is JsonValue v && v.TryGetValue<bool>(out var p) && !p)
                return new { recording = name, finished = true, note = "Settings applied in game; they stay if the player saves. MCM Recorder may show a completion message to close." };
            await Task.Delay(1000);
        }
        return new { recording = name, finished = false, note = "Still playing: moves, captures and face edits stay refused until it finishes (GetGameSafety shows it)." };
    }
}
