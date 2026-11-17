using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkyrimMCP.Face;

namespace SkyrimMCP.Tools;

/// <summary>Hair picker (FEAT-236): search the installed hairstyles by shape and see them, without loading any in game.</summary>
[McpServerToolType]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class HairTools : ToolBase
{
    public HairTools(IPipeClient pipe) : base(pipe) { }

    [McpServerTool]
    [Description("Find hairstyles by shape without trying them on in game (FEAT-236): reads every hair mesh the character menu offers " +
        "the player (her race and sex) from disk, measures it against her head, and returns a contact sheet of front and side " +
        "silhouettes plus the measures. Filters: length (short: above the ears; bob: to the chin; shoulder; long; very long), " +
        "fringe (true: hair covers the forehead above the brows; false: forehead clear), behind (true: much of the hair is behind " +
        "the head, as buns, ponytails or long backs), nameContains (editor ID, name or mod). sort: length, fringe or name. page " +
        "and pageSize (default 12, max 16) step through the matches. Each entry's index is the hair's number in the character " +
        "menu; apply one with SetHeadPart(type: 'hair', part: <editorId>). Meshes are read loose or from BSA archives. The first call measures every mesh (can take a minute); later calls use a cache.")]
    public async Task<CallToolResult> FindHairstyles(string? length = null, bool? fringe = null, bool? behind = null,
        string? nameContains = null, string sort = "length", int page = 1, int pageSize = 12, bool showImage = true)
    {
        try
        {
            pageSize = Math.Clamp(pageSize, 1, 16);
            var list = await _pipe.SendRequestAsync("list_head_part_choices", new JsonObject { ["type"] = "hair", ["start"] = 0, ["count"] = 2000 });
            if (list?["error"] is JsonNode error) return Error(error.ToString());
            var entries = (list?["entries"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            var appearance = await _pipe.SendRequestAsync("get_appearance");
            var faceModel = (appearance?["headParts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .FirstOrDefault(p => p["type"]?.GetValue<int>() == 1)?["model"]?.GetValue<string>();
            var head = HairCatalog.Head(faceModel);

            var measured = new System.Collections.Concurrent.ConcurrentBag<(JsonObject Entry, HairCatalog.Shape? Shape)>();
            await Task.Run(() => Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = 6 }, e =>
            {
                var model = e["model"]?.GetValue<string>();
                measured.Add((e, model == null ? null : HairCatalog.Measure(model, head)));
            }));
            HairCatalog.SaveCache();

            var unreadable = measured.Count(m => m.Shape == null);
            var matches = measured.Where(m => m.Shape != null).Where(m =>
            {
                var s = m.Shape!;
                if (length != null && !s.Length.Equals(length.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
                if (fringe == true && s.Fringe < 0.35) return false;
                if (fringe == false && s.Fringe > 0.15) return false;
                if (behind == true && s.Behind < 0.25) return false;
                if (behind == false && s.Behind > 0.15) return false;
                if (!string.IsNullOrWhiteSpace(nameContains))
                {
                    var text = $"{m.Entry["editorId"]} {m.Entry["name"]} {m.Entry["mod"]}";
                    if (!text.Contains(nameContains.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            });
            matches = sort.Trim().ToLowerInvariant() switch
            {
                "fringe" => matches.OrderByDescending(m => m.Shape!.Fringe),
                "name" => matches.OrderBy(m => m.Entry["editorId"]?.GetValue<string>()),
                _ => matches.OrderBy(m => m.Shape!.LengthRatio),
            };
            var all = matches.ToList();
            var pageItems = all.Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).ToList();

            var rows = new JsonArray(pageItems.Select((m, i) => (JsonNode?)new JsonObject
            {
                ["tile"] = i + 1,
                ["index"] = m.Entry["index"]?.DeepClone(),
                ["editorId"] = m.Entry["editorId"]?.DeepClone(),
                ["name"] = m.Entry["name"]?.DeepClone(),
                ["mod"] = m.Entry["mod"]?.DeepClone(),
                ["length"] = m.Shape!.Length,
                ["lengthRatio"] = m.Shape.LengthRatio,
                ["fringe"] = m.Shape.Fringe,
                ["behind"] = m.Shape.Behind,
                ["width"] = m.Shape.Width,
                ["swept"] = m.Shape.Swept,
            }).ToArray());

            var result = new CallToolResult
            {
                Content = [new TextContentBlock { Text = new JsonObject
                {
                    ["hairs"] = entries.Count,
                    ["matches"] = all.Count,
                    ["page"] = page,
                    ["pages"] = (all.Count + pageSize - 1) / pageSize,
                    ["notReadable"] = unreadable > 0 ? $"{unreadable} hair meshes weren't found or couldn't be read" : null,
                    ["entries"] = rows,
                    ["measures"] = "length: head heights below the top of the head; fringe: share of the forehead covered (0-1); behind: share of the hair behind the head; width: hair width over head width; swept: how lopsided (0 even, 1 all to one side)",
                    ["apply"] = "SetHeadPart(type: 'hair', part: '<editorId>')",
                }.ToJsonString() }],
            };
            if (showImage && pageItems.Count > 0)
            {
                var sheet = HairCatalog.DrawSheet(
                    pageItems.Select((m, i) => ($"{i + 1}. {m.Entry["editorId"]} ({m.Shape!.Length}{(m.Shape.Fringe >= 0.35 ? ", fringe" : "")})",
                                                m.Entry["model"]!.GetValue<string>())).ToList(),
                    faceModel,
                    Path.Combine(SkyLinkSettings.Folder, "hair_sheets", $"hairs_p{page}.jpg"));
                result.Content.Add(new ImageContentBlock { Data = Convert.ToBase64String(await File.ReadAllBytesAsync(sheet)), MimeType = "image/jpeg" });
            }
            return result;
        }
        catch (Exception ex) { return Error("FindHairstyles: " + ex.Message); }
    }

    private static CallToolResult Error(string message) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = message }) }],
        IsError = true,
    };
}
