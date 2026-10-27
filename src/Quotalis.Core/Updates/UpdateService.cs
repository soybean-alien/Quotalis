using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Quotalis.Core.Diagnostics;

namespace Quotalis.Core.Updates;

/// <summary>A Quotalis release on GitHub: its tag, version, installer asset and checksum sources.</summary>
/// <param name="Sha256">GitHub's own digest of the installer asset (lowercase hex), when the release has one.</param>
/// <param name="SumsAsset">The release's <c>SHA256SUMS-X.Y.Z.txt</c>, when attached.</param>
/// <param name="InstallerUrl">The installer's public download link (<c>browser_download_url</c>), when it is one of this repository's.</param>
/// <param name="SumsUrl">The checksum file's public download link, likewise.</param>
/// <param name="ViaCli">Found through the GitHub CLI because the repository can't be read anonymously; download it the same way.</param>
public sealed record QuotalisRelease(
    string Tag,
    Version Version,
    string Name,
    string Installer,
    string? Sha256,
    string? SumsAsset,
    string? InstallerUrl = null,
    string? SumsUrl = null,
    bool ViaCli = false);

public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Self-update from the <c>soybean-alien/Quotalis</c> GitHub releases. A public repository is read anonymously over
/// HTTPS (GitHub's REST API for the latest release, then the asset's download link), so no GitHub CLI or account is
/// needed. While the repository is private GitHub answers 404 to anonymous requests; then, and only then, Quotalis
/// falls back to the GitHub CLI (<c>gh</c>), which uses the account it is already signed in with. Quotalis itself
/// never sees or stores a token. The downloaded installer's SHA-256 must match GitHub's asset digest or the release's
/// <c>SHA256SUMS-X.Y.Z.txt</c>; without either, the update is refused.
/// </summary>
public sealed class UpdateService
{
    public const string Repo = "soybean-alien/Quotalis";

    private const string LatestUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    private const string DownloadPrefix = $"https://github.com/{Repo}/releases/download/";

