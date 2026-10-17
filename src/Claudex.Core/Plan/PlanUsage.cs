using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudex.Core.Diagnostics;
using Claudex.Core.Usage;

namespace Claudex.Core.Plan;

/// <summary>One usage window of a Claude subscription, as Claude Code reports it.</summary>
/// <param name="UsedPercent">0 to 100.</param>
/// <param name="IsEstimated">True when the figure is an estimate between exact readings (Tokenz), not a measurement.</param>
public sealed record PlanWindow(double UsedPercent, DateTimeOffset ResetsAt, bool IsEstimated = false);

/// <summary>
/// Claude plan usage, measured by Claude Code and handed to its status line: the 5-hour window
/// ("current") and the 7-day window ("weekly"). Never computed from local token counts.
/// </summary>
public sealed record PlanUsage(PlanWindow? Current, PlanWindow? Weekly, DateTimeOffset MeasuredAt)
{
    /// <summary>The current window, or null once it has reset (its old percentage no longer holds).</summary>
    public PlanWindow? CurrentAt(DateTimeOffset now) => Current is { } c && c.ResetsAt > now ? c : null;

    public PlanWindow? WeeklyAt(DateTimeOffset now) => Weekly is { } w && w.ResetsAt > now ? w : null;
}

/// <summary>
/// Reads the documented status line input of Claude Code. Only <c>rate_limits</c> is used:
/// <c>five_hour</c> and <c>seven_day</c>, each with <c>used_percentage</c> (0-100) and
/// <c>resets_at</c> (Unix seconds). It is present only for Claude.ai subscribers, after the first
/// response of a session. Everything else in the input (paths, model, cost, session) is ignored.
/// </summary>
public static class ClaudeStatusLine
{
    public static PlanUsage? Extract(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var current = Window(limits, "five_hour");
            var weekly = Window(limits, "seven_day");
            return current is null && weekly is null ? null : new PlanUsage(current, weekly, now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PlanWindow? Window(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent)
            || !window.TryGetProperty("resets_at", out var resets) || resets.ValueKind != JsonValueKind.Number || !resets.TryGetInt64(out var seconds))
        {
            return null;
        }

        if (!double.IsFinite(percent) || seconds <= 0)
        {
            return null;
        }

        return new PlanWindow(Math.Clamp(percent, 0, 100), DateTimeOffset.FromUnixTimeSeconds(seconds));
    }
}

/// <summary>
/// Reads Claude plan usage from Tokenz (soybean_alien's usage widget), which keeps
/// <c>%LOCALAPPDATA%\Tokenz\tokenz_status.json</c> current while it runs. Tokenz gets exact readings
/// from the Claude desktop app's usage card and estimates between them; each window says which.
/// Only <c>claude.five_hour</c>, <c>claude.weekly</c> and <c>written_at</c> are read.
/// </summary>
public static class TokenzStatus
{
    public static string DefaultPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tokenz", "tokenz_status.json");

    public static PlanUsage? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return Extract(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("plan", "Tokenz status unreadable", new { error = ex.GetType().Name });
            return null;
        }
    }

    public static PlanUsage? Extract(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("claude", out var claude) || claude.ValueKind != JsonValueKind.Object
                || !TryTime(root, "written_at", out var writtenAt))
            {
                return null;
            }

            var current = Window(claude, "five_hour");
            var weekly = Window(claude, "weekly");
            return current is null && weekly is null ? null : new PlanUsage(current, weekly, writtenAt);
        }
        catch (JsonException)
        {
            // Tokenz rewrites the file in place; a half-written read is skipped until the next change.
            return null;
        }
    }

    private static PlanWindow? Window(JsonElement claude, string name)
    {
        if (!claude.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("percent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent)
            || !double.IsFinite(percent)
            || !TryTime(window, "resets_at", out var resetsAt))
        {
            return null;
        }

        var estimated = window.TryGetProperty("estimated", out var e) && e.ValueKind == JsonValueKind.True;
        return new PlanWindow(Math.Clamp(percent, 0, 100), resetsAt, estimated);
    }

    private static bool TryTime(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        return element.TryGetProperty(name, out var raw)
            && raw.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(raw.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }
}

/// <summary>The last plan usage Claude Code reported, kept in a small file (numbers only).</summary>
public sealed class PlanUsageStore(string path)
{
    public string Path => path;

    public PlanUsage? Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.PlanUsage) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Debug("plan", "Plan usage file unreadable", new { error = ex.GetType().Name });
            return null;
        }
    }

    public void Save(PlanUsage usage)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(usage, CoreJsonContext.Default.PlanUsage));
        File.Move(temp, path, overwrite: true);
    }
}

