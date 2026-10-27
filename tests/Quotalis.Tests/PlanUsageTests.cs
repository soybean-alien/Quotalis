using Quotalis.Core.Models;
using Quotalis.Core.Plan;
using Quotalis.Core.Storage;

namespace Quotalis.Tests;

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
    public void Plan_service_refresh_recovers_a_reading_the_first_load_missed()
    {
        var path = Path.Combine(TestData.TempDirectory(), "claude-plan.json");
        PlanUsage? onDisk = null;
        using var service = new PlanUsageService(path, () => onDisk);
        service.Configure(true);
        Assert.Null(service.Usage);

        PlanUsage? seen = null;
        service.Changed += u => seen = u;
        onDisk = new PlanUsage(new PlanWindow(83, Now.AddHours(1)), new PlanWindow(36, Now.AddDays(3)), Now);
        service.Refresh();

        Assert.Equal(83, service.Usage!.Current!.UsedPercent);
        Assert.Same(onDisk, seen);
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

        Assert.Equal(PlanConnection.Connected, ClaudeStatusLineSetup.Connect(path, @"C:\Apps\Quotalis.exe"));

        var text = File.ReadAllText(path);
        Assert.Contains("\"model\": \"opus\"", text);
        Assert.Contains("Bash(git:*)", text);
        Assert.Contains("--quotalis-statusline", text);
        Assert.True(File.Exists(path + ".quotalis.bak"));
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
        Assert.Contains("claude.ai", PlanStatus.Reason(PlanDataState.WaitingForData));
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
        Assert.Equal(PlanDataState.SignInExpired, PlanStatus.StateOf(PlanConnection.SignInExpired, usage, Now));
        Assert.Equal(PlanDataState.FormatChanged, PlanStatus.StateOf(PlanConnection.FormatChanged, usage, Now));
    }

    [Fact]
    public void A_sign_in_problem_wins_and_either_link_connects()
    {
        Assert.Equal(PlanConnection.Connected, ClaudePlanLink.Combine(ClaudeWebStatus.SignedIn, PlanConnection.NotConnected));
        Assert.Equal(PlanConnection.Connected, ClaudePlanLink.Combine(ClaudeWebStatus.SignedOut, PlanConnection.Connected));
        Assert.Equal(PlanConnection.NotConnected, ClaudePlanLink.Combine(ClaudeWebStatus.SignedOut, PlanConnection.OtherStatusLine));
        Assert.Equal(PlanConnection.SignInExpired, ClaudePlanLink.Combine(ClaudeWebStatus.Expired, PlanConnection.Connected));
        Assert.Equal(PlanConnection.FormatChanged, ClaudePlanLink.Combine(ClaudeWebStatus.FormatChanged, PlanConnection.Connected));
    }
}

