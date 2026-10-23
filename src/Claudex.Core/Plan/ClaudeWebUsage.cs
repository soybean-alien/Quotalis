using System.Globalization;
using System.Text.Json;

namespace Claudex.Core.Plan;

/// <summary>Where the claude.ai sign-in stands.</summary>
public enum ClaudeWebStatus
{
    SignedOut,
    SignedIn,

    /// <summary>claude.ai no longer accepts the sign-in.</summary>
    Expired,

    /// <summary>claude.ai answered, but not in a format Claudex knows.</summary>
    FormatChanged,
}

/// <summary>
/// Plan usage read from claude.ai the way its own Usage page reads it: <c>/api/organizations</c> for the account's
/// organization, then <c>/api/organizations/{id}/usage</c> with <c>five_hour</c> and <c>seven_day</c>, each with
/// <c>utilization</c> (0-100) and <c>resets_at</c>. These requests send no message to Claude, so they never count
/// against the plan limits. Only the two percentages and reset times are used; the organization id stays in memory.
/// </summary>
public static class ClaudeWebUsage
{
    public const string Origin = "https://claude.ai";

    public const string LoginUrl = Origin + "/login";

    public const string OrganizationsUrl = Origin + "/api/organizations";

    /// <summary>The cookie claude.ai sets for a signed-in browser. Only its presence is checked, never its value.</summary>
    public const string SessionCookie = "sessionKey";

    /// <summary>The cookie claude.ai keeps for the organization last used.</summary>
    public const string ActiveOrganizationCookie = "lastActiveOrg";

    /// <summary>The usage address of one organization; null unless the id is a GUID.</summary>
    public static string? UsageUrl(string? organization) =>
        Guid.TryParse(organization, out var id) ? $"{OrganizationsUrl}/{id:D}/usage" : null;

    /// <summary>
    /// The organization to read: the one claude.ai used last when it is in the list, else the first with chat, else the
    /// first. Null when the answer is not a list of organizations.
    /// </summary>
    public static string? PickOrganization(string json, string? lastActive)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var ids = new List<(string Id, bool Chat)>();
            foreach (var org in doc.RootElement.EnumerateArray())
            {
                if (org.ValueKind == JsonValueKind.Object && org.TryGetProperty("uuid", out var uuid) && uuid.ValueKind == JsonValueKind.String
                    && Guid.TryParse(uuid.GetString(), out var id))
                {
                    var chat = org.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                        && caps.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "chat");
                    ids.Add((id.ToString("D"), chat));
                }
            }

            if (Guid.TryParse(lastActive, out var last) && ids.Any(o => o.Id == last.ToString("D")))
            {
                return last.ToString("D");
            }

            return ids.Where(o => o.Chat).Select(o => o.Id).FirstOrDefault() ?? ids.Select(o => o.Id).FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The usage answer as plan usage; null when it is not in the known format (then nothing is shown). A window
    /// without a reset time (nothing used in it yet) is left out rather than given a guessed one.
    /// </summary>
    public static PlanUsage? Parse(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !(Known(root, "five_hour") && Known(root, "seven_day")))
            {
                return null;
            }

            return new PlanUsage(Window(root, "five_hour"), Window(root, "seven_day"), now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether an answer means the sign-in is no longer accepted: 401 or 403 with claude.ai's own JSON error. A 403
    /// page that is not JSON (a network check in between) is a temporary failure, not a signed-out account.
    /// </summary>
    public static bool IsSignInRefused(int status, string body)
    {
        if (status is not (401 or 403))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Known(JsonElement root, string name) =>
        root.TryGetProperty(name, out var window) && window.ValueKind is JsonValueKind.Object or JsonValueKind.Null;

    private static PlanWindow? Window(JsonElement root, string name)
    {
        var window = root.GetProperty(name);
        if (window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent)
            || !double.IsFinite(percent)
            || !window.TryGetProperty("resets_at", out var resets) || resets.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(resets.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var resetsAt))
        {
            return null;
        }

        return new PlanWindow(Math.Clamp(percent, 0, 100), resetsAt);
    }
}

/// <summary>
/// When to read claude.ai next: a few seconds after Claude Code writes (a reply landed), at most every
/// <see cref="MinGap"/> while it keeps writing, every <see cref="Idle"/> otherwise, and less often after failures.
/// </summary>
public sealed class ClaudeWebSchedule
{
    public static readonly TimeSpan AfterActivity = TimeSpan.FromSeconds(4);

    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    private DateTimeOffset? _lastAttempt;
    private DateTimeOffset? _activityDue;
    private int _failures;

    /// <summary>Claude Code wrote to a session: read soon. Later writes before that read do not push it back.</summary>
    public void OnActivity(DateTimeOffset now) => _activityDue ??= now + AfterActivity;

    public void OnAttempt(DateTimeOffset now, bool succeeded)
    {
        _lastAttempt = now;
        _activityDue = null;
        _failures = succeeded ? 0 : Math.Min(_failures + 1, 10);
    }

    /// <summary>Forget the history, for example after signing in: the next read is due at once.</summary>
    public void Reset()
    {
        _lastAttempt = null;
        _activityDue = null;
        _failures = 0;
    }

    public DateTimeOffset Next(DateTimeOffset now)
    {
        if (_lastAttempt is not { } last)
        {
            return now;
        }

        if (_failures > 0)
        {
            var backoff = TimeSpan.FromTicks(Math.Min(Idle.Ticks << (_failures - 1), MaxBackoff.Ticks));
            return last + backoff;
        }

        var regular = last + Idle;
        if (_activityDue is { } due)
        {
            var soon = due > last + MinGap ? due : last + MinGap;
            return soon < regular ? soon : regular;
        }

        return regular;
    }
}
