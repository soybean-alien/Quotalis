using Quotalis.Core.Modules;
using Quotalis.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace Quotalis.Tests;

public class ModuleCatalogTests
{
    [Fact]
    public void Default_order_matches_the_product_order()
    {
        Assert.Equal(
            [ModuleId.Claude, ModuleId.Codex, ModuleId.Hermes, ModuleId.Git],
            new AppSettings().Order);
    }

    [Fact]
    public void Saved_order_is_kept_and_new_modules_are_appended()
    {
        // "Music" and "Focus" were modules of earlier versions: a saved order naming them simply drops them.
        var order = ModuleCatalog.NormalizeOrder(["Git", "music", "Weather", "Git", "Focus", "Claude"]);

        Assert.Equal(ModuleId.Git, order[0]);
        Assert.Equal(ModuleId.Claude, order[1]);
        Assert.Equal(4, order.Count);
        Assert.Equal(ModuleId.Codex, order[2]);
        Assert.Equal(ModuleId.Hermes, order[^1]);
    }

    [Fact]
    public void Moving_is_clamped_at_the_ends()
    {
        var order = ModuleCatalog.DefaultOrder;

        Assert.Equal(ModuleId.Codex, ModuleCatalog.Move(order, ModuleId.Codex, -1)[0]);
        Assert.Equal(order, ModuleCatalog.Move(order, ModuleId.Claude, -1));
        Assert.Equal(ModuleId.Hermes, ModuleCatalog.Move(order, ModuleId.Hermes, 5)[^1]);
    }

    [Fact]
    public void Module_settings_round_trip_and_are_sanitised()
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        File.WriteAllText(path, """{ "moduleOrder": ["Git", "Nope", "System"], "gitEnabled": false, "musicEnabled": true }""");

        var s = new SettingsStore(path).Current;

        Assert.Equal(ModuleId.Git, s.Order[0]);
        Assert.False(s.IsEnabled(ModuleId.Git));
        Assert.True(s.IsEnabled(ModuleId.Claude));

        var store = new SettingsStore(path);
        store.Update(x => x.SetEnabled(ModuleId.Hermes, true));
        Assert.True(new SettingsStore(path).Current.HermesEnabled);
    }
}

public class CompactFavoritesTests
{
    private static readonly IReadOnlyList<ModuleId> Order = ModuleCatalog.DefaultOrder;

    [Fact]
    public void One_favorite_is_always_shown()
    {
        Assert.Equal(ModuleId.Git, CompactSelector.Select([ModuleId.Git], ModuleId.Claude, _ => true, turn: 5));
    }

    [Fact]
    public void Several_favorites_take_turns_and_skip_silent_ones()
    {
        IReadOnlyList<ModuleId> favorites = [ModuleId.Claude, ModuleId.Git, ModuleId.Hermes];
        bool Speaks(ModuleId m) => m != ModuleId.Git; // no repository

        Assert.Equal(ModuleId.Claude, CompactSelector.Select(favorites, null, Speaks, 0));
        Assert.Equal(ModuleId.Hermes, CompactSelector.Select(favorites, null, Speaks, 1));
        Assert.Equal(ModuleId.Claude, CompactSelector.Select(favorites, null, Speaks, 2));
        Assert.True(CompactSelector.Rotates(favorites));
    }

    [Fact]
    public void The_AI_tools_are_followed_by_a_today_total_slide()
    {
        IReadOnlyList<ModuleId> favorites = [ModuleId.Claude, ModuleId.Codex, ModuleId.Hermes];

        Assert.Equal((ModuleId.Claude, false), CompactSelector.SelectSlide(favorites, null, _ => true, 0));
        Assert.Equal((ModuleId.Codex, false), CompactSelector.SelectSlide(favorites, null, _ => true, 1));
        Assert.Equal((ModuleId.Hermes, false), CompactSelector.SelectSlide(favorites, null, _ => true, 2));
        Assert.Equal(((ModuleId?)null, true), CompactSelector.SelectSlide(favorites, null, _ => true, 3));
        Assert.Equal((ModuleId.Claude, false), CompactSelector.SelectSlide(favorites, null, _ => true, 4));
    }

    [Fact]
    public void The_today_slide_sits_after_the_last_AI_tool_and_needs_two_of_them()
    {
        IReadOnlyList<ModuleId> withGit = [ModuleId.Claude, ModuleId.Codex, ModuleId.Git];
        Assert.Equal(((ModuleId?)null, true), CompactSelector.SelectSlide(withGit, null, _ => true, 2));
        Assert.Equal((ModuleId.Git, false), CompactSelector.SelectSlide(withGit, null, _ => true, 3));

        IReadOnlyList<ModuleId> oneTool = [ModuleId.Claude, ModuleId.Git];
        Assert.Equal((ModuleId.Claude, false), CompactSelector.SelectSlide(oneTool, null, _ => true, 2));
        Assert.Equal((ModuleId.Codex, false), CompactSelector.SelectSlide([], ModuleId.Codex, _ => true, 3));
    }

