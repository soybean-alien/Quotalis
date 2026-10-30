namespace Quotalis.Core.Modules;

/// <summary>
/// Decides which module the compact island features:
/// <list type="bullet">
/// <item>One favorite: that one.</item>
/// <item>Several favorites: they take turns every <see cref="RotationInterval"/>; favorites with
/// nothing to say right now (no track playing, no session today) are skipped, unless none has
/// anything to say.</item>
/// <item>Two or more AI tools among them (Claude, Codex, Hermes): a "Today" slide with their combined
/// total follows the last of them (<see cref="SelectSlide"/>).</item>
/// <item>No favorite: the module opened last.</item>
/// <item>Neither: null, and the island keeps its classic summary (focus, AI usage, music).</item>
/// </list>
/// Disabled modules never appear: callers pass only enabled ones.
/// </summary>
public static class CompactSelector
{
    public static readonly TimeSpan RotationInterval = TimeSpan.FromSeconds(7);

    /// <param name="favorites">Enabled favorites in the user's module order.</param>
    /// <param name="lastActive">The module opened last, if it is enabled.</param>
    /// <param name="hasContent">Whether a module has something to show right now.</param>
    /// <param name="turn">Increments every rotation interval.</param>
    public static ModuleId? Select(IReadOnlyList<ModuleId> favorites, ModuleId? lastActive, Func<ModuleId, bool> hasContent, long turn)
    {
        if (favorites.Count == 0)
        {
            return lastActive;
        }

        var speaking = favorites.Where(hasContent).ToList();
        var pool = speaking.Count > 0 ? speaking : favorites;
        var index = (int)(((turn % pool.Count) + pool.Count) % pool.Count);
        return pool[index];
    }

    /// <summary>
    /// Like <see cref="Select"/>, with the "Today" slide: when two or more AI tools take turns, one more
    /// turn after the last of them shows today's combined total (<c>IsTotal</c>, no module).
    /// </summary>
    public static (ModuleId? Module, bool IsTotal) SelectSlide(IReadOnlyList<ModuleId> favorites, ModuleId? lastActive, Func<ModuleId, bool> hasContent, long turn)
    {
        if (favorites.Count == 0)
        {
            return (lastActive, false);
        }

        var speaking = favorites.Where(hasContent).ToList();
        var pool = (speaking.Count > 0 ? speaking : favorites).Select(m => (ModuleId?)m).ToList();
        if (pool.Count(m => IsAiTool(m!.Value)) >= 2)
        {
            pool.Insert(pool.FindLastIndex(m => IsAiTool(m!.Value)) + 1, null);
        }

        var index = (int)(((turn % pool.Count) + pool.Count) % pool.Count);
        return pool[index] is { } module ? (module, false) : (null, true);
    }

    public static bool IsAiTool(ModuleId module) => module is ModuleId.Claude or ModuleId.Codex or ModuleId.Hermes;

    /// <summary>Whether a rotation timer is needed at all (two or more favorites).</summary>
    public static bool Rotates(IReadOnlyList<ModuleId> favorites) => favorites.Count > 1;

    /// <summary>Favorites as saved, reduced to enabled modules in the user's order.</summary>
    public static List<ModuleId> EffectiveFavorites(IEnumerable<string> saved, IReadOnlyList<ModuleId> order, Func<ModuleId, bool> isEnabled)
    {
        var set = saved
            .Select(s => Enum.TryParse<ModuleId>(s, ignoreCase: true, out var id) && Enum.IsDefined(id) ? id : (ModuleId?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();
        return order.Where(m => set.Contains(m) && isEnabled(m)).ToList();
    }
}
