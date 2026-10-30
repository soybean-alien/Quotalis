using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quotalis.Linux;

/// <summary>
/// Adds the Quotalis command module to Omarchy's shell.json (its Quickshell bar, Omarchy 4 and later), or takes it out
/// again. The file is plain JSON that Omarchy rewrites itself, so it is edited as JSON. Null means nothing could safely be changed.
/// </summary>
internal static class OmarchyBarConfig
{
    public const string ModuleId = "quotalis";

    // Each refresh shows the next tool's turn. 0.1.1 and earlier wrote this interval, before the tools took turns.
    private const int IntervalSeconds = WaybarOutput.CycleSeconds;
    private const int OldIntervalSeconds = 30;

    private static readonly JsonSerializerOptions Written = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string? Add(string config, string executable)
    {
        if (Parse(config) is not { } root || Right(root) is not { } right)
        {
            return null;
        }

        if (right.FirstOrDefault(IsOurs) is { } ours)
        {
            // A different interval is the user's own choice, so only the old default moves.
            if (ours["interval"]?.GetValueKind() != JsonValueKind.Number || ours["interval"]!.GetValue<int>() != OldIntervalSeconds)
            {
                return config;
            }

            ours["interval"] = IntervalSeconds;
            return root.ToJsonString(Written) + "\n";
        }

        // The bar runs these through bash, so a path with a space needs quoting there too.
        var shell = executable.Contains(' ') ? "'" + executable.Replace("'", "'\\''") + "'" : executable;
        right.Insert(0, new JsonObject
        {
            ["id"] = ModuleId,
            ["type"] = "command",
            ["exec"] = shell + " bar",
            ["interval"] = IntervalSeconds,
            ["onClick"] = shell + " open",
        });
        return root.ToJsonString(Written) + "\n";
    }

    public static string? Remove(string config)
    {
        if (Parse(config) is not { } root || root["bar"]?["layout"] is not JsonObject layout)
        {
            return null;
        }

        var removed = 0;
        foreach (var (_, section) in layout)
        {
            if (section is JsonArray modules)
            {
                removed += modules.RemoveAll(IsOurs);
            }
        }

        return removed == 0 ? config : root.ToJsonString(Written) + "\n";
    }

    private static bool IsOurs(JsonNode? module) => module?["id"]?.GetValueKind() == JsonValueKind.String && module["id"]!.GetValue<string>() == ModuleId;

    private static JsonArray? Right(JsonObject root) => root["bar"]?["layout"]?["right"] as JsonArray;

    private static JsonObject? Parse(string config)
    {
        try
        {
            return JsonNode.Parse(config) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
