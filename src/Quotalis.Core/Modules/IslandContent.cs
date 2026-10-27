namespace Quotalis.Core.Modules;

/// <summary>What a hover peek shows.</summary>
public enum PeekContent
{
    /// <summary>The featured module (a favorite, or the module opened last).</summary>
    Featured,
    Usage,
    Info,
}

/// <summary>Live facts the content choice depends on (no UI types).</summary>
public sealed record IslandFacts(bool UsageAvailable);

/// <summary>
/// Which module content the island shows. Kept apart from the display state (Hidden, Compact,
/// Medium, Expanded, Dragging; see IslandStateMachine): favorites and the module opened last
/// decide <i>what</i> is shown, never <i>whether</i> the island is open.
/// <list type="bullet">
/// <item>Hover and opening show the featured module: the favorite (or the rotating favorites), else
/// the module opened last. Claude is never a silent default.</item>
/// <item>Only with neither does the island fall back to AI usage, then a short info line.</item>
/// <item>The module opened last changes only when the user picks a tab, not when the island
/// opens on its own choice.</item>
/// </list>
/// </summary>
public static class IslandContent
{
    public static PeekContent ChoosePeek(ModuleId? featured, IslandFacts facts) =>
        featured is not null ? PeekContent.Featured
        : facts.UsageAvailable ? PeekContent.Usage
        : PeekContent.Info;

    /// <summary>
    /// The module whose tab opens when the island expands: an explicit request first, then the event the user just
    /// saw, then the featured module; null keeps the current tab.
    /// </summary>
    public static ModuleId? ChooseTabOnOpen(ModuleId? requested, ModuleId? justAnnounced, ModuleId? featured) =>
        requested ?? justAnnounced ?? featured;
}
