using Quotalis.Core.Storage;

namespace Quotalis.Tests;

public sealed class SharedFileAdoptionTests
{
    [Fact]
    public void Newest_copy_wins_and_sources_are_left_alone()
    {
        var root = TestData.TempDirectory();
        var target = Path.Combine(root, "shared");
        var old = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
        var package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
        File.WriteAllText(Path.Combine(old, "claude-plan.json"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(old, "claude-plan.json"), DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(Path.Combine(package, "claude-plan.json"), "packaged");

        var adopted = SharedFileAdoption.Adopt(target, [old, package], ["claude-plan.json", "pricing.json"]);

        Assert.Equal(["claude-plan.json"], adopted);
        Assert.Equal("packaged", File.ReadAllText(Path.Combine(target, "claude-plan.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(old, "claude-plan.json")));
        Assert.True(File.Exists(Path.Combine(package, "claude-plan.json")));
        Assert.False(File.Exists(Path.Combine(target, "pricing.json")));

        // Running again changes nothing.
        Assert.Empty(SharedFileAdoption.Adopt(target, [old, package], ["claude-plan.json"]));
    }

    [Fact]
    public void A_newer_shared_file_is_not_overwritten()
    {
        var root = TestData.TempDirectory();
        var target = Directory.CreateDirectory(Path.Combine(root, "shared")).FullName;
        var old = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
        File.WriteAllText(Path.Combine(old, "pricing.json"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(old, "pricing.json"), DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(Path.Combine(target, "pricing.json"), "current");

        Assert.Empty(SharedFileAdoption.Adopt(target, [old], ["pricing.json"]));
        Assert.Equal("current", File.ReadAllText(Path.Combine(target, "pricing.json")));
    }

    [Fact]
    public void Logs_are_merged_by_time_without_duplicates()
    {
        var root = TestData.TempDirectory();
        var target = Directory.CreateDirectory(Path.Combine(root, "shared")).FullName;
        var package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
        const string a = """{"at":"2026-10-18T10:00:00+00:00","n":1}""";
        const string b = """{"at":"2026-10-18T11:00:00+00:00","n":2}""";
        const string c = """{"at":"2026-10-18T12:00:00+00:00","n":3}""";
        File.WriteAllLines(Path.Combine(target, "calibration-log.jsonl"), [c]);
        File.WriteAllLines(Path.Combine(package, "calibration-log.jsonl"), [a, b, c]);

        Assert.Equal(["calibration-log.jsonl"], SharedFileAdoption.Adopt(target, [package], ["calibration-log.jsonl"]));
        Assert.Equal([a, b, c], File.ReadAllLines(Path.Combine(target, "calibration-log.jsonl")));
        Assert.Empty(SharedFileAdoption.Adopt(target, [package], ["calibration-log.jsonl"]));
    }
}
