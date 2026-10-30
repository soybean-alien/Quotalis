using System.Text.Json;
using System.Text.RegularExpressions;

namespace Quotalis.Linux;

/// <summary>
/// Adds the Quotalis module to a Waybar config, or takes it out again. Waybar's config allows comments, so it is edited
/// as text and everything else in the file stays as it was. Null means nothing could safely be changed.
/// </summary>
internal static partial class WaybarConfig
{
    public const string ModuleName = "custom/quotalis";

    private const string StyleStart = "/* Quotalis */";
    private const string StyleEnd = "/* end Quotalis */";

    public static string? Add(string config, string executable)
    {
        if (config.Contains(ModuleName, StringComparison.Ordinal))
        {
            return config;
        }

        var list = ModulesRight().Match(config);
        var open = config.IndexOf('{');
        if (!list.Success || open < 0 || open > list.Index)
        {
            return null;
        }

        // Waybar runs these through a shell, so a path with a space needs quoting there too.
        var shell = executable.Contains(' ') ? "'" + executable.Replace("'", "'\\''") + "'" : executable;
        var quoted = JsonSerializer.Serialize(shell, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })[1..^1];
        var definition = $$"""

              "{{ModuleName}}": {
                "exec": "{{quoted}} waybar",
                "return-type": "json",
                "escape": true,
                "on-click": "{{quoted}} open"
              },
            """;
        var insertAt = list.Index + list.Length;
        var empty = insertAt < config.Length && config[insertAt] == ']';
        var withList = config.Insert(insertAt, empty ? $"\"{ModuleName}\"" : $"\"{ModuleName}\", ");
        return withList.Insert(open + 1, definition);
    }

    public static string Remove(string config)
    {
        var withoutDefinition = Definition().Replace(config, string.Empty);
        return ListEntry().Replace(withoutDefinition, string.Empty);
    }

    public static string AddStyle(string css) => css.Contains(StyleStart, StringComparison.Ordinal)
        ? css
        : css.TrimEnd() + $$"""


            {{StyleStart}}
            #custom-quotalis { margin: 0 7.5px; }
            #custom-quotalis.warning { font-weight: bold; }
            #custom-quotalis.critical { font-weight: bold; text-decoration: underline; }
            #custom-quotalis.stopped { opacity: 0.5; }
            {{StyleEnd}}

            """;

    public static string RemoveStyle(string css)
    {
        var start = css.IndexOf(StyleStart, StringComparison.Ordinal);
        var end = css.IndexOf(StyleEnd, StringComparison.Ordinal);
        return start < 0 || end < start ? css : css[..start].TrimEnd() + "\n" + css[(end + StyleEnd.Length)..].TrimStart('\r', '\n');
    }

    // A commented-out line doesn't count: nothing before the name on its line may start a comment.
    [GeneratedRegex("(?<=^[^/\\n]*)\"modules-right\"\\s*:\\s*\\[\\s*", RegexOptions.Multiline)]
    private static partial Regex ModulesRight();

    [GeneratedRegex("\\s*\"custom/quotalis\"\\s*:\\s*\\{[^}]*\\}\\s*,?")]
    private static partial Regex Definition();

    [GeneratedRegex("\"custom/quotalis\"\\s*,?\\s*")]
    private static partial Regex ListEntry();
}
