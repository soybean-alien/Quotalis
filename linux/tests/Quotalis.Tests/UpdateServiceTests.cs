using System.Net;
using System.Security.Cryptography;
using System.Text;
using Quotalis.Core.Diagnostics;
using Quotalis.Core.Updates;

namespace Quotalis.Tests;

public class UpdateServiceTests
{
    private const string Download = "https://github.com/soybean-alien/Quotalis/releases/download/";

    private static string ReleaseJson(string tag, string? digest = null, bool sums = true, string? installer = null, string? installerUrl = null)
    {
        var v = tag[1..];
        var name = installer ?? $"Quotalis-Setup-{v}.exe";
        var url = installerUrl ?? $"{Download}{tag}/{name}";
        return $$"""
            {"tag_name":"{{tag}}","name":"Quotalis {{v}}","assets":[
              {"name":"{{name}}","browser_download_url":"{{url}}"{{(digest is null ? "" : $",\"digest\":\"{digest}\"")}}},
              {"name":"Quotalis-{{v}}-win-x64-portable.zip"}{{(sums ? $",{{\"name\":\"SHA256SUMS-{v}.txt\",\"browser_download_url\":\"{Download}{tag}/SHA256SUMS-{v}.txt\"}}" : "")}}]}
            """;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static HttpResponseMessage Answer(HttpStatusCode status, byte[]? body = null) =>
        new(status) { Content = new ByteArrayContent(body ?? []) };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(answer(request));
        }
    }

    private static Task<ProcessResult> NoGh(IReadOnlyList<string> args, TimeSpan timeout) =>
        throw new InvalidOperationException("The GitHub CLI must not be used here.");

    [Fact]
    public void Parses_the_installer_its_digest_and_the_checksum_file()
    {
        var digest = new string('a', 64);
        var release = UpdateService.ParseRelease(ReleaseJson("v0.6.0", "sha256:" + digest.ToUpperInvariant()));

        Assert.Equal("v0.6.0", release.Tag);
        Assert.Equal(new Version(0, 6, 0), release.Version);
        Assert.Equal("Quotalis-Setup-0.6.0.exe", release.Installer);
        Assert.Equal(digest, release.Sha256);
        Assert.Equal("SHA256SUMS-0.6.0.txt", release.SumsAsset);
        Assert.Equal(Download + "v0.6.0/Quotalis-Setup-0.6.0.exe", release.InstallerUrl);
        Assert.Equal(Download + "v0.6.0/SHA256SUMS-0.6.0.txt", release.SumsUrl);
        Assert.False(release.ViaCli);
    }

    [Theory]
    [InlineData("https://example.com/Quotalis-Setup-0.6.0.exe")]
    [InlineData("https://github.com/someone-else/Quotalis/releases/download/v0.6.0/Quotalis-Setup-0.6.0.exe")]
    [InlineData("https://github.com/soybean-alien/Quotalis/releases/download/v0.5.9/Quotalis-Setup-0.6.0.exe")]
    public void Ignores_download_links_outside_this_release(string url)
    {
        var release = UpdateService.ParseRelease(ReleaseJson("v0.6.0", installerUrl: url));

        Assert.Null(release.InstallerUrl);
    }

    [Theory]
    [InlineData("""{"tag_name":"latest","assets":[]}""")]                                     // not a version tag
    [InlineData("""{"tag_name":"v1.2.3.4","assets":[]}""")]                                   // four parts
    [InlineData("""{"tag_name":"v0.6.0","assets":[{"name":"Other-Setup-0.6.0.exe"}]}""")]     // no Quotalis installer
    [InlineData("""{"tag_name":"v0.6.0","assets":[{"name":"Quotalis-Setup-0.5.9.exe"}]}""")]   // installer of another version
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
        var sums = $"{a}  Quotalis-0.6.0-win-x64-portable.zip\r\n{b}  Quotalis-Setup-0.6.0.exe\r\n";

        Assert.Equal(b, UpdateService.FindInSums(sums, "Quotalis-Setup-0.6.0.exe"));
        Assert.Null(UpdateService.FindInSums(sums, "Quotalis-Setup-0.7.0.exe"));
    }

    [Fact]
    public async Task A_public_release_is_checked_and_downloaded_without_the_GitHub_CLI()
    {
        var installerBytes = Encoding.UTF8.GetBytes("pretend installer");
        var handler = new FakeHandler(r => r.RequestUri!.ToString() switch
        {
            "https://api.github.com/repos/soybean-alien/Quotalis/releases/latest" => Answer(HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson("v0.6.0"))),
            Download + "v0.6.0/Quotalis-Setup-0.6.0.exe" => Answer(HttpStatusCode.OK, installerBytes),
            Download + "v0.6.0/SHA256SUMS-0.6.0.txt" => Answer(HttpStatusCode.OK, Encoding.UTF8.GetBytes($"{Sha(installerBytes)}  Quotalis-Setup-0.6.0.exe\n")),
            _ => Answer(HttpStatusCode.NotFound),
        });
        var service = new UpdateService(new HttpClient(handler), NoGh);

        var release = await service.GetLatestAsync();
        var path = await service.DownloadAsync(release);

        Assert.False(release.ViaCli);
        Assert.Equal(installerBytes, File.ReadAllBytes(path));
        Assert.Equal(3, handler.Urls.Count);
    }

    [Fact]
    public async Task A_private_repository_falls_back_to_the_GitHub_CLI()
    {
        var installerBytes = Encoding.UTF8.GetBytes("pretend installer");
        var calls = new List<IReadOnlyList<string>>();
        var handler = new FakeHandler(_ => Answer(HttpStatusCode.NotFound));
        var service = new UpdateService(new HttpClient(handler), (args, _) =>
        {
            calls.Add(args);
            if (args[0] == "api")
            {
                return Task.FromResult(new ProcessResult(0, ReleaseJson("v0.6.0"), string.Empty));
            }

            var dir = args[args.ToList().IndexOf("--dir") + 1];
            File.WriteAllBytes(Path.Combine(dir, "Quotalis-Setup-0.6.0.exe"), installerBytes);
            File.WriteAllText(Path.Combine(dir, "SHA256SUMS-0.6.0.txt"), $"{Sha(installerBytes)}  Quotalis-Setup-0.6.0.exe\n");
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        });

        var release = await service.GetLatestAsync();
        var path = await service.DownloadAsync(release);

        Assert.True(release.ViaCli);
        Assert.Equal(installerBytes, File.ReadAllBytes(path));
        Assert.Single(handler.Urls);   // the anonymous check only; the private download links would fail
        Assert.Equal(["api", "repos/soybean-alien/Quotalis/releases/latest"], calls[0]);
        Assert.Equal(["release", "download", "v0.6.0", "--repo", "soybean-alien/Quotalis"], calls[1].Take(5));
    }

    [Fact]
    public async Task A_tampered_or_unverifiable_download_is_refused()
    {
        var handler = new FakeHandler(_ => Answer(HttpStatusCode.OK, Encoding.UTF8.GetBytes("tampered")));
        var service = new UpdateService(new HttpClient(handler), NoGh);

        var wrongDigest = UpdateService.ParseRelease(ReleaseJson("v0.6.0", "sha256:" + new string('b', 64), sums: false));
        var error = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(wrongDigest));
        Assert.Contains("SHA-256", error.Message);

        var noChecksum = UpdateService.ParseRelease(ReleaseJson("v0.6.0", sums: false));
        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(noChecksum));
    }

    [Fact]
    public async Task Explains_GitHubs_rate_limit()
    {
        var handler = new FakeHandler(_ =>
        {
            var answer = Answer(HttpStatusCode.Forbidden);
            answer.Headers.Add("x-ratelimit-remaining", "0");
            return answer;
        });
        var service = new UpdateService(new HttpClient(handler), NoGh);

        var error = await Assert.ThrowsAsync<UpdateException>(service.GetLatestAsync);

        Assert.Contains("Try again in an hour", error.Message);
    }

    [Theory]
    [InlineData(-1, "", "No Quotalis release")]
    [InlineData(4, "To get started with GitHub CLI, please run:  gh auth login", "gh auth login")]
    [InlineData(1, "HTTP 404: Not Found (https://api.github.com/repos/soybean-alien/Quotalis/releases/latest)", "No Quotalis release")]
    public async Task Explains_a_missing_release_in_plain_words(int exitCode, string stderr, string expected)
    {
        var handler = new FakeHandler(_ => Answer(HttpStatusCode.NotFound));
        var service = new UpdateService(new HttpClient(handler), (_, _) => Task.FromResult(new ProcessResult(exitCode, string.Empty, stderr)));

        var error = await Assert.ThrowsAsync<UpdateException>(service.GetLatestAsync);

        Assert.Contains(expected, error.Message);
    }
}
