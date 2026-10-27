using System.Net;
using System.Text.Json;

namespace Quotalis.Core.Hermes;

/// <summary>A device-code sign-in in progress: the user approves <see cref="UserCode"/> on the portal page.</summary>
public sealed record NousDeviceCode(string DeviceCode, string UserCode, Uri VerificationUri, TimeSpan Interval, DateTimeOffset ExpiresAt);

/// <summary>Quotalis's own Nous sign-in. The refresh token is single-use: every refresh returns the one to keep.</summary>
public sealed record NousTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public enum NousPollResult { Pending, SlowDown, Granted, Denied, Expired, Failed }

public enum NousReadStatus { Ok, SignInRejected, Throttled, Unavailable }

/// <summary>An answer from the portal: what happened, the body for a 200, and how long the server asked us to wait.</summary>
public sealed record NousResponse(NousReadStatus Status, string? Body, TimeSpan? RetryAfter);

/// <summary>
/// Nous Portal's OAuth device flow and the one read Quotalis makes, as Hermes Agent does them
/// (hermes_cli/auth_device_flow.py, auth_nous.py, nous_billing.py in NousResearch/hermes-agent):
/// <c>POST /api/oauth/device/code</c>, <c>POST /api/oauth/token</c> (device code, then <c>refresh_token</c> with the
/// <c>x-nous-refresh-token</c> header) and <c>GET /api/oauth/account</c>. No scope is requested; Nous still grants
/// <c>inference:invoke</c>, so the token could run models. Quotalis never calls an inference address with it.
/// Tokens and answers are never logged.
/// </summary>
public sealed class NousPortalClient(HttpClient http, string clientId = NousPortalClient.DefaultClientId)
{
    public static readonly Uri Portal = new("https://portal.nousresearch.com");

    /// <summary>
    /// Hermes's own app name, borrowed for now (owner's decision, 2026-10-24): the portal shows the sign-in as
    /// "Connect Hermes". To be replaced by a Quotalis app name from Nous before Quotalis is shared.
    /// </summary>
    public const string DefaultClientId = "hermes-cli";

    public static readonly Uri TopUpUrl = new(Portal, "/billing?topup=open");

    public async Task<NousDeviceCode> RequestCodeAsync(DateTimeOffset now, CancellationToken cancel)
    {
        using var response = await http.PostAsync(new Uri(Portal, "/api/oauth/device/code"),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = clientId }), cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        var root = doc.RootElement;
        var device = Str(root, "device_code");
        var user = Str(root, "user_code");
        var uri = Str(root, "verification_uri_complete") ?? Str(root, "verification_uri");
        if (device is null || user is null || !Uri.TryCreate(uri, UriKind.Absolute, out var verification) || verification.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Nous sign-in answer not in a known format");
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(Num(root, "interval") ?? 5, 1, 30));
        var expires = TimeSpan.FromSeconds(Math.Clamp(Num(root, "expires_in") ?? 600, 30, 3600));
        return new NousDeviceCode(device, user, verification, interval, now + expires);
    }

    /// <summary>One poll of the token endpoint for an approved device code.</summary>
    public async Task<(NousPollResult Result, NousTokens? Tokens)> PollAsync(NousDeviceCode code, DateTimeOffset now, CancellationToken cancel)
    {
        using var response = await http.PostAsync(new Uri(Portal, "/api/oauth/token"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = clientId,
            ["device_code"] = code.DeviceCode,
        }), cancel).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return Tokens(body, now) is { } tokens ? (NousPollResult.Granted, tokens) : (NousPollResult.Failed, null);
        }

        return ErrorCode(body) switch
        {
            "authorization_pending" => (NousPollResult.Pending, null),
            "slow_down" => (NousPollResult.SlowDown, null),
            "access_denied" => (NousPollResult.Denied, null),
            "expired_token" => (NousPollResult.Expired, null),
            // The edge firewall or a hiccup: keep waiting until the code expires.
            _ when IsTransient(response) => (NousPollResult.Pending, null),
            _ => (NousPollResult.Failed, null),
        };
    }

    /// <summary>
    /// Trades the single-use refresh token for new tokens. Null with <see cref="NousReadStatus.SignInRejected"/> means
    /// the sign-in is over (sign in again); Throttled or Unavailable keep the old tokens for a later try.
    /// </summary>
    public async Task<(NousReadStatus Status, NousTokens? Tokens, TimeSpan? RetryAfter)> RefreshAsync(string refreshToken, DateTimeOffset now, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Portal, "/api/oauth/token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = clientId }),
        };
        request.Headers.Add("x-nous-refresh-token", refreshToken);
        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return Tokens(body, now) is { } tokens ? (NousReadStatus.Ok, tokens, null) : (NousReadStatus.Unavailable, null, null);
        }

        if (IsTransient(response) || ErrorCode(body) is null && response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return (response.StatusCode == HttpStatusCode.TooManyRequests ? NousReadStatus.Throttled : NousReadStatus.Unavailable, null, RetryAfter(response));
        }

        return (NousReadStatus.SignInRejected, null, null);
    }

    /// <summary>Reads the account (wallet) with the access token. Free: it runs no model.</summary>
    public async Task<NousResponse> ReadAccountAsync(string accessToken, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Portal, "/api/oauth/account"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return new NousResponse(NousReadStatus.Ok, await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false), null);
        }

        if (IsTransient(response))
        {
            return new NousResponse(response.StatusCode == HttpStatusCode.TooManyRequests ? NousReadStatus.Throttled : NousReadStatus.Unavailable, null, RetryAfter(response));
        }

        return new NousResponse(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? NousReadStatus.SignInRejected : NousReadStatus.Unavailable, null, null);
    }

    /// <summary>429, 408, 5xx, or the edge firewall's challenge (<c>x-vercel-mitigated</c> on a 403): not about the sign-in.</summary>
    private static bool IsTransient(HttpResponseMessage r) =>
        r.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)r.StatusCode >= 500
        || (r.StatusCode == HttpStatusCode.Forbidden && r.Headers.Contains("x-vercel-mitigated"));

    private static TimeSpan? RetryAfter(HttpResponseMessage r) =>
        r.Headers.RetryAfter?.Delta ?? (r.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

    internal static NousTokens? Tokens(string body, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var access = Str(doc.RootElement, "access_token");
            var refresh = Str(doc.RootElement, "refresh_token");
            if (access is null || refresh is null)
            {
                return null;
            }

            return new NousTokens(access, refresh, now + TimeSpan.FromSeconds(Math.Clamp(Num(doc.RootElement, "expires_in") ?? 3600, 60, 86400)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "error") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
