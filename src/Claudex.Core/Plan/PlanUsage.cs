using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudex.Core.Diagnostics;
using Claudex.Core.Usage;

namespace Claudex.Core.Plan;

/// <summary>One usage window of a Claude subscription, as Claude Code reports it.</summary>
/// <param name="UsedPercent">0 to 100.</param>
/// <param name="IsEstimated">True when the figure is an estimate between exact readings, not a measurement.</param>
public sealed record PlanWindow(double UsedPercent, DateTimeOffset ResetsAt, bool IsEstimated = false);

/// <summary>
/// Claude plan usage, measured by Claude and handed over by Claude Code's status line or by calibration
/// from the desktop app's usage card: the 5-hour window
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
            Log.Warn("plan", "Plan usage file unreadable", new { error = ex.GetType().Name, ex.Message });
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

    internal static JsonObject? ReadSettings(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        var text = File.ReadAllText(settingsPath);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new JsonException("settings.json is not a JSON object");
    }

    internal static void Write(string settingsPath, JsonObject root)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath)!);
        if (File.Exists(settingsPath))
        {
            File.Copy(settingsPath, settingsPath + ".claudex.bak", overwrite: true);
        }

        var temp = settingsPath + ".tmp";
        // Relaxed escaping keeps the file readable (quotes and non-ASCII text stay as typed).
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(temp, settingsPath, overwrite: true);
    }
}

/// <summary>
/// Follows the plan usage file (written by the status line bridge and by calibration) with a file watcher;
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
        Log.Info("plan", "Plan usage loaded", new { found = _usage is not null, fileExists = File.Exists(_path) });
        Watch();
        Changed?.Invoke(_usage);
    }

    /// <summary>
    /// Reads the file again when the reading shown may be missing or behind (the Usage panel opened).
    /// The watcher normally delivers every write; this catches a missed event or a failed first read.
    /// </summary>
    public void Refresh()
    {
        if (_enabled)
        {
            Reload(retry: false);
        }
    }

    public void Dispose()
    {
        _enabled = false;
        _watcher?.Dispose();
        _watcher = null;
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            _watcher.Changed += (_, _) => Reload(retry: true);
            _watcher.Created += (_, _) => Reload(retry: true);
            _watcher.Renamed += (_, _) => Reload(retry: true);
            // A failed watcher stops reporting for good: start a new one and read the file once.
            _watcher.Error += (_, e) =>
            {
                Log.Warn("plan", "Plan usage watcher failed, restarting it", new { error = e.GetException()?.GetType().Name });
                if (_enabled)
                {
                    Watch();
                    Reload(retry: false);
                }
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Warn("plan", "Plan usage not watched", new { error = ex.GetType().Name });
        }
    }

    private void Reload(bool retry)
    {
        var usage = _load();
        if (usage is null && retry)
        {
            // The writer may still hold the file: try once more shortly.
            _ = Task.Delay(300).ContinueWith(_ => Reload(retry: false), TaskScheduler.Default);
            return;
        }

        lock (_gate)
        {
            if (usage is null || usage == _usage)
            {
                return;
            }

            _usage = usage;
        }

        Changed?.Invoke(usage);
    }

    private readonly object _gate = new();
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
    public static string Reason(PlanDataState state) => state switch
    {
        PlanDataState.WaitingForData => "Connected, no data yet. Claude Code sends plan usage from terminal sessions, after a reply; the VS Code extension does not.",
        PlanDataState.Stale => "No recent update. Plan usage refreshes with your next reply in a Claude Code session.",
        PlanDataState.OtherStatusLine => "Claude Code already runs another status line.",
        PlanDataState.Unreadable => "Claude Code settings could not be read.",
        PlanDataState.NotConnected => "Connect Claude Code in Settings, Modules.",
        _ => string.Empty,
    };
}