public enum PlanConnection
{
    /// <summary>Claude Code's settings do not run Claudex as the status line.</summary>
    NotConnected,

    /// <summary>Claudex is the status line; plan usage arrives with the next response.</summary>
    Connected,

    /// <summary>The user has their own status line; it is left alone.</summary>
    OtherStatusLine,

    /// <summary>Claude Code's settings could not be read.</summary>
    Unreadable,

    /// <summary>Plan usage comes from Tokenz instead of Claude Code's status line.</summary>
    Tokenz,
}

/// <summary>
/// Connects Claudex as Claude Code's status line command, which is how Claude Code hands
/// plan usage to other programs. Edits only the <c>statusLine</c> entry of Claude Code's
/// <c>settings.json</c>, keeps every other setting, writes a backup first, and never replaces a
/// status line the user set up themselves. Disconnecting removes only our own entry.
/// </summary>
public static class ClaudeStatusLineSetup
{
    public const string Argument = "--claudex-statusline";

    public static string SettingsPath(string? configDir = null)
    {
        var dir = configDir ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        return System.IO.Path.Combine(dir, "settings.json");
    }

    /// <summary>The command Claude Code runs: our executable with the status line argument.</summary>
    public static string CommandFor(string executable) => $"\"{executable}\" {Argument}";

    public static bool IsOurs(string? command) => command?.Contains(Argument, StringComparison.Ordinal) == true;

    public static PlanConnection Inspect(string settingsPath)
    {
        try
        {
            var statusLine = ReadSettings(settingsPath)?["statusLine"];
            if (statusLine is null)
            {
                return PlanConnection.NotConnected;
            }

            var command = statusLine["command"]?.GetValue<string>();
            return IsOurs(command) ? PlanConnection.Connected : PlanConnection.OtherStatusLine;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return PlanConnection.Unreadable;
        }
    }

    /// <summary>Adds our status line unless another one exists. Returns the resulting state.</summary>
    public static PlanConnection Connect(string settingsPath, string executable)
    {
        var state = Inspect(settingsPath);
        if (state is PlanConnection.Connected or PlanConnection.OtherStatusLine or PlanConnection.Unreadable)
        {
            return state;
        }

        var root = ReadSettings(settingsPath) ?? new JsonObject();
        root["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = CommandFor(executable),
        };
        Write(settingsPath, root);
        return PlanConnection.Connected;
    }

    /// <summary>Removes our status line (and only ours).</summary>
    public static PlanConnection Disconnect(string settingsPath)
    {
        if (Inspect(settingsPath) != PlanConnection.Connected)
        {
            return Inspect(settingsPath);
        }

        var root = ReadSettings(settingsPath)!;
        root.Remove("statusLine");
        Write(settingsPath, root);
        return PlanConnection.NotConnected;
    }

    private static JsonObject? ReadSettings(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        var text = File.ReadAllText(settingsPath);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new JsonException("settings.json is not a JSON object");
    }

    private static void Write(string settingsPath, JsonObject root)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath)!);
        if (File.Exists(settingsPath))
        {
            File.Copy(settingsPath, settingsPath + ".claudex.bak", overwrite: true);
        }

        var temp = settingsPath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, settingsPath, overwrite: true);
    }
}