public class PlanCalibrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 25, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_both_windows_from_the_usage_card_numbers()
    {
        var usage = PlanCalibration.Parse(
            ["--quotalis-calibrate", "--five-hour", "51", "--weekly", "67", "--five-hour-reset", "2026-10-17T15:29:59.690Z",
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

public class LegacyClaudeStopHookTests
{
    [Fact]
    public void Removes_every_old_claude_stop_hook_and_keeps_the_rest()
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        File.WriteAllText(path, """
            { "statusLine": { "type": "command", "command": "my-status.sh" },
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "other-stop.sh" } ] },
                                   { "hooks": [ { "type": "command", "command": "\"C:\\Apps\\Quotalis.exe\" --claudex-stop-hook", "timeout": 15 } ] },
                                   { "hooks": [ { "type": "command", "command": "\"D:\\dev\\Quotalis.exe\" --claudex-stop-hook" } ] } ] } }
            """);

        Assert.True(LegacyClaudeStopHook.Remove(path));

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("--claudex-stop-hook", text);
        Assert.Contains("other-stop.sh", text);
        Assert.Contains("my-status.sh", text);
        Assert.False(LegacyClaudeStopHook.Remove(path));
    }

    [Fact]
    public void A_file_without_it_is_not_written()
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        File.WriteAllText(path, """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "other-stop.sh" } ] } ] } }""");

        Assert.False(LegacyClaudeStopHook.Remove(path));
        Assert.False(LegacyClaudeStopHook.Remove(Path.Combine(TestData.TempDirectory(), "missing.json")));
        Assert.False(File.Exists(path + ".quotalis.bak"));
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

        var (reason, why) = CodexStopHook.Decide(Input(path), null, Now, @"C:\Apps\Quotalis.exe");

        Assert.Equal("due", why);
        Assert.Contains("`🔵 Usage: Codex 5h 18% · wk 41%`", reason);
        Assert.Contains("--quotalis-calibrate", reason);
        Assert.DoesNotContain("Tokenz", reason);
    }

    [Fact]
    public void Missing_or_reset_windows_show_dashes()
    {
        var reset = $$$"""{"limit_id":"codex","primary":{"used_percent":30,"window_minutes":300,"resets_at":{{{Now.AddMinutes(-1).ToUnixTimeSeconds()}}}}}""";

        Assert.Contains("5h -- · wk --", CodexStopHook.Decide(Input(Rollout(TimeSpan.FromMinutes(5), reset)), null, Now, "Quotalis.exe").Reason);
        Assert.Contains("5h -- · wk --", CodexStopHook.Decide(Input(Rollout(TimeSpan.FromMinutes(5))), null, Now, "Quotalis.exe").Reason);
    }

    [Fact]
    public void Stays_quiet_when_a_report_is_not_needed()
    {
        var path = Rollout(TimeSpan.FromMinutes(5), Limits("codex", 4, 1));

        Assert.Null(CodexStopHook.Decide(Input(path, active: true), null, Now, "Quotalis.exe").Reason);
        Assert.Null(CodexStopHook.Decide(Input(path), Now.AddSeconds(-10), Now, "Quotalis.exe").Reason);
        Assert.NotNull(CodexStopHook.Decide(Input(path), Now.AddMinutes(-1), Now, "Quotalis.exe").Reason);
        Assert.Null(CodexStopHook.Decide(Input(Rollout(TimeSpan.FromMinutes(1), Limits("codex", 4, 1))), null, Now, "Quotalis.exe").Reason);
        Assert.Null(CodexStopHook.Decide(Input(Path.Combine(TestData.TempDirectory(), "missing.jsonl")), null, Now, "Quotalis.exe").Reason);
        Assert.Null(CodexStopHook.Decide("{", null, Now, "Quotalis.exe").Reason);
    }
}

public class HookInstallerTests
{
    private const string Dev = @"C:\src\Quotalis\bin\Debug\Quotalis.exe";
    private const string Installed = @"C:\Users\me\AppData\Local\Programs\Quotalis\Quotalis.exe";

    private static string LegacyCommand(string executable) => $"\"{executable}\" {LegacyClaudeStopHook.Argument}";

    [Fact]
    public void Connect_moves_every_quotalis_entry_to_the_installed_copy_and_keeps_the_rest()
    {
        var dir = TestData.TempDirectory();
        var claude = Path.Combine(dir, "settings.json");
        var codex = Path.Combine(dir, "codex", "hooks.json");
        File.WriteAllText(claude, $$"""
            { "model": "opus", "statusLine": { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize(ClaudeStatusLineSetup.CommandFor(Dev))}} },
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "other.sh" } ] },
                                   { "hooks": [ { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize(LegacyCommand(Dev))}}, "timeout": 15 } ] } ] } }
            """);

        HookInstaller.Connect(claude, codex, Installed);

        var claudeText = File.ReadAllText(claude);
        Assert.DoesNotContain("Debug", claudeText);
        Assert.DoesNotContain("--claudex-stop-hook", claudeText); // the old Claude Stop hook is gone
        Assert.Contains("--quotalis-statusline", claudeText);
        Assert.Contains("other.sh", claudeText);
        Assert.Contains("opus", claudeText);
        Assert.Contains("--quotalis-codex-stop-hook", File.ReadAllText(codex));

        HookInstaller.Connect(claude, codex, Installed);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(codex), "--quotalis-codex-stop-hook"));
    }

    [Fact]
    public void Disconnect_removes_only_the_entries_of_that_copy()
    {
        var dir = TestData.TempDirectory();
        var claude = Path.Combine(dir, "settings.json");
        var codex = Path.Combine(dir, "hooks.json");
        HookInstaller.Connect(claude, codex, Installed);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(claude))!;
        root["hooks"] = System.Text.Json.Nodes.JsonNode.Parse( // a development build's entries next to it
            $$"""{ "Stop": [ { "hooks": [ { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize(LegacyCommand(Dev))}} } ] } ] }""");
        File.WriteAllText(claude, root.ToJsonString());

        HookInstaller.Disconnect(claude, codex, Installed);

        var claudeText = File.ReadAllText(claude);
        Assert.DoesNotContain("Programs", claudeText);
        Assert.Contains("Debug", claudeText);
        Assert.DoesNotContain("--quotalis-codex-stop-hook", File.ReadAllText(codex));
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
        Assert.DoesNotContain("--claudex-stop-hook", text);
    }

    [Fact]
    public void Repoint_moves_Claudex_entries_to_the_renamed_program_and_adds_nothing()
    {
        const string Claudex = @"C:\Users\me\AppData\Local\Programs\Claudex\Claudex.exe";
        var dir = TestData.TempDirectory();
        var claude = Path.Combine(dir, "settings.json");
        var codex = Path.Combine(dir, "hooks.json");
        var untouched = Path.Combine(dir, "other-settings.json");
        File.WriteAllText(claude, $$"""
            { "statusLine": { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize($"\"{Claudex}\" --claudex-statusline")}} } }
            """);
        File.WriteAllText(codex, $$"""
            { "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize($"\"{Claudex}\" --claudex-codex-stop-hook")}}, "statusMessage": "Claudex usage check" } ] } ] } }
            """);

        Assert.True(ClaudeStatusLineSetup.IsOurs($"\"{Claudex}\" --claudex-statusline"));
        HookInstaller.Repoint(claude, codex, Installed);
        HookInstaller.Repoint(untouched, Path.Combine(dir, "missing", "hooks.json"), Installed);

        var claudeText = File.ReadAllText(claude);
        var codexText = File.ReadAllText(codex);
        Assert.DoesNotContain("Claudex", claudeText + codexText);
        Assert.Contains("--quotalis-statusline", claudeText);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(codexText, "--quotalis-codex-stop-hook"));
        Assert.Contains("Quotalis usage check", codexText);
        Assert.False(File.Exists(untouched));   // nothing to point, nothing added
    }
}

