using Claudex.Core.Models;
using Claudex.Core.Plan;
using Claudex.Core.Storage;

namespace Claudex.Tests;

public class PlanUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private const string StatusLine = """
        {"session_id":"abc","cwd":"/home/me/secret-project","model":{"id":"claude-opus-5-5"},"cost":{"total_cost_usd":1.23},
         "rate_limits":{"five_hour":{"used_percentage":72.4,"resets_at":1790000000},"seven_day":{"used_percentage":41,"resets_at":1790500000}}}
        """;

    [Fact]
    public void Reads_both_windows_from_the_documented_status_line_input()
    {
        var usage = ClaudeStatusLine.Extract(StatusLine, Now)!;

        Assert.Equal(72.4, usage.Current!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), usage.Current.ResetsAt);
        Assert.Equal(41, usage.Weekly!.UsedPercent);
        Assert.Equal(Now, usage.MeasuredAt);
    }

    [Theory]
    [InlineData("""{"model":{"id":"x"}}""")]                                   // API users: no rate_limits
    [InlineData("""{"rate_limits":{}}""")]                                     // before the first response
    [InlineData("""{"rate_limits":{"five_hour":{"used_percentage":"high"}}}""")] // malformed
    [InlineData("not json")]
    [InlineData("")]
    public void Missing_or_malformed_plan_data_yields_nothing(string json)
    {
        Assert.Null(ClaudeStatusLine.Extract(json, Now));
    }

    [Fact]
    public void Only_one_window_is_fine_and_percentages_are_clamped()
    {
        var usage = ClaudeStatusLine.Extract("""{"rate_limits":{"seven_day":{"used_percentage":130,"resets_at":1790500000}}}""", Now)!;

        Assert.Null(usage.Current);
        Assert.Equal(100, usage.Weekly!.UsedPercent);
    }

    [Fact]
    public void A_window_that_has_reset_no_longer_reports_its_old_percentage()
    {
        var usage = new PlanUsage(new PlanWindow(72, Now.AddHours(1)), new PlanWindow(41, Now.AddDays(3)), Now);

        Assert.NotNull(usage.CurrentAt(Now.AddMinutes(30)));
        Assert.Null(usage.CurrentAt(Now.AddHours(2)));
        Assert.NotNull(usage.WeeklyAt(Now.AddHours(2)));
    }

    [Fact]
    public void Store_keeps_numbers_only()
    {
        var path = Path.Combine(TestData.TempDirectory(), "claude-plan.json");
        var store = new PlanUsageStore(path);
        store.Save(ClaudeStatusLine.Extract(StatusLine, Now)!);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("opus", text);
        Assert.Equal(72.4, store.Load()!.Current!.UsedPercent);
    }

    [Fact]
    public void Plan_service_reports_unavailable_until_data_arrives_and_follows_the_file()
    {
        var path = Path.Combine(TestData.TempDirectory(), "claude-plan.json");
        using var service = new PlanUsageService(new PlanUsageStore(path));
        var arrived = new TaskCompletionSource<PlanUsage?>();
        service.Configure(true);
        Assert.Null(service.Usage);

        service.Changed += u => { if (u is not null) arrived.TrySetResult(u); };
        new PlanUsageStore(path).Save(new PlanUsage(new PlanWindow(10, Now.AddHours(1)), null, Now));

        Assert.Equal(10, arrived.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()!.Current!.UsedPercent);
        service.Configure(false);
        Assert.Null(service.Usage);
    }

    [Fact]
    public void Plan_peaks_keep_the_daily_maximum_and_are_never_invented()
    {
        using var db = UsageDatabase.OpenInMemory();
        var day = new DateOnly(2026, 10, 1);
        db.RecordPlanPeak(day, AiProviderKind.Claude, "five_hour", 40, Now);
        db.RecordPlanPeak(day, AiProviderKind.Claude, "five_hour", 72, Now);
        db.RecordPlanPeak(day, AiProviderKind.Claude, "five_hour", 12, Now);

        var peaks = db.GetPlanPeaks(AiProviderKind.Claude, day.AddDays(-7), day);
        Assert.Equal(72, peaks[day]["five_hour"]);
        Assert.False(peaks.ContainsKey(day.AddDays(-1)));
        Assert.Empty(db.GetPlanPeaks(AiProviderKind.Codex, day.AddDays(-7), day));
    }
}

