using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Quotalis.Core.Plan;

/// <summary>
/// Claude's banked limit resets ("Limit resets" on claude.ai's Usage page), counted: how many are left and when the
/// weekly window resets on its own. Numbers and dates only; grant names and ids are not kept.
/// </summary>
public sealed record ClaudeResetCount(int Available, DateTimeOffset? WeeklyResetsAt, DateTimeOffset MeasuredAt);

/// <summary>
/// Reads the banked resets the way claude.ai's own Usage page does: the usage address with <c>cedar_ember=1</c>
/// returns a <c>cedar_ember</c> object whose <c>grants</c> each carry <c>resets_left</c>. Like the usage read, it sends
/// no message to Claude. Published in <c>claude-resets.json</c> for other programs (Quotalis Mini).
/// </summary>
public static class ClaudeResetGrants
{
    /// <summary>The usage address with the reset grants included; null unless the id is a GUID.</summary>
    public static string? Url(string? organization) =>
        ClaudeWebUsage.UsageUrl(organization) is { } url ? url + "?cedar_ember=1&skip_spend=1" : null;

    /// <summary>
    /// The resets left across all grants (as the Usage page counts them); null when the answer has no grants list.
    /// Accepts the grants object on its own or inside <c>cedar_ember</c>.
    /// </summary>
    public static int? ParseAvailable(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("cedar_ember", out var inner))
            {
                if (inner.ValueKind == JsonValueKind.Null)
                {
                    return 0; // no grants for this account
                }

                root = inner;
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("grants", out var grants) || grants.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var total = 0;
            foreach (var grant in grants.EnumerateArray())
            {
                if (grant.ValueKind == JsonValueKind.Object && grant.TryGetProperty("resets_left", out var left)
                    && left.ValueKind == JsonValueKind.Number && left.TryGetInt32(out var n) && n > 0)
                {
                    total += n;
                }
            }

            return total;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the count for other programs, replacing the file in one move.</summary>
    public static void Save(string path, ClaudeResetCount count)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("available", count.Available);
            if (count.WeeklyResetsAt is { } weekly)
            {
                writer.WriteString("weeklyResetsAt", weekly.ToUniversalTime());
            }
            else
            {
                writer.WriteNull("weeklyResetsAt");
            }

            writer.WriteString("measuredAt", count.MeasuredAt.ToUniversalTime());
            writer.WriteEndObject();
        }

        var temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(temp, Encoding.UTF8.GetString(stream.ToArray()));
        File.Move(temp, path, overwrite: true);
    }
}
