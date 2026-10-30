using System.Text.Json;
using System.Text.Json.Nodes;
using Quotalis.Core.Models;
using Quotalis.Linux;

namespace Quotalis.Tests;

public class LinuxFrontEndTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static ToolState Tool(string name, double? five = null, double? week = null, DateTimeOffset? resets = null) => new()
    {
        Name = name,
        Status = ProviderState.Ready,
        FiveHour = five is { } f ? new PlanReading(f, resets ?? Now.AddHours(2)) : null,
        Weekly = week is { } w ? new PlanReading(w, resets ?? Now.AddDays(3)) : null,
    };

    private static AppState State(ToolState? claude = null, ToolState? codex = null, DateTimeOffset? at = null) => new()
    {
        GeneratedAt = at ?? Now,
        Claude = claude ?? Tool("Claude", 58, 34),
        Codex = codex ?? Tool("Codex", 21, 49),
        Hermes = new HermesState(),
    };

    [Fact]
    public void ScreenStaysWithinItsWidth()
    {
        var state = State(claude: Tool("Claude", 42, 63) with { TodayUsd = 123.45m, TodayTokens = 1_200_000, Sessions = 12, Active = true, Note = "A long note that has to break over more than one line to stay inside" });
        foreach (var line in new Screen(color: false).Render(state, ScreenPage.Overview, Now, Screen.MaxWidth))
        {
            Assert.True(line.Length < Screen.MaxWidth, line);
        }
    }

    [Fact]
    public void DemoScreenKeepsItsGitLineWithinTheWidth()
    {
        foreach (var line in new Screen(color: false).Render(StateBuilder.Demo(Now), ScreenPage.Overview, Now, Screen.MaxWidth))
        {
            if (line.StartsWith(" GIT"))
            {
                Assert.True(line.Length < Screen.MaxWidth, line);
                Assert.Contains("…", line);
            }
        }
    }

    [Fact]
    public void ScreenShowsWhenALimitResets()
    {
        var text = string.Join('\n', new Screen(color: false).Render(State(claude: Tool("Claude", 42, 63)), ScreenPage.Overview, Now, Screen.MaxWidth));
        Assert.Contains("42%  2 h", text);
    }

    private static JsonElement Bar(AppState? state) => JsonDocument.Parse(WaybarOutput.Render(state, Now)).RootElement;

    private static string[] Classes(JsonElement bar) => bar.GetProperty("class").EnumerateArray().Select(c => c.GetString()!).ToArray();

    [Fact]
    public void BarShowsBothToolsAndNoTilde()
    {
        var bar = Bar(State());
        Assert.Equal("C ▰▰▰▱▱ 58%  X ▰▱▱▱▱ 21%  $0.00", bar.GetProperty("text").GetString());
        Assert.Equal(58, bar.GetProperty("percentage").GetInt32());
        Assert.DoesNotContain('~', bar.GetRawText());
    }

    [Fact]
    public void BarFallsBackToTheWeeklyWindow() =>
        Assert.Equal("C ▰▰▰▱▱ 58%  X ▰▱▱▱▱ 29%  $0.00", Bar(State(codex: Tool("Codex", null, 29))).GetProperty("text").GetString());

    [Theory]
    [InlineData(79, new string[0])]
    [InlineData(80, new[] { "warning" })]
    [InlineData(95, new[] { "critical" })]
    public void BarClassFollowsTheWorstWindow(double percent, string[] expected) =>
        Assert.Equal(expected, Classes(Bar(State(codex: Tool("Codex", 10, percent)))));

    [Theory]
    [InlineData(79, new string[0])]
    [InlineData(80, new[] { "active" })]
    [InlineData(95, new[] { "active" })]
    public void OmarchyBarHighlightsACloseLimit(double percent, string[] expected) =>
        Assert.Equal(expected, Classes(JsonDocument.Parse(WaybarOutput.Render(State(codex: Tool("Codex", 10, percent)), Now, omarchy: true)).RootElement));

    [Fact]
    public void OmarchyBarIgnoresAToolAtWork() =>
        Assert.Empty(Classes(JsonDocument.Parse(WaybarOutput.Render(State(claude: Tool("Claude", 5, 5) with { Active = true }), Now, omarchy: true)).RootElement));

    private const string OmarchyShell = """
        {
          "version": 1,
          "idle": { "screensaver": 150 },
          "bar": {
            "position": "top",
            "layout": {
              "left": [ { "id": "omarchy.menu" } ],
              "center": [ { "id": "omarchy.clock", "format": "dddd HH:mm", "verticalFormat": "HH\n—\nmm" } ],
              "right": [ { "id": "omarchy.tray" }, { "id": "omarchy.power" } ]
            }
          },
          "plugins": []
        }
        """;

    private static string OmarchyText(AppState state, int turn) =>
        JsonDocument.Parse(WaybarOutput.Render(state, Now.AddSeconds(turn * WaybarOutput.CycleSeconds), omarchy: true)).RootElement.GetProperty("text").GetString()!;

    [Fact]
    public void OmarchyBarTakesTurnsWithAPaddedRightEdge()
    {
        var shown = Enumerable.Range(0, 3).Select(turn => OmarchyText(State(), turn)).ToList();
        Assert.Contains("C ▰▰▰▱▱ 58%\u00A0", shown);
        Assert.Contains("X ▰▱▱▱▱ 21%\u00A0", shown);
        Assert.Contains("Today $0.00\u00A0", shown);
        Assert.Equal(shown[0], OmarchyText(State(), 3));
    }

    [Fact]
    public void OmarchyBarGivesHermesATurnWhenItIsOn()
    {
        var demo = StateBuilder.Demo(Now);
        Assert.Contains(Enumerable.Range(0, 4).Select(turn => OmarchyText(demo, turn)), text => text.StartsWith("H $"));
    }

    [Fact]
    public void OmarchyBarConfigMovesOnlyTheOldInterval()
    {
        var old = OmarchyBarConfig.Add(OmarchyShell, "/home/u/.local/bin/quotalis")!.Replace("\"interval\": 7", "\"interval\": 30");
        var upgraded = OmarchyBarConfig.Add(old, "/home/u/.local/bin/quotalis")!;
        Assert.Contains("\"interval\": 7", upgraded);
        var own = old.Replace("\"interval\": 30", "\"interval\": 12");
        Assert.Equal(own, OmarchyBarConfig.Add(own, "/home/u/.local/bin/quotalis"));
    }

    [Fact]
    public void OmarchyBarConfigKeepsEverythingElse()
    {
        var added = OmarchyBarConfig.Add(OmarchyShell, "/home/u/.local/bin/quotalis")!;
        var right = JsonDocument.Parse(added).RootElement.GetProperty("bar").GetProperty("layout").GetProperty("right");
        Assert.Equal(3, right.GetArrayLength());
        Assert.Equal("quotalis", right[0].GetProperty("id").GetString());
        Assert.Equal("command", right[0].GetProperty("type").GetString());
        Assert.Equal("/home/u/.local/bin/quotalis bar", right[0].GetProperty("exec").GetString());
        Assert.Equal("/home/u/.local/bin/quotalis open", right[0].GetProperty("onClick").GetString());
        Assert.Equal("omarchy.tray", right[1].GetProperty("id").GetString());
        Assert.Equal(added, OmarchyBarConfig.Add(added, "/elsewhere/quotalis"));

        var removed = JsonNode.Parse(OmarchyBarConfig.Remove(added)!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(OmarchyShell), removed));
        Assert.Equal(OmarchyShell, OmarchyBarConfig.Remove(OmarchyShell));
    }

    [Fact]
    public void OmarchyBarConfigRemovesTheModuleAfterTheUserMovedIt()
    {
        var moved = OmarchyShell.Replace("\"left\": [ { \"id\": \"omarchy.menu\" } ]", "\"left\": [ { \"id\": \"omarchy.menu\" }, { \"id\": \"quotalis\", \"type\": \"command\" } ]");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(OmarchyShell), JsonNode.Parse(OmarchyBarConfig.Remove(moved)!)));
    }

    [Fact]
    public void OmarchyBarConfigLeavesWhatItCannotRead()
    {
        Assert.Null(OmarchyBarConfig.Add("not json", "q"));
        Assert.Null(OmarchyBarConfig.Add("{ \"bar\": {} }", "q"));
        Assert.Null(OmarchyBarConfig.Remove("not json"));
        var spaced = OmarchyBarConfig.Add(OmarchyShell, "/home/my name/quotalis")!;
        Assert.Contains("'/home/my name/quotalis' bar", spaced);
    }

    [Fact]
    public void BarSaysWhenTheServiceIsGone()
    {
        Assert.Equal(["stopped"], Classes(Bar(null)));
        Assert.Equal(["stopped"], Classes(Bar(State(at: Now.AddMinutes(-3)))));
    }

    [Fact]
    public void AWindowPastItsResetCountsAsEmpty()
    {
        var bar = Bar(State(claude: Tool("Claude", 99, 99, Now.AddMinutes(-1)), codex: Tool("Codex")));
        Assert.Equal(0, bar.GetProperty("percentage").GetInt32());
        Assert.Contains("0% (reset)", bar.GetProperty("tooltip").GetString());
    }

    [Theory]
    [InlineData(40)]
    [InlineData(76)]
    [InlineData(300)]
    public void ScreenFitsTheTerminal(int width)
    {
        foreach (var page in Enum.GetValues<ScreenPage>())
        {
            foreach (var line in new Screen(color: false).Render(State(), page, Now, width))
            {
                Assert.True(line.Length <= Math.Clamp(width, 40, Screen.MaxWidth) + 12, line);
                Assert.DoesNotContain('~', line);
            }
        }
    }

    [Fact]
    public void ColoursUseOnlyTheTerminalsOwnPalette()
    {
        var text = string.Join('\n', new Screen(color: true).Render(State(), ScreenPage.Overview, Now, 76));
        Assert.Contains("\x1b[31m", text);
        Assert.DoesNotContain("38;", text); // no fixed 256-colour or RGB values, so the theme decides
        Assert.DoesNotContain('\x1b', string.Join('\n', new Screen(color: false).Render(State(), ScreenPage.Overview, Now, 76)));
    }

    [Fact]
    public void ScreenExplainsAMissingService() =>
        Assert.Contains(new Screen(false).Render(null, ScreenPage.Overview, Now, 76), l => l.Contains("quotalis setup"));

    [Fact]
    public void ResetReadsAsCountdownThenDay()
    {
        Assert.Contains("h", Screen.ResetText(Now.AddHours(2), Now, TimeZoneInfo.Utc));
        Assert.Equal("Sat 09:00", Screen.ResetText(new DateTimeOffset(2026, 6, 6, 9, 0, 0, TimeSpan.Zero), Now, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(1, 1000000, 1)]
    [InlineData(100, 100, 4)]
    public void IntensityKeepsSmallDaysVisible(long tokens, long max, int expected) =>
        Assert.Equal(expected, Screen.Intensity(tokens, max));

    [Fact]
    public void StateSurvivesTheFile()
    {
        var path = Path.Combine(TestData.TempDirectory(), "state.json");
        AppStateFile.Write(path, State());
        var read = AppStateFile.Read(path);
        Assert.Equal(58, read!.Claude.FiveHour!.Percent);
        Assert.False(File.Exists(path + ".part"));
        Assert.Null(AppStateFile.Parse("{ half"));
    }

    private const string Omarchy = """
        {
          // "modules-right": ["old"],
          "layer": "top",
          "modules-right": ["tray", "clock"],
          "clock": { "format": "{:%H:%M}" }
        }
        """;

    [Fact]
    public void WaybarConfigKeepsEverythingElse()
    {
        var added = WaybarConfig.Add(Omarchy, "/home/u/.local/bin/quotalis")!;
        Assert.Contains("\"modules-right\": [\"custom/quotalis\", \"tray\", \"clock\"]", added);
        Assert.Contains("// \"modules-right\": [\"old\"]", added);
        Assert.Contains("\"exec\": \"/home/u/.local/bin/quotalis waybar\"", added);
        Assert.Equal(added, WaybarConfig.Add(added, "/elsewhere/quotalis"));
        var removed = WaybarConfig.Remove(added);
        Assert.DoesNotContain("quotalis", removed);
        Assert.Contains("\"modules-right\": [\"tray\", \"clock\"]", removed);
        Assert.Contains("// \"modules-right\": [\"old\"]", removed);
    }

    [Fact]
    public void WaybarConfigHandlesEdgeCases()
    {
        Assert.Null(WaybarConfig.Add("{ \"modules-left\": [] }", "q"));
        Assert.Null(WaybarConfig.Add("{\n// \"modules-right\": []\n}", "q"));
        Assert.Contains("[\"custom/quotalis\"]", WaybarConfig.Add("{ \"modules-right\": [] }", "q"));
        var spaced = WaybarConfig.Add("{ \"modules-right\": [] }", "/home/my name/quotalis")!;
        Assert.Contains("\"exec\": \"'/home/my name/quotalis' waybar\"", spaced);
    }

    [Fact]
    public void WaybarStyleCanBeTakenOutAgain()
    {
        const string css = "#clock { color: red; }\n";
        var added = WaybarConfig.AddStyle(css);
        Assert.Contains("#custom-quotalis.critical", added);
        Assert.Equal(added, WaybarConfig.AddStyle(added));
        Assert.Equal(css, WaybarConfig.RemoveStyle(added));
    }

    [Fact]
    public void DemoStateIsMarkedAndComplete()
    {
        var demo = StateBuilder.Demo(DateTimeOffset.UtcNow);
        Assert.True(demo.Demo);
        Assert.NotNull(demo.Claude.FiveHour);
        Assert.NotEmpty(demo.Days);
        Assert.Contains("(sample data)", new Screen(false).Render(demo, ScreenPage.Overview, DateTimeOffset.UtcNow, 76)[0]);
    }

    [Fact]
    public void ServiceUnitQuotesThePath() =>
        Assert.Contains("ExecStart=\"/home/my name/quotalis\" service", Setup.ServiceUnit("/home/my name/quotalis"));
}