public class ClaudeStatusLineSetupTests
{
    private static string Settings(string json)
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Connect_adds_only_the_status_line_and_keeps_every_other_setting()
    {
        var path = Settings("""{ "model": "opus", "permissions": { "allow": ["Bash(git:*)"] } }""");

        Assert.Equal(PlanConnection.Connected, ClaudeStatusLineSetup.Connect(path, @"C:\Apps\Claudex.exe"));

        var text = File.ReadAllText(path);
        Assert.Contains("\"model\": \"opus\"", text);
        Assert.Contains("Bash(git:*)", text);
        Assert.Contains("--claudex-statusline", text);
        Assert.True(File.Exists(path + ".claudex.bak"));
        Assert.Equal(PlanConnection.Connected, ClaudeStatusLineSetup.Inspect(path));
    }

    [Fact]
    public void An_existing_status_line_is_never_replaced()
    {
        var path = Settings("""{ "statusLine": { "type": "command", "command": "my-status.sh" } }""");

        Assert.Equal(PlanConnection.OtherStatusLine, ClaudeStatusLineSetup.Connect(path, @"C:\DI.exe"));
        Assert.Contains("my-status.sh", File.ReadAllText(path));
        Assert.Equal(PlanConnection.OtherStatusLine, ClaudeStatusLineSetup.Disconnect(path));
        Assert.Contains("my-status.sh", File.ReadAllText(path));
    }

    [Fact]
    public void Developer_Islands_status_line_is_not_ours()
    {
        var path = Settings("""{ "statusLine": { "type": "command", "command": "\"C:/Apps/DeveloperIsland.exe\" --claude-statusline" } }""");

        Assert.Equal(PlanConnection.OtherStatusLine, ClaudeStatusLineSetup.Inspect(path));
        Assert.Equal(PlanConnection.OtherStatusLine, ClaudeStatusLineSetup.Disconnect(path));
        Assert.Contains("DeveloperIsland.exe", File.ReadAllText(path));
    }

    [Fact]
    public void Disconnect_removes_only_our_entry()
    {
        var path = Settings("""{ "theme": "dark" }""");
        ClaudeStatusLineSetup.Connect(path, @"C:\DI.exe");

        Assert.Equal(PlanConnection.NotConnected, ClaudeStatusLineSetup.Disconnect(path));
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("statusLine", text);
        Assert.Contains("dark", text);
    }

    [Fact]
    public void Missing_settings_are_created_and_unreadable_ones_left_alone()
    {
        var dir = TestData.TempDirectory();
        var missing = Path.Combine(dir, "new", "settings.json");
        Assert.Equal(PlanConnection.Connected, ClaudeStatusLineSetup.Connect(missing, @"C:\DI.exe"));

        var broken = Settings("{ this is not json");
        Assert.Equal(PlanConnection.Unreadable, ClaudeStatusLineSetup.Connect(broken, @"C:\DI.exe"));
        Assert.Equal("{ this is not json", File.ReadAllText(broken));
    }
}

public class PlanStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static PlanUsage Measured(TimeSpan ago, TimeSpan resetsIn) =>
        new(new PlanWindow(40, Now + resetsIn), new PlanWindow(20, Now.AddDays(3)), Now - ago);

    [Fact]
    public void Connected_without_data_is_not_reported_as_available()
    {
        Assert.Equal(PlanDataState.WaitingForData, PlanStatus.StateOf(PlanConnection.Connected, null, Now));
        Assert.Contains("terminal", PlanStatus.Reason(PlanDataState.WaitingForData));
    }

    [Fact]
    public void Recent_data_is_receiving_and_old_data_is_stale()
    {
        Assert.Equal(PlanDataState.Receiving, PlanStatus.StateOf(PlanConnection.Connected, Measured(TimeSpan.FromMinutes(5), TimeSpan.FromHours(2)), Now));
        Assert.Equal(PlanDataState.Stale, PlanStatus.StateOf(PlanConnection.Connected, Measured(TimeSpan.FromHours(3), TimeSpan.FromHours(2)), Now));
    }

    [Fact]
    public void Connection_problems_win_over_data()
    {
        var usage = Measured(TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        Assert.Equal(PlanDataState.NotConnected, PlanStatus.StateOf(PlanConnection.NotConnected, usage, Now));
        Assert.Equal(PlanDataState.OtherStatusLine, PlanStatus.StateOf(PlanConnection.OtherStatusLine, null, Now));
        Assert.Equal(PlanDataState.Unreadable, PlanStatus.StateOf(PlanConnection.Unreadable, null, Now));
    }
}

