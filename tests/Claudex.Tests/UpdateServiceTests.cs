using System.Security.Cryptography;
using System.Text;
using Claudex.Core.Diagnostics;
using Claudex.Core.Updates;

namespace Claudex.Tests;

public class UpdateServiceTests
{
    private static string ReleaseJson(string tag, string? digest = null, bool sums = true, string? installer = null) => $$"""
        {"tag_name":"{{tag}}","name":"Claudex {{tag[1..]}}","assets":[
          {"name":"{{installer ?? $"Claudex-Setup-{tag[1..]}.exe"}}"{{(digest is null ? "" : $",\"digest\":\"{digest}\"")}}},
          {"name":"Claudex-{{tag[1..]}}-win-x64-portable.zip"}{{(sums ? $",{{\"name\":\"SHA256SUMS-{tag[1..]}.txt\"}}" : "")}}]}
        """;

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public void Parses_the_installer_its_digest_and_the_checksum_file()
    {
        var digest = new string('a', 64);
        var release = UpdateService.ParseRelease(ReleaseJson("v0.6.0", "sha256:" + digest.ToUpperInvariant()));

        Assert.Equal("v0.6.0", release.Tag);
        Assert.Equal(new Version(0, 6, 0), release.Version);
        Assert.Equal("Claudex-Setup-0.6.0.exe", release.Installer);
        Assert.Equal(digest, release.Sha256);
        Assert.Equal("SHA256SUMS-0.6.0.txt", release.SumsAsset);
    }

    [Theory]
    [InlineData("""{"tag_name":"latest","assets":[]}""")]                                     // not a version tag
    [InlineData("""{"tag_name":"v1.2.3.4","assets":[]}""")]                                   // four parts
    [InlineData("""{"tag_name":"v0.6.0","assets":[{"name":"Other-Setup-0.6.0.exe"}]}""")]     // no Claudex installer
    [InlineData("""{"tag_name":"v0.6.0","assets":[{"name":"Claudex-Setup-0.5.9.exe"}]}""")]   // installer of another version
    [InlineData("not json")]
    public void Refuses_releases_it_cannot_trust(string json)
    {
        Assert.Throws<UpdateException>(() => UpdateService.ParseRelease(json));
    }

    [Fact]
    public void Versions_compare_without_build_metadata()
    {
        var current = UpdateService.ParseVersion("0.5.3+4871515abcdef");
        Assert.Equal(new Version(0, 5, 3), current);
        Assert.True(UpdateService.IsNewer(UpdateService.ParseRelease(ReleaseJson("v0.5.4")), current));
        Assert.False(UpdateService.IsNewer(UpdateService.ParseRelease(ReleaseJson("v0.5.3")), current));
        Assert.False(UpdateService.IsNewer(UpdateService.ParseRelease(ReleaseJson("v0.4.9")), current));
    }

    [Fact]
    public void Finds_a_file_in_a_checksum_list()
    {
        var a = new string('1', 64);
        var b = new string('2', 64);
        var sums = $"{a}  Claudex-0.6.0-win-x64-portable.zip\r\n{b}  Claudex-Setup-0.6.0.exe\r\n";

        Assert.Equal(b, UpdateService.FindInSums(sums, "Claudex-Setup-0.6.0.exe"));
        Assert.Null(UpdateService.FindInSums(sums, "Claudex-Setup-0.7.0.exe"));
    }

    [Fact]
    public async Task Downloads_through_gh_and_verifies_against_the_checksum_file()
    {
        var installerBytes = Encoding.UTF8.GetBytes("pretend installer");
        var calls = new List<IReadOnlyList<string>>();
        var service = new UpdateService((args, _) =>
        {
            calls.Add(args);
            var dir = args[args.ToList().IndexOf("--dir") + 1];
            File.WriteAllBytes(Path.Combine(dir, "Claudex-Setup-0.6.0.exe"), installerBytes);
            File.WriteAllText(Path.Combine(dir, "SHA256SUMS-0.6.0.txt"), $"{Sha(installerBytes)}  Claudex-Setup-0.6.0.exe\n");
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        });

        var path = await service.DownloadAsync(UpdateService.ParseRelease(ReleaseJson("v0.6.0")));

        Assert.Equal(installerBytes, File.ReadAllBytes(path));
        var call = Assert.Single(calls);
        Assert.Equal(["release", "download", "v0.6.0", "--repo", "soybean-alien/Claudex"], call.Take(5));
    }

    [Fact]
    public async Task A_tampered_or_unverifiable_download_is_refused()
    {
        var service = new UpdateService((args, _) =>
        {
            var dir = args[args.ToList().IndexOf("--dir") + 1];
            File.WriteAllText(Path.Combine(dir, "Claudex-Setup-0.6.0.exe"), "tampered");
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        });

        var wrongDigest = UpdateService.ParseRelease(ReleaseJson("v0.6.0", "sha256:" + new string('b', 64), sums: false));
        var error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(wrongDigest));
        Assert.Contains("SHA-256", error.Message);

        var noChecksum = UpdateService.ParseRelease(ReleaseJson("v0.6.0", sums: false));
        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(noChecksum));
    }

    [Theory]
    [InlineData(-1, "", "isn't installed")]
    [InlineData(4, "To get started with GitHub CLI, please run:  gh auth login", "isn't signed in")]
    [InlineData(1, "HTTP 404: Not Found (https://api.github.com/repos/soybean-alien/Claudex/releases/latest)", "No Claudex release")]
    public async Task Explains_gh_failures_in_plain_words(int exitCode, string stderr, string expected)
    {
        var service = new UpdateService((_, _) => Task.FromResult(new ProcessResult(exitCode, string.Empty, stderr)));

        var error = await Assert.ThrowsAsync<UpdateException>(service.GetLatestAsync);

        Assert.Contains(expected, error.Message);
    }
}