public class LegacyNamesTests
{
    [Fact]
    public void Old_arguments_still_count()
    {
        Assert.True(Quotalis.Core.LegacyNames.HasArgument(["--claudex-calibrate", "--weekly", "5"], PlanCalibration.Argument));
        Assert.True(Quotalis.Core.LegacyNames.HasArgument(["--quotalis-calibrate"], PlanCalibration.Argument));
        Assert.False(Quotalis.Core.LegacyNames.HasArgument(["--claudex-calibrate"], CodexStopHook.Argument));
        Assert.Equal("--claudex-statusline", Quotalis.Core.LegacyNames.Old(ClaudeStatusLineSetup.Argument));
    }

    [Fact]
    public void A_calibration_under_the_old_argument_reads_the_same()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var usage = PlanCalibration.Parse(["--claudex-calibrate", "--weekly", "40", "--weekly-reset", "1800100000"], now);

        Assert.Equal(40, usage.Weekly!.UsedPercent);
    }

    [Fact]
    public void An_old_data_folder_moves_over_without_overwriting_new_files()
    {
        var dir = TestData.TempDirectory();
        var old = Path.Combine(dir, ".claudex");
        var current = Path.Combine(dir, ".quotalis");
        Directory.CreateDirectory(Path.Combine(old, "claude-signin"));
        File.WriteAllText(Path.Combine(old, "claude-signin", "Cookies"), "old profile");
        File.WriteAllText(Path.Combine(old, "nous-signin.bin"), "old token");
        File.WriteAllText(Path.Combine(old, "claude-plan.json"), "old reading");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "claude-plan.json"), "new reading"); // a hook ran first

        Assert.False(Quotalis.Core.LegacyNames.MoveFolder(old, current));   // the clash stays behind, nothing is deleted

        Assert.Equal(["claude-plan.json"], Directory.EnumerateFileSystemEntries(old).Select(Path.GetFileName));
        Assert.Equal("new reading", File.ReadAllText(Path.Combine(current, "claude-plan.json")));
        Assert.Equal("old token", File.ReadAllText(Path.Combine(current, "nous-signin.bin")));
        Assert.Equal("old profile", File.ReadAllText(Path.Combine(current, "claude-signin", "Cookies")));
    }

    [Fact]
    public void An_old_data_folder_is_renamed_when_nothing_new_exists()
    {
        var dir = TestData.TempDirectory();
        var old = Path.Combine(dir, "Claudex");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "usage.db"), "history");

        Assert.True(Quotalis.Core.LegacyNames.MoveFolder(old, Path.Combine(dir, "Quotalis")));
        Assert.True(Quotalis.Core.LegacyNames.MoveFolder(old, Path.Combine(dir, "Quotalis")));   // nothing left: fine

        Assert.Equal("history", File.ReadAllText(Path.Combine(dir, "Quotalis", "usage.db")));
    }

    [Fact]
    public void The_installed_copy_sets_a_development_folder_aside_and_takes_the_real_data()
    {
        var dir = TestData.TempDirectory();
        var old = Path.Combine(dir, "Claudex");
        var current = Path.Combine(dir, "Quotalis");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "usage.db"), "history");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "usage.db"), "empty, from a dev build");
        var now = new DateTimeOffset(2026, 10, 26, 20, 15, 0, TimeSpan.Zero);

        Assert.True(Quotalis.Core.LegacyNames.AdoptFolder(old, current, now));

        Assert.Equal("history", File.ReadAllText(Path.Combine(current, "usage.db")));
        Assert.Equal("empty, from a dev build", File.ReadAllText(Path.Combine(dir, "Quotalis.before-claudex-20261026-201500", "usage.db")));
        Assert.True(File.Exists(Path.Combine(current, Quotalis.Core.LegacyNames.MovedMarker)));

        // A leftover after a marked move only merges; the folder is not set aside again.
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "late.json"), "late");
        Assert.True(Quotalis.Core.LegacyNames.AdoptFolder(old, current, now.AddMinutes(1)));
        Assert.Equal("history", File.ReadAllText(Path.Combine(current, "usage.db")));
        Assert.Equal("late", File.ReadAllText(Path.Combine(current, "late.json")));
    }
}