public class PlanCalibrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 25, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_both_windows_from_the_usage_card_numbers()
    {
        var usage = PlanCalibration.Parse(
            ["--claudex-calibrate", "--five-hour", "51", "--weekly", "67", "--five-hour-reset", "2026-10-17T15:29:59.690Z",
             "--weekly-reset", "1792648799", "--plan", "Pro", "--by", "claude-auto", "--source", "desktop usage card (auto hook)"], Now);

        Assert.Equal(51, usage.Current!.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 15, 29, 59, 690, TimeSpan.Zero), usage.Current.ResetsAt);
        Assert.Equal(67, usage.Weekly!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792648799), usage.Weekly.ResetsAt);
        Assert.False(usage.Current.IsEstimated);
        Assert.Equal(Now, usage.MeasuredAt);
    }

    [Fact]
    public void A_missing_window_is_left_out_and_percentages_are_clamped()
    {
        var usage = PlanCalibration.Parse(["--five-hour", "120", "--five-hour-reset", "2026-10-17T15:30:00Z", "--weekly", "--", "--weekly-reset", "--"], Now);

        Assert.Equal(100, usage.Current!.UsedPercent);
        Assert.Null(usage.Weekly);
    }

    [Theory]
    [InlineData("--plan", "Pro")]                                      // no window at all
    [InlineData("--five-hour", "51")]                                  // no reset time
    [InlineData("--five-hour", "lots", "--five-hour-reset", "1792648799")] // not a number
    [InlineData("--weekly", "67", "--weekly-reset", "soon")]           // unreadable reset
    public void Incomplete_readings_are_refused(params string[] args)
    {
        Assert.Throws<FormatException>(() => PlanCalibration.Parse(args, Now));
    }
}

public class ClaudeStopHookTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 25, 0, TimeSpan.Zero);

    private static string Transcript(TimeSpan age, bool desktop)
    {
        var path = Path.Combine(TestData.TempDirectory(), "session.jsonl");
        File.WriteAllLines(path,
        [
            $$"""{"type":"user","timestamp":"{{(Now - age).ToString("o")}}"{{(desktop ? ",\"entrypoint\":\"claude-desktop\"" : "")}}}""",
            """{"type":"assistant","message":"hi"}""",
        ]);
        return path;
    }

    private static string Input(string transcript, bool active = false) =>
        $$"""{"session_id":"s1","transcript_path":{{System.Text.Json.JsonSerializer.Serialize(transcript)}},"stop_hook_active":{{(active ? "true" : "false")}}}""";

    [Fact]
    public void Asks_for_a_reading_in_a_desktop_session_over_two_minutes_old()
    {
        var (reason, why) = ClaudeStopHook.Decide(Input(Transcript(TimeSpan.FromMinutes(5), desktop: true)), null, Now, @"C:\Apps\Claudex.exe");

        Assert.Equal("due", why);
        Assert.Contains("\"C:\\Apps\\Claudex.exe\" --claudex-calibrate", reason);
        Assert.Contains("applied by Claudex", reason);
        Assert.Contains("🟠 Usage: Claude 5h", reason);
        Assert.DoesNotContain("Tokenz", reason);
        Assert.Contains("\"decision\":\"block\"", ClaudeStopHook.Output(reason!));
    }

    [Fact]
    public void Stays_quiet_when_a_reading_is_not_needed_or_not_possible()
    {
        var desktop = Transcript(TimeSpan.FromMinutes(5), desktop: true);
        var recent = new PlanUsage(new PlanWindow(51, Now.AddHours(3)), null, Now.AddSeconds(-10));

        Assert.Null(ClaudeStopHook.Decide(Input(desktop, active: true), null, Now, "Claudex.exe").Reason);
        Assert.Null(ClaudeStopHook.Decide(Input(desktop), recent, Now, "Claudex.exe").Reason);
        Assert.Null(ClaudeStopHook.Decide(Input(Transcript(TimeSpan.FromMinutes(1), desktop: true)), null, Now, "Claudex.exe").Reason);
        Assert.Null(ClaudeStopHook.Decide(Input(Transcript(TimeSpan.FromMinutes(5), desktop: false)), null, Now, "Claudex.exe").Reason);
        Assert.Null(ClaudeStopHook.Decide(Input(Path.Combine(TestData.TempDirectory(), "missing.jsonl")), null, Now, "Claudex.exe").Reason);
        Assert.Null(ClaudeStopHook.Decide("not json", null, Now, "Claudex.exe").Reason);
    }

    [Fact]
    public void Setup_adds_the_hook_next_to_others_and_removes_only_ours()
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        File.WriteAllText(path, """
            { "statusLine": { "type": "command", "command": "my-status.sh" },
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "other-stop.sh" } ] } ] } }
            """);

        Assert.Equal(PlanConnection.Connected, ClaudePlanLink.Connect(path, @"C:\Apps\Claudex.exe"));
        var text = File.ReadAllText(path);
        Assert.Contains("--claudex-stop-hook", text);
        Assert.Contains("other-stop.sh", text);
        Assert.Contains("my-status.sh", text);

        ClaudePlanLink.Connect(path, @"C:\Apps\Claudex.exe");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), "--claudex-stop-hook"));

        Assert.Equal(PlanConnection.OtherStatusLine, ClaudePlanLink.Disconnect(path));
        text = File.ReadAllText(path);
        Assert.DoesNotContain("--claudex-stop-hook", text);
        Assert.Contains("other-stop.sh", text);
        Assert.Contains("my-status.sh", text);
    }
}