    private static readonly Regex InstallerName = new(@"^Quotalis-Setup-\d+\.\d+\.\d+\.exe$", RegexOptions.CultureInvariant);
    private static readonly Regex SumsName = new(@"^SHA256SUMS-\d+\.\d+\.\d+\.txt$", RegexOptions.CultureInvariant);
    private static readonly Regex Digest = new(@"^sha256:([0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly Func<IReadOnlyList<string>, TimeSpan, Task<ProcessResult>> _gh;

    /// <param name="http">Anonymous HTTPS to GitHub; tests pass one over a fake handler. Null makes a default client.</param>
    /// <param name="gh">Runs gh with the given arguments (the private-repository fallback); tests pass a fake. Null uses the installed GitHub CLI.</param>
    public UpdateService(HttpClient? http = null, Func<IReadOnlyList<string>, TimeSpan, Task<ProcessResult>>? gh = null)
    {
        _http = http ?? CreateHttpClient();
        _gh = gh ?? RunGhAsync;
    }

    /// <summary>"0.5.3" from an informational version such as "0.5.3+4871515…".</summary>
    public static Version ParseVersion(string informational)
    {
        var core = informational.Split('+', '-')[0];
        return Version.TryParse(core, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);
    }

    public static bool IsNewer(QuotalisRelease release, Version current) => release.Version > current;

    /// <summary>Reads a GitHub release object (what <c>GET repos/…/releases/latest</c> returns).</summary>
    public static QuotalisRelease ParseRelease(string json)
    {
        JsonObject release;
        try
        {
            release = JsonNode.Parse(json) as JsonObject ?? throw new UpdateException("GitHub's answer isn't a release.");
        }
        catch (JsonException)
        {
            throw new UpdateException("GitHub's answer isn't valid JSON.");
        }

        var tag = Str(release["tag_name"]) ?? throw new UpdateException("The latest release has no tag.");
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version.Build < 0 || version.Revision >= 0)
        {
            throw new UpdateException($"The latest release '{tag}' isn't a Quotalis version tag.");
        }

        var assets = (release["assets"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var expected = $"Quotalis-Setup-{tag[1..]}.exe";
        var installer = assets.FirstOrDefault(a => Str(a["name"]) == expected)
            ?? throw new UpdateException($"Release {tag} has no {expected}.");

        string? sha = null;
        if (Str(installer["digest"]) is { } digest && digest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            var m = Digest.Match(digest);
            sha = m.Success ? m.Groups[1].Value.ToLowerInvariant() : throw new UpdateException($"Release {tag} has a malformed SHA-256 digest.");
        }

        var sumsName = $"SHA256SUMS-{tag[1..]}.txt";
        var sums = assets.FirstOrDefault(a => Str(a["name"]) == sumsName);
        var name = Str(release["name"]) is { Length: > 0 } title ? title : $"Quotalis {tag[1..]}";
        return new QuotalisRelease(tag, version, name, expected, sha, sums is null ? null : sumsName,
            DownloadUrl(installer, tag, expected), sums is null ? null : DownloadUrl(sums, tag, sumsName));
    }

    /// <summary>The digest of <paramref name="file"/> in a <c>sha256sum</c>-style list ("hex  name" per line).</summary>
    public static string? FindInSums(string sums, string file)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == file && Regex.IsMatch(parts[0], "^[0-9a-fA-F]{64}$"))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    public static void VerifySha256(string file, string expected)
    {
        string actual;
        using (var stream = File.OpenRead(file))
        {
            actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException($"The download doesn't match its published SHA-256 (got {actual[..12]}…, expected {expected[..12]}…). It was not installed.");
        }
    }

    /// <summary>The release GitHub marks as latest (never a draft or pre-release).</summary>
    public async Task<QuotalisRelease> GetLatestAsync()
    {
        HttpResponseMessage response;
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            response = await _http.SendAsync(request, cancel.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new UpdateException("Couldn't reach GitHub to check for updates. Check the connection and try again.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return ParseRelease(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }

            if (IsRateLimited(response))
            {
                throw new UpdateException("GitHub's limit for checks without an account is used up for now. Try again in an hour.");
            }

            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                throw new UpdateException($"Couldn't check for updates: GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
        }

        // 404: no release yet, or the repository is private. The signed-in GitHub CLI can tell the two apart.
        var result = await _gh(["api", $"repos/{Repo}/releases/latest"], TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Explain(result, "check for updates");
        }

        return ParseRelease(result.Output) with { ViaCli = true };
    }

    /// <summary>Downloads the release's installer and verifies it; returns the installer's path.</summary>
    public async Task<string> DownloadAsync(QuotalisRelease release)
    {
        if (!InstallerName.IsMatch(release.Installer) || (release.SumsAsset is { } s && !SumsName.IsMatch(s)))
        {
            throw new UpdateException($"'{release.Installer}' isn't a Quotalis installer name.");
        }

        var dir = Path.Combine(Path.GetTempPath(), $"Quotalis-update-{release.Tag}");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        if (release.ViaCli || release.InstallerUrl is null)
        {
            await DownloadWithGhAsync(release, dir).ConfigureAwait(false);
        }
        else
        {
            await DownloadFileAsync(release.InstallerUrl, Path.Combine(dir, release.Installer)).ConfigureAwait(false);
            if (release.Sha256 is null && release.SumsUrl is { } sumsUrl && release.SumsAsset is { } sumsName)
            {
                await DownloadFileAsync(sumsUrl, Path.Combine(dir, sumsName)).ConfigureAwait(false);
            }
        }

        var installer = Path.Combine(dir, release.Installer);
        if (!File.Exists(installer))
        {
            throw new UpdateException("The download finished, but the installer is missing.");
        }

        var expected = release.Sha256;
        if (expected is null && release.SumsAsset is { } sumsFile && File.Exists(Path.Combine(dir, sumsFile)))
        {
            expected = FindInSums(await File.ReadAllTextAsync(Path.Combine(dir, sumsFile)).ConfigureAwait(false), release.Installer);
        }

        if (expected is null)
        {
            throw new UpdateException($"Release {release.Tag} has no SHA-256 for its installer, so it can't be verified. It was not installed.");
        }

        VerifySha256(installer, expected);
        return installer;
    }

    private async Task DownloadFileAsync(string url, string path)
    {
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"Couldn't download the update: GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, cancel.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            throw new UpdateException("The download stopped before it finished. Check the connection and try again.");
        }
    }

    private async Task DownloadWithGhAsync(QuotalisRelease release, string dir)
    {
        var args = new List<string> { "release", "download", release.Tag, "--repo", Repo, "--dir", dir, "--clobber", "--pattern", release.Installer };
        if (release.SumsAsset is { } sums)
        {
            args.AddRange(["--pattern", sums]);
        }

        var result = await _gh(args, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Explain(result, "download the update");
        }
    }

    /// <summary>The asset's <c>browser_download_url</c>, only if it is exactly this repository's link for this tag and file.</summary>
    private static string? DownloadUrl(JsonObject asset, string tag, string file)
    {
        var url = Str(asset["browser_download_url"]);
        return url == $"{DownloadPrefix}{tag}/{file}" ? url : null;
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.StatusCode == HttpStatusCode.Forbidden
            && response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0");

    private static UpdateException Explain(ProcessResult result, string what)
    {
        var error = result.Error.Trim();
        if (result.ExitCode == -1 && error.Length == 0)
        {
            return new UpdateException($"No Quotalis release found on {Repo} yet (or the repository is private; the GitHub CLI, signed in with access, can read it).");
        }

        if (error.Contains("auth login", StringComparison.OrdinalIgnoreCase) || error.Contains("not logged", StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateException($"No public Quotalis release found on {Repo}. If the repository is private, sign the GitHub CLI in with: gh auth login");
        }

        if (error.Contains("Not Found", StringComparison.OrdinalIgnoreCase) || error.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateException($"No Quotalis release found on {Repo} yet (or this GitHub account can't see the repository).");
        }

        var first = error.Split('\n')[0];
        return new UpdateException(first.Length > 0 ? $"Couldn't {what}: {first}" : $"Couldn't {what} (gh exited with code {result.ExitCode}).");
    }

    private static HttpClient CreateHttpClient()
    {
        // No overall timeout: the check has its own 30-second limit and a download its own 10 minutes.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Quotalis", typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    private static async Task<ProcessResult> RunGhAsync(IReadOnlyList<string> args, TimeSpan timeout)
    {
        var gh = ProcessRunner.FindExecutable("gh", @"%ProgramFiles%\GitHub CLI\gh.exe", @"%LocalAppData%\Programs\GitHub CLI\gh.exe");
        if (gh is null)
        {
            return new ProcessResult(-1, string.Empty, string.Empty);
        }

        try
        {
            return await ProcessRunner.RunAsync(gh, args, timeout: timeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ProcessResult(-2, string.Empty, "GitHub CLI timed out.");
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
