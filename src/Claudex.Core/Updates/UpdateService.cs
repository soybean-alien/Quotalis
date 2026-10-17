using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Claudex.Core.Diagnostics;

namespace Claudex.Core.Updates;

/// <summary>A Claudex release on GitHub: its tag, version, installer asset and checksum sources.</summary>
/// <param name="Sha256">GitHub's own digest of the installer asset (lowercase hex), when the release has one.</param>
/// <param name="SumsAsset">The release's <c>SHA256SUMS-X.Y.Z.txt</c>, when attached.</param>
public sealed record ClaudexRelease(string Tag, Version Version, string Name, string Installer, string? Sha256, string? SumsAsset);

public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Self-update from the private <c>soybean-alien/Claudex</c> repository. Claudex is never published, so a private
/// repository cannot be read anonymously: every request goes through the GitHub CLI (<c>gh</c>), which uses the
/// account it is already signed in with. Claudex itself never sees or stores a token.
/// Check: <c>gh api repos/…/releases/latest</c>, compared with this build's version. Download: <c>gh release download</c>
/// of <c>Claudex-Setup-X.Y.Z.exe</c> into a temporary folder, then its SHA-256 must match GitHub's asset digest or the
/// release's <c>SHA256SUMS-X.Y.Z.txt</c>; without either, the update is refused.
/// </summary>
public sealed class UpdateService
{
    public const string Repo = "soybean-alien/Claudex";

    private static readonly Regex InstallerName = new(@"^Claudex-Setup-\d+\.\d+\.\d+\.exe$", RegexOptions.CultureInvariant);
    private static readonly Regex SumsName = new(@"^SHA256SUMS-\d+\.\d+\.\d+\.txt$", RegexOptions.CultureInvariant);
    private static readonly Regex Digest = new(@"^sha256:([0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);

    private readonly Func<IReadOnlyList<string>, TimeSpan, Task<ProcessResult>> _gh;

    /// <param name="gh">Runs gh with the given arguments; tests pass a fake. Null uses the installed GitHub CLI.</param>
    public UpdateService(Func<IReadOnlyList<string>, TimeSpan, Task<ProcessResult>>? gh = null)
    {
        _gh = gh ?? RunGhAsync;
    }

    /// <summary>"0.5.3" from an informational version such as "0.5.3+4871515…".</summary>
    public static Version ParseVersion(string informational)
    {
        var core = informational.Split('+', '-')[0];
        return Version.TryParse(core, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);
    }

    public static bool IsNewer(ClaudexRelease release, Version current) => release.Version > current;

    /// <summary>Reads a GitHub release object (what <c>gh api repos/…/releases/latest</c> prints).</summary>
    public static ClaudexRelease ParseRelease(string json)
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
            throw new UpdateException($"The latest release '{tag}' isn't a Claudex version tag.");
        }

        var assets = (release["assets"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var expected = $"Claudex-Setup-{tag[1..]}.exe";
        var installer = assets.FirstOrDefault(a => Str(a["name"]) == expected)
            ?? throw new UpdateException($"Release {tag} has no {expected}.");

        string? sha = null;
        if (Str(installer["digest"]) is { } digest && digest.StartsWith("sha256:", StringComparison.Ordinal))
        {
            var m = Digest.Match(digest);
            sha = m.Success ? m.Groups[1].Value.ToLowerInvariant() : throw new UpdateException($"Release {tag} has a malformed SHA-256 digest.");
        }

        var sums = assets.Select(a => Str(a["name"])).FirstOrDefault(n => n == $"SHA256SUMS-{tag[1..]}.txt");
        var name = Str(release["name"]) is { Length: > 0 } title ? title : $"Claudex {tag[1..]}";
        return new ClaudexRelease(tag, version, name, expected, sha, sums);
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
    public async Task<ClaudexRelease> GetLatestAsync()
    {
        var result = await _gh(["api", $"repos/{Repo}/releases/latest"], TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Explain(result, "check for updates");
        }

        return ParseRelease(result.Output);
    }

    /// <summary>Downloads the release's installer and verifies it; returns the installer's path.</summary>
    public async Task<string> DownloadAsync(ClaudexRelease release)
    {
        if (!InstallerName.IsMatch(release.Installer) || (release.SumsAsset is { } s && !SumsName.IsMatch(s)))
        {
            throw new UpdateException($"'{release.Installer}' isn't a Claudex installer name.");
        }

        var dir = Path.Combine(Path.GetTempPath(), $"Claudex-update-{release.Tag}");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
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

    private static UpdateException Explain(ProcessResult result, string what)
    {
        var error = result.Error.Trim();
        if (result.ExitCode == -1 && error.Length == 0)
        {
            return new UpdateException("The GitHub CLI isn't installed. Install it (winget install GitHub.cli) and sign in with: gh auth login");
        }

        if (error.Contains("auth login", StringComparison.OrdinalIgnoreCase) || error.Contains("not logged", StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateException("The GitHub CLI isn't signed in. Run: gh auth login");
        }

        if (error.Contains("Not Found", StringComparison.OrdinalIgnoreCase) || error.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateException($"No Claudex release found on {Repo} yet (or this GitHub account can't see the repository).");
        }

        var first = error.Split('\n')[0];
        return new UpdateException(first.Length > 0 ? $"Couldn't {what}: {first}" : $"Couldn't {what} (gh exited with code {result.ExitCode}).");
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