public class CodexStopHookTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 25, 0, TimeSpan.Zero);

    private static string Rollout(TimeSpan age, params string[] limits)
    {
        var path = Path.Combine(TestData.TempDirectory(), "rollout.jsonl");
        var lines = new List<string> { $$$"""{"timestamp":"{{{(Now - age).ToString("o")}}}","type":"session_meta","payload":{}}""" };
        lines.AddRange(limits.Select((l, i) => $$$"""{"timestamp":"{{{(Now - age + TimeSpan.FromSeconds(i + 1)).ToString("o")}}}","type":"event_msg","payload":{"type":"token_count","rate_limits":{{{l}}}}}"""));
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Input(string transcript, bool active = false) =>
        $$$"""{"session_id":"s1","transcript_path":{{{System.Text.Json.JsonSerializer.Serialize(transcript)}}},"stop_hook_active":{{{(active ? "true" : "false")}}}}""";

    private static string Limits(string id, double five, double week) =>
        $$$"""{"limit_id":"{{{id}}}","primary":{"used_percent":{{{five}}},"window_minutes":300,"resets_at":{{{Now.AddHours(2).ToUnixTimeSeconds()}}}},"secondary":{"used_percent":{{{week}}},"window_minutes":10080,"resets_at":{{{Now.AddDays(3).ToUnixTimeSeconds()}}}},"plan_type":"plus"}""";

    [Fact]
    public void Reports_the_latest_codex_snapshot_with_the_numbers_filled_in()
    {
        var path = Rollout(TimeSpan.FromMinutes(5), Limits("codex", 4, 1), Limits("codex", 18.4, 40.6), Limits("codex_other", 90, 90));

        var (reason, why) = CodexStopHook.Decide(Input(path), null, Now, @"C:\Apps\Claudex.exe");

        Assert.Equal("due", why);
        Assert.Contains("`🔵 Usage: Codex 5h 18% · wk 41%`", reason);
        Assert.Contains("--claudex-calibrate", reason);
        Assert.DoesNotContain("Tokenz", reason);
    }

    [Fact]
    public void Missing_or_reset_windows_show_dashes()
    {
        var reset = $$$"""{"limit_id":"codex","primary":{"used_percent":30,"window_minutes":300,"resets_at":{{{Now.AddMinutes(-1).ToUnixTimeSeconds()}}}}}""";

        Assert.Contains("5h -- · wk --", CodexStopHook.Decide(Input(Rollout(TimeSpan.FromMinutes(5), reset)), null, Now, "Claudex.exe").Reason);
        Assert.Contains("5h -- · wk --", CodexStopHook.Decide(Input(Rollout(TimeSpan.FromMinutes(5))), null, Now, "Claudex.exe").Reason);
    }

    [Fact]
    public void Stays_quiet_when_a_report_is_not_needed()
    {
        var path = Rollout(TimeSpan.FromMinutes(5), Limits("codex", 4, 1));

        Assert.Null(CodexStopHook.Decide(Input(path, active: true), null, Now, "Claudex.exe").Reason);
        Assert.Null(CodexStopHook.Decide(Input(path), Now.AddSeconds(-10), Now, "Claudex.exe").Reason);
        Assert.NotNull(CodexStopHook.Decide(Input(path), Now.AddMinutes(-1), Now, "Claudex.exe").Reason);
        Assert.Null(CodexStopHook.Decide(Input(Rollout(TimeSpan.FromMinutes(1), Limits("codex", 4, 1))), null, Now, "Claudex.exe").Reason);
        Assert.Null(CodexStopHook.Decide(Input(Path.Combine(TestData.TempDirectory(), "missing.jsonl")), null, Now, "Claudex.exe").Reason);
        Assert.Null(CodexStopHook.Decide("{", null, Now, "Claudex.exe").Reason);
    }
}

