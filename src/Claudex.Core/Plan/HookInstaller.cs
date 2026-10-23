using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claudex.Core.Plan;

/// <summary>
/// <c>Claudex.exe --claudex-hooks connect|disconnect</c>, run by the installer and uninstaller.
/// Connect points every Claudex entry (status line, Codex Stop hook) at this executable, wherever an earlier
/// copy put them, and adds the ones that are missing; a status line the user set up themselves is left alone.
/// It also removes the Claude Stop hook of versions before 0.5.14 (see <see cref="LegacyClaudeStopHook"/>).
/// Disconnect removes only the entries that run this executable, so a development
/// build's hooks survive uninstalling the installed copy. Other settings and hooks are never touched, and
/// each file is backed up before it is written (see <see cref="ClaudeStatusLineSetup"/>).
/// </summary>
public static class HookInstaller
{
    public const string Argument = "--claudex-hooks";

    public static string CodexHooksPath(string? codexHome = null)
    {
        var dir = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        return Path.Combine(dir, "hooks.json");
    }

    public static string CodexCommandFor(string executable) => $"\"{executable}\" {CodexStopHook.Argument}";

    public static void Connect(string claudeSettings, string codexHooks, string executable)
    {
        if (Repoint(claudeSettings, executable) is not null)
        {
            LegacyClaudeStopHook.Remove(claudeSettings);
            ClaudeStatusLineSetup.Connect(claudeSettings, executable);
        }

        var codex = Repoint(codexHooks, executable) ?? throw new JsonException("hooks.json is not readable");
        if (!StopCommands(codex).Any(c => IsOurs(c, CodexStopHook.Argument)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(codexHooks)!);
            AddStopHook(codex, new JsonObject
            {
                ["type"] = "command",
                ["command"] = CodexCommandFor(executable),
                ["timeout"] = 30,
                ["statusMessage"] = "Claudex usage check",
            });
            ClaudeStatusLineSetup.Write(codexHooks, codex);
        }
    }

    public static void Disconnect(string claudeSettings, string codexHooks, string executable)
    {
        foreach (var path in new[] { claudeSettings, codexHooks })
        {
            JsonObject? root;
            try
            {
                root = ClaudeStatusLineSetup.ReadSettings(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }

            if (root is null)
            {
                continue;
            }

            var changed = false;
            if (Command(root["statusLine"]) is { } line && Runs(line, executable) && ClaudeStatusLineSetup.IsOurs(line))
            {
                root.Remove("statusLine");
                changed = true;
            }

            if (root["hooks"]?["Stop"] is JsonArray stop)
            {
                foreach (var group in stop.OfType<JsonObject>().ToList())
                {
                    if (group["hooks"] is not JsonArray inner)
                    {
                        continue;
                    }

                    foreach (var hook in inner.OfType<JsonObject>().Where(h => Command(h) is { } c && Runs(c, executable) && IsClaudex(c)).ToList())
                    {
                        inner.Remove(hook);
                        changed = true;
                    }

                    if (inner.Count == 0)
                    {
                        stop.Remove(group);
                    }
                }

                if (stop.Count == 0)
                {
                    ((JsonObject)root["hooks"]!).Remove("Stop");
                }
            }

            if (changed)
            {
                ClaudeStatusLineSetup.Write(path, root);
            }
        }
    }

    /// <summary>Rewrites every Claudex command in the file to run <paramref name="executable"/>; null when unreadable.</summary>
    private static JsonObject? Repoint(string path, string executable)
    {
        JsonObject root;
        try
        {
            root = ClaudeStatusLineSetup.ReadSettings(path) ?? new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        var changed = false;
        if (root["statusLine"] is JsonObject statusLine && Command(statusLine) is { } line && ClaudeStatusLineSetup.IsOurs(line))
        {
            changed |= Set(statusLine, ClaudeStatusLineSetup.CommandFor(executable));
        }

        foreach (var hook in StopHooks(root))
        {
            var command = Command(hook);
            if (IsOurs(command, CodexStopHook.Argument))
            {
                changed |= Set(hook, CodexCommandFor(executable));
            }
        }

        if (changed)
        {
            ClaudeStatusLineSetup.Write(path, root);
        }

        return root;
    }

    private static bool Set(JsonObject hook, string command)
    {
        if (Command(hook) == command)
        {
            return false;
        }

        hook["command"] = command;
        return true;
    }

    private static void AddStopHook(JsonObject root, JsonObject hook)
    {
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        var stop = hooks["Stop"] as JsonArray ?? new JsonArray();
        stop.Add(new JsonObject { ["hooks"] = new JsonArray(hook) });
        hooks["Stop"] = stop;
        root["hooks"] = hooks;
    }

    private static IEnumerable<JsonObject> StopHooks(JsonObject root) =>
        (root["hooks"]?["Stop"] as JsonArray ?? [])
            .SelectMany(group => group?["hooks"] as JsonArray ?? [])
            .OfType<JsonObject>();

    private static IEnumerable<string?> StopCommands(JsonObject root) => StopHooks(root).Select(Command);

    private static bool IsOurs(string? command, string argument) =>
        command is not null && command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(argument, StringComparer.Ordinal);

    private static bool IsClaudex(string command) =>
        IsOurs(command, LegacyClaudeStopHook.Argument) || IsOurs(command, CodexStopHook.Argument);

    private static bool Runs(string command, string executable) => command.Contains(executable, StringComparison.OrdinalIgnoreCase);

    private static string? Command(JsonNode? hook) =>
        hook?["command"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
