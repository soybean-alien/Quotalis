using System.Globalization;
using System.Text.Json;
using Quotalis.Core.Diagnostics;

namespace Quotalis.Core.Storage;

/// <summary>
/// Brings files that other programs wrote for Quotalis into one folder: the newest copy of each file, and for
/// <c>.jsonl</c> logs every distinct line, ordered by its <c>"at"</c> time. Sources are only read, never changed,
/// so a run can be repeated safely.
/// </summary>
public static class SharedFileAdoption
{
    /// <summary>Returns the names that were brought in or updated.</summary>
    public static IReadOnlyList<string> Adopt(string target, IEnumerable<string> sources, IEnumerable<string> names)
    {
        var folders = sources.Where(s => !string.Equals(Path.GetFullPath(s), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)).ToList();
        var adopted = new List<string>();
        foreach (var name in names)
        {
            try
            {
                var copies = folders.Select(f => Path.Combine(f, name)).Where(File.Exists).ToList();
                if (copies.Count == 0)
                {
                    continue;
                }

                Directory.CreateDirectory(target);
                var destination = Path.Combine(target, name);
                var changed = name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                    ? MergeLines(destination, copies)
                    : TakeNewest(destination, copies);
                if (changed)
                {
                    adopted.Add(name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("storage", "Shared file not adopted", new { name, error = ex.GetType().Name });
            }
        }

        return adopted;
    }

    private static bool TakeNewest(string destination, List<string> copies)
    {
        var newest = copies.MaxBy(File.GetLastWriteTimeUtc)!;
        if (File.Exists(destination) && File.GetLastWriteTimeUtc(destination) >= File.GetLastWriteTimeUtc(newest))
        {
            return false;
        }

        var temp = destination + ".adopt.tmp";
        File.Copy(newest, temp, overwrite: true);
        File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(newest));
        File.Move(temp, destination, overwrite: true);
        return true;
    }

    private static bool MergeLines(string destination, List<string> copies)
    {
        var existing = File.Exists(destination) ? File.ReadAllLines(destination).Where(l => l.Length > 0).ToList() : [];
        var all = new List<string>(existing);
        var seen = new HashSet<string>(existing, StringComparer.Ordinal);
        foreach (var copy in copies)
        {
            foreach (var line in File.ReadAllLines(copy))
            {
                if (line.Length > 0 && seen.Add(line))
                {
                    all.Add(line);
                }
            }
        }

        if (all.Count == existing.Count)
        {
            return false;
        }

        // Stable order by time; lines without a readable "at" keep their place at the start.
        var ordered = all.Select((line, index) => (line, index, at: TimeOf(line)))
            .OrderBy(x => x.at ?? DateTimeOffset.MinValue).ThenBy(x => x.index)
            .Select(x => x.line);
        var temp = destination + ".adopt.tmp";
        File.WriteAllLines(temp, ordered);
        File.Move(temp, destination, overwrite: true);
        return true;
    }

    private static DateTimeOffset? TimeOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