public class HookInstallerTests
{
    private const string Dev = @"C:\src\Claudex\bin\Debug\Claudex.exe";
    private const string Installed = @"C:\Users\me\AppData\Local\Programs\Claudex\Claudex.exe";

    [Fact]
    public void Connect_moves_every_claudex_entry_to_the_installed_copy_and_keeps_the_rest()
    {
        var dir = TestData.TempDirectory();
        var claude = Path.Combine(dir, "settings.json");
        var codex = Path.Combine(dir, "codex", "hooks.json");
        File.WriteAllText(claude, $$"""
            { "model": "opus", "statusLine": { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize(ClaudeStatusLineSetup.CommandFor(Dev))}} },
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "other.sh" } ] },
                                   { "hooks": [ { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize(ClaudeStopHookSetup.CommandFor(Dev))}}, "timeout": 15 } ] } ] } }
            """);

        HookInstaller.Connect(claude, codex, Installed);

        var claudeText = File.ReadAllText(claude);
        Assert.DoesNotContain("Debug", claudeText);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(claudeText, "--claudex-stop-hook"));
        Assert.Contains("--claudex-statusline", claudeText);
        Assert.Contains("other.sh", claudeText);
        Assert.Contains("opus", claudeText);
        Assert.Contains("--claudex-codex-stop-hook", File.ReadAllText(codex));

        HookInstaller.Connect(claude, codex, Installed);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(codex), "--claudex-codex-stop-hook"));
    }

    [Fact]
    public void Disconnect_removes_only_the_entries_of_that_copy()
    {
        var dir = TestData.TempDirectory();
        var claude = Path.Combine(dir, "settings.json");
        var codex = Path.Combine(dir, "hooks.json");
        HookInstaller.Connect(claude, codex, Installed);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(claude))!;
        ((System.Text.Json.Nodes.JsonArray)root["hooks"]!["Stop"]!).Add(System.Text.Json.Nodes.JsonNode.Parse(
            $$"""{ "hooks": [ { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize(ClaudeStopHookSetup.CommandFor(Dev))}} } ] }""")); // a development build's hook next to it
        File.WriteAllText(claude, root.ToJsonString());

        HookInstaller.Disconnect(claude, codex, Installed);

        var claudeText = File.ReadAllText(claude);
        Assert.DoesNotContain("Programs", claudeText);
        Assert.Contains("Debug", claudeText);
        Assert.DoesNotContain("--claudex-codex-stop-hook", File.ReadAllText(codex));
    }

    [Fact]
    public void A_status_line_of_someone_else_is_kept()
    {
        var dir = TestData.TempDirectory();
        var claude = Path.Combine(dir, "settings.json");
        File.WriteAllText(claude, """{ "statusLine": { "type": "command", "command": "tokenz statusline" } }""");

        HookInstaller.Connect(claude, Path.Combine(dir, "hooks.json"), Installed);

        var text = File.ReadAllText(claude);
        Assert.Contains("tokenz statusline", text);
        Assert.Contains("--claudex-stop-hook", text);
    }
}