    [Fact]
    public void When_no_favorite_speaks_they_still_rotate()
    {
        Assert.Equal(ModuleId.Hermes, CompactSelector.Select([ModuleId.Git, ModuleId.Hermes], null, _ => false, 1));
    }

    [Fact]
    public void Without_favorites_the_last_opened_module_is_shown_else_the_classic_summary()
    {
        Assert.Equal(ModuleId.Codex, CompactSelector.Select([], ModuleId.Codex, _ => true, 3));
        Assert.Null(CompactSelector.Select([], null, _ => true, 3));
        Assert.False(CompactSelector.Rotates([ModuleId.Codex]));
    }

    [Fact]
    public void Disabled_modules_are_never_favorites_and_order_follows_the_tabs()
    {
        var favorites = CompactSelector.EffectiveFavorites(["Hermes", "Claude", "Nope", "Git"], Order, m => m != ModuleId.Git);

        Assert.Equal([ModuleId.Claude, ModuleId.Hermes], favorites);
    }

    [Fact]
    public void Favorites_persist_and_are_sanitised()
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        var store = new SettingsStore(path);
        store.Update(s =>
        {
            s.SetFavorite(ModuleId.Codex, true);
            s.SetFavorite(ModuleId.Claude, true);
            s.SetFavorite(ModuleId.Codex, false);
            s.FavoriteModules.Add("weather");
            s.FavoriteModules.Add("claude");
        });

        var reloaded = new SettingsStore(path).Current;
        Assert.Equal(["Claude"], reloaded.FavoriteModules);
        Assert.True(reloaded.IsFavorite(ModuleId.Claude));
    }

    [Fact]
    public void A_single_module_tab_star_toggles_that_module()
    {
        Assert.False(TabFavorite.IsFavorite([ModuleId.Hermes], [ModuleId.Git]));
        AssertToggle(TabFavorite.Toggle([ModuleId.Hermes], [ModuleId.Git]), true, ModuleId.Hermes);
        AssertToggle(TabFavorite.Toggle([ModuleId.Hermes], [ModuleId.Hermes, ModuleId.Git]), false, ModuleId.Hermes);
    }

    [Fact]
    public void The_usage_star_with_one_provider_is_that_providers_star()
    {
        // Claude Code only (Codex off): the star features Claude, like any other tab.
        AssertToggle(TabFavorite.Toggle([ModuleId.Claude], []), true, ModuleId.Claude);
        Assert.True(TabFavorite.IsFavorite([ModuleId.Claude], [ModuleId.Claude]));
    }

    [Fact]
    public void The_usage_star_with_both_providers_features_and_clears_both()
    {
        IReadOnlyList<ModuleId> usage = [ModuleId.Claude, ModuleId.Codex];

        AssertToggle(TabFavorite.Toggle(usage, [ModuleId.Git]), true, ModuleId.Claude, ModuleId.Codex);
        AssertToggle(TabFavorite.Toggle(usage, [ModuleId.Claude, ModuleId.Codex]), false, ModuleId.Claude, ModuleId.Codex);

        // A star saved by an earlier version for one provider only: the tab shows filled, and a click clears it.
        Assert.True(TabFavorite.IsFavorite(usage, [ModuleId.Codex]));
        AssertToggle(TabFavorite.Toggle(usage, [ModuleId.Codex]), false, ModuleId.Claude, ModuleId.Codex);
    }

    private static void AssertToggle((IReadOnlyList<ModuleId> Modules, bool Favorite) toggle, bool favorite, params ModuleId[] modules)
    {
        Assert.Equal(favorite, toggle.Favorite);
        Assert.Equal(modules, toggle.Modules);
    }

    [Fact]
    public void Last_active_module_persists_in_its_own_file()
    {
        var path = Path.Combine(TestData.TempDirectory(), "ui-state.json");
        new UiStateStore(path).SetLastActive(ModuleId.Codex);

        Assert.Equal(ModuleId.Codex, new UiStateStore(path).LastActiveModule);
        Assert.Null(new UiStateStore(Path.Combine(TestData.TempDirectory(), "missing.json")).LastActiveModule);
    }

    [Fact]
    public void A_corrupt_ui_state_starts_fresh()
    {
        var path = Path.Combine(TestData.TempDirectory(), "ui-state.json");
        File.WriteAllText(path, "{ nope");

        Assert.Null(new UiStateStore(path).LastActiveModule);
    }
}