/// <summary>
/// Follows a plan usage file (the status line bridge's, or Tokenz's status file) with a file watcher;
/// no polling.
/// </summary>
public sealed class PlanUsageService : IDisposable
{
    private readonly string _path;
    private readonly Func<PlanUsage?> _load;
    private FileSystemWatcher? _watcher;
    private PlanUsage? _usage;
    private bool _enabled;

    public PlanUsageService(PlanUsageStore store)
        : this(store.Path, store.Load)
    {
    }

    public PlanUsageService(string path, Func<PlanUsage?> load)
    {
        _path = path;
        _load = load;
    }

    /// <summary>Raised on a background thread when new plan usage arrived.</summary>
    public event Action<PlanUsage?>? Changed;

    public PlanUsage? Usage => _usage;

    public void Configure(bool enabled)
    {
        if (enabled == _enabled)
        {
            return;
        }

        _enabled = enabled;
        _watcher?.Dispose();
        _watcher = null;
        if (!enabled)
        {
            _usage = null;
            Changed?.Invoke(null);
            return;
        }

        _usage = _load();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            _watcher.Changed += (_, _) => Reload();
            _watcher.Created += (_, _) => Reload();
            _watcher.Renamed += (_, _) => Reload();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Debug("plan", "Plan usage not watched", new { error = ex.GetType().Name });
        }

        Changed?.Invoke(_usage);
    }

    public void Dispose()
    {
        _enabled = false;
        _watcher?.Dispose();
        _watcher = null;
    }

    private void Reload()
    {
        var usage = _load();
        if (usage is null || usage == _usage)
        {
            return;
        }

        _usage = usage;
        Changed?.Invoke(usage);
    }
}

/// <summary>What the plan usage link can deliver right now; "connected" alone is not "available".</summary>
public enum PlanDataState
{
    NotConnected,

    /// <summary>The status line is set up, but Claude Code has not handed over any plan usage yet.</summary>
    WaitingForData,

    /// <summary>Plan usage arrived recently.</summary>
    Receiving,

    /// <summary>The last plan usage is old (no terminal session for a while), or its windows have reset.</summary>
    Stale,

    OtherStatusLine,
    Unreadable,
}

/// <summary>Turns the connection and the last measurement into one honest state and its wording.</summary>
public static class PlanStatus
{
    /// <summary>Older than this, plan usage is reported as stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    public static PlanDataState StateOf(PlanConnection connection, PlanUsage? usage, DateTimeOffset now) => connection switch
    {
        PlanConnection.OtherStatusLine => PlanDataState.OtherStatusLine,
        PlanConnection.Unreadable => PlanDataState.Unreadable,
        PlanConnection.NotConnected => PlanDataState.NotConnected,
        _ when usage is null => PlanDataState.WaitingForData,
        _ when now - usage.MeasuredAt > StaleAfter || (usage.CurrentAt(now) is null && usage.WeeklyAt(now) is null) => PlanDataState.Stale,
        _ => PlanDataState.Receiving,
    };

    /// <summary>Why the plan block says "Unavailable" (or that it may be out of date), in one line.</summary>
    public static string Reason(PlanDataState state, PlanConnection connection = PlanConnection.Connected) => (state, connection) switch
    {
        (PlanDataState.WaitingForData, PlanConnection.Tokenz) => "Tokenz has no Claude reading yet. It gets one from the desktop app's usage card.",
        (PlanDataState.Stale, PlanConnection.Tokenz) => "Tokenz has not updated for over an hour. Is it running?",
        _ => ReasonFor(state),
    };

    private static string ReasonFor(PlanDataState state) => state switch
    {
        PlanDataState.WaitingForData => "Connected, no data yet. Claude Code sends plan usage from terminal sessions, after a reply; the VS Code extension does not.",
        PlanDataState.Stale => "No recent update. Plan usage refreshes with your next reply in a Claude Code terminal session.",
        PlanDataState.OtherStatusLine => "Claude Code already runs another status line.",
        PlanDataState.Unreadable => "Claude Code settings could not be read.",
        PlanDataState.NotConnected => "Connect Claude Code in Settings, Modules.",
        _ => string.Empty,
    };
}
