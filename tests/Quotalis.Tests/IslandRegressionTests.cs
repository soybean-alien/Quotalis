using Quotalis.Core.Island;
using Quotalis.Core.Modules;
using Quotalis.Core.Placement;
using Quotalis.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace Quotalis.Tests;

/// <summary>Auto-hide is one central rule (foreground app + rule + island state), whatever module is showing.</summary>
public class AutoHideAcrossModulesTests
{
    private readonly FakeTimeProvider _time = new();
    private bool? _pointerOver;

    private static readonly AppSettings Settings = new()
    {
        SmartHideProcesses = ["chrome", "code"],
        AutoHideApps =
        [
            new AutoHideApp { Process = "chrome", Name = "Google Chrome" },
            new AutoHideApp { Process = "code", Name = "Visual Studio Code" },
        ],
    };

    private static RestMode RestFor(string process) =>
        SmartHidePolicy.Decide(Settings, IslandAnchor.TopCenter, 0, new ForegroundInfo(process, IsMaximized: true, IsOnIslandMonitor: true));

    /// <summary>How each module typically leaves the island when the user switches apps.</summary>
    private IslandStateMachine IslandShowing(string module)
    {
        var island = new IslandStateMachine(_time) { PointerProbe = () => _pointerOver };
        switch (module)
        {
            case "Claude": // resting compact, Claude featured
                break;
            case "Codex": // an AI session activity is open
                island.ShowEvent();
                break;
            case "Music": // a song change, and the pointer rests on it (on its way to the tabs)
                _pointerOver = true;
                island.ShowEvent();
                island.PointerEntered();
                break;
            case "Focus": // focus just started
                island.ShowEvent(TimeSpan.FromSeconds(3));
                break;
            case "System": // the wide System capsule caught a hover: the peek is open under the pointer
                _pointerOver = true;
                island.PointerEntered();
                _time.Advance(IslandStateMachine.HoverDwell + TimeSpan.FromMilliseconds(10));
                Assert.Equal(IslandMode.Activity, island.Mode);
                break;
            case "Calendar": // "Robotics in 25 min" announcement
                island.ShowEvent(TimeSpan.FromSeconds(5));
                break;
            case "Expanded": // the user had the island open
                island.Activate();
                break;
        }

        return island;
    }

    [Theory]
    [InlineData("Claude", "chrome")]
    [InlineData("Codex", "chrome")]
    [InlineData("Music", "chrome")]
    [InlineData("Focus", "chrome")]
    [InlineData("System", "chrome")]
    [InlineData("Calendar", "chrome")]
    [InlineData("Expanded", "chrome")]
    [InlineData("Claude", "code")]
    [InlineData("Music", "code")]
    [InlineData("System", "code")]
    public void A_configured_app_in_front_retracts_the_island_at_once(string module, string app)
    {
        var island = IslandShowing(module);

        island.SetRest(RestFor(app));

        Assert.Equal(IslandMode.Retracted, island.Mode);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(IslandMode.Retracted, island.Mode); // no timer brings it back
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Music")]
    [InlineData("System")]
    [InlineData("Calendar")]
    public void A_normal_app_keeps_normal_behavior(string module)
    {
        var island = IslandShowing(module);
        var before = island.Mode;

        island.SetRest(RestFor("explorer"));

        Assert.Equal(before, island.Mode);
    }

    [Fact]
    public void Events_never_pop_out_of_the_notch()
    {
        var island = IslandShowing("Claude");
        island.SetRest(RestFor("chrome"));

        Assert.False(island.ShowEvent()); // a song change, focus, an AI session
        Assert.Equal(IslandMode.Retracted, island.Mode);
    }

    [Fact]
    public void A_drag_finishes_before_the_island_retracts()
    {
        var island = IslandShowing("Claude");
        island.BeginDrag();
        island.SetRest(RestFor("chrome"));
        Assert.Equal(IslandMode.Compact, island.Mode);

        island.EndDrag();
        Assert.Equal(IslandMode.Retracted, island.Mode);
    }
}

public class IslandContentTests
{
    private static readonly IslandFacts Usage = new(UsageAvailable: true);
    private static readonly IslandFacts Nothing = new(UsageAvailable: false);

    [Theory]
    [InlineData(ModuleId.Git)]
    [InlineData(ModuleId.Hermes)]
    [InlineData(ModuleId.Codex)]
    public void Hover_shows_the_featured_module_not_Claude(ModuleId featured)
    {
        Assert.Equal(PeekContent.Featured, IslandContent.ChoosePeek(featured, Usage));
        Assert.Equal(PeekContent.Featured, IslandContent.ChoosePeek(featured, Nothing));
    }

    [Fact]
    public void Without_favorite_or_last_module_usage_shows_else_info()
    {
        Assert.Equal(PeekContent.Usage, IslandContent.ChoosePeek(null, Usage));
        Assert.Equal(PeekContent.Info, IslandContent.ChoosePeek(null, Nothing));
    }

    [Fact]
    public void Opening_shows_the_featured_module_unless_something_was_just_announced_or_requested()
    {
        Assert.Equal(ModuleId.Git, IslandContent.ChooseTabOnOpen(null, null, ModuleId.Git));
        Assert.Equal(ModuleId.Claude, IslandContent.ChooseTabOnOpen(null, ModuleId.Claude, ModuleId.Git));
        Assert.Equal(ModuleId.Hermes, IslandContent.ChooseTabOnOpen(ModuleId.Hermes, ModuleId.Claude, ModuleId.Git));
        Assert.Null(IslandContent.ChooseTabOnOpen(null, null, null));
    }

    [Fact]
    public void The_last_opened_module_is_featured_when_there_is_no_favorite()
    {
        // Git was opened last; hover and the compact island must stay on Git.
        Assert.Equal(ModuleId.Git, CompactSelector.Select([], ModuleId.Git, _ => true, turn: 3));
        Assert.Equal(PeekContent.Featured, IslandContent.ChoosePeek(ModuleId.Git, Usage));
    }
}
