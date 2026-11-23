using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;
using static LeagueOverlay.Tests.Fixtures;

namespace LeagueOverlay.Tests;

public class CatalogTests
{
    [Fact]
    public void Parses_stats_including_percent_and_flat_penetration()
    {
        var stats = ItemCatalog.ParseStats("<stats><attention>12</attention> Magic Penetration<br><attention>30%</attention> Magic Penetration<br><attention>25%</attention> Critical Strike Chance</stats>");
        Assert.Equal(12, stats[Stat.MagicPenFlat]);
        Assert.Equal(30, stats[Stat.MagicPenPercent]);
        Assert.Equal(25, stats[Stat.CritChance]);
    }

    [Fact]
    public void Shop_list_excludes_components_consumables_and_champion_locked_items()
    {
        var names = Data.Items.ShopCompleted.Select(i => i.Name).ToHashSet();
        Assert.Contains("Infinity Edge", names);
        Assert.Contains("Plated Steelcaps", names);
        Assert.DoesNotContain("Pickaxe", names);
        Assert.DoesNotContain("Boots", names);
        Assert.DoesNotContain("Health Potion", names);
        Assert.DoesNotContain("Ornn Masterwork", names);
    }

    [Fact]
    public void Special_mode_copies_of_removed_items_are_not_in_the_shop()
    {
        Assert.DoesNotContain(Data.Items.ShopCompleted, i => i.Name == "Gargoyle Stoneplate");
        Assert.NotNull(Data.Items.Get(663193)); // still known, so its gold value counts if someone owns it
    }

    [Fact]
    public void Item_value_uses_data_dragon_cost_and_falls_back_to_client_price()
    {
        Assert.Equal(3500, Data.Items.ValueOf(new PlayerItem { ItemID = 3031, Price = 1 }));
        Assert.Equal(999, Data.Items.ValueOf(new PlayerItem { ItemID = 999999, Price = 999 }));
        Assert.Equal(100, Data.Items.ValueOf(new PlayerItem { ItemID = 2003, Count = 2 }));
    }

    [Fact]
    public void Remaining_cost_subtracts_owned_components_anywhere_in_the_tree()
    {
        var mortal = Data.Items.Get(3033)!;
        Assert.Equal(3000, Data.Items.RemainingCost(mortal, []));
        Assert.Equal(3000 - 800, Data.Items.RemainingCost(mortal, [3123]));
        Assert.Equal(0, Data.Items.RemainingCost(mortal, [3033]));
        var rabadon = Data.Items.Get(3089)!; // two rods: owning one only covers one
        Assert.Equal(3600 - 1200, Data.Items.RemainingCost(rabadon, [1058]));
    }
}

public class GoldAndLaneTests
{
    private static LiveGame FullGame() => Game(
        P("Ahri", "ORDER", "MIDDLE", 11, 3089),                 // 3600
        1000,
        P("Darius", "ORDER", "TOP", 12, 3071),                  // 3000
        P("MonkeyKing", "ORDER", "JUNGLE", 10, 1037),           // 875
        P("Jinx", "ORDER", "BOTTOM", 10, 3031),                 // 3500
        P("Leona", "ORDER", "UTILITY", 9),                      // 0
        P("Malphite", "CHAOS", "TOP", 11, 3075),                // 2450
        P("Zed", "CHAOS", "JUNGLE", 11, 3814, 1038),            // 4200
        P("Syndra", "CHAOS", "MIDDLE", 11, 3089),               // 3600
        P("Caitlyn", "CHAOS", "BOTTOM", 11, 3031, 1037),        // 4375
        P("Soraka", "CHAOS", "UTILITY", 10, 1058));             // 1200

    [Fact]
    public void Gold_lead_is_enemy_minus_mine()
    {
        var gold = GoldAnalyzer.Analyze(FullGame(), Data.Items);
        Assert.Equal(3600 + 3000 + 875 + 3500, gold.MyTeamGold);
        Assert.Equal(2450 + 4200 + 3600 + 4375 + 1200, gold.EnemyTeamGold);
        Assert.Equal(gold.EnemyTeamGold - gold.MyTeamGold, gold.EnemyLead);
        Assert.True(gold.EnemyLead > 0);
    }

    [Fact]
    public void Bot_lane_combines_adc_and_support_and_biggest_lead_is_highlighted()
    {
        var lanes = LaneAnalyzer.Analyze(FullGame(), Data.Items, levelWeight: 350)!;
        Assert.Equal(4, lanes.Lanes.Count);

        var top = lanes.Get(Lane.Top)!;
        Assert.Equal(3000 - 2450, top.GoldDiff);
        Assert.Equal(1, top.LevelDiff);
        Assert.Equal(550 + 350, top.Score);

        var bot = lanes.Get(Lane.Bot)!;
        Assert.Equal(3500 - (4375 + 1200), bot.GoldDiff);
        Assert.Equal((10 + 9) - (11 + 10), bot.LevelDiff);
        Assert.Equal(-2075 - 700, bot.Score);

        var jungle = lanes.Get(Lane.Jungle)!;
        Assert.Equal(875 - 4200 - 350, jungle.Score); // -3675: biggest absolute lead (enemy's)
        Assert.Equal(Lane.Jungle, lanes.Highlight);
    }

    [Fact]
    public void Smite_marks_the_jungler_when_positions_are_missing()
    {
        var game = Game(P("Ahri", "ORDER", "", 5), 0,
            P("MonkeyKing", "ORDER", "JUNGLE", 6, 1037), P("Zed", "CHAOS", "JUNGLE", 5));
        foreach (var p in game.AllPlayers) p.Position = ""; // keep the Smite spells, drop positions

        var lanes = LaneAnalyzer.Analyze(game, Data.Items)!;
        var only = Assert.Single(lanes.Lanes);
        Assert.Equal(Lane.Jungle, only.Lane);
        Assert.Equal(875 + 350, only.Score);
    }

    [Fact]
    public void No_lanes_when_nothing_can_be_paired()
    {
        var game = Game(P("Ahri", "ORDER", "", 5), 0, P("Zed", "CHAOS", "", 5));
        Assert.Null(LaneAnalyzer.Analyze(game, Data.Items));
    }

    [Fact]
    public void Overlay_state_is_value_equal_for_identical_games()
    {
        Assert.Equal(OverlayState.Build(FullGame(), Data), OverlayState.Build(FullGame(), Data));
    }
}

public class RecommenderTests
{
    private static IEnumerable<Recommendation> All(ItemAdvice a) =>
        (a.Core is null ? [] : new[] { a.Core }).Concat(a.Situational);

    private static ItemInfo Info(Recommendation r) => Data.Items.Get(r.ItemId)!;

    [Fact]
    public void Ap_heavy_enemies_push_magic_resist()
    {
        var game = Game(P("Ahri", "ORDER", "MIDDLE", 11, 3020), 0,
            P("Syndra", "CHAOS", "MIDDLE", 11, 3089), P("Annie", "CHAOS", "TOP", 11, 3089), P("Lux", "CHAOS", "UTILITY", 11, 3157));

        var advice = ItemRecommender.Recommend(game, Data);
        var mr = All(advice).FirstOrDefault(r => Info(r).Get(Stat.MagicResist) > 0);
        Assert.NotNull(mr);
        Assert.Contains("magic", mr!.Reason);
    }

    [Fact]
    public void Healer_gets_exactly_one_anti_heal_item_matching_damage_type()
    {
        var game = Game(P("Jinx", "ORDER", "BOTTOM", 11, 3006), 0,
            P("Soraka", "CHAOS", "UTILITY", 11), P("Caitlyn", "CHAOS", "BOTTOM", 11, 3031));

        var advice = ItemRecommender.Recommend(game, Data);
        var wounds = All(advice).Where(r => Info(r).AppliesWounds).ToList();
        var gw = Assert.Single(wounds);
        Assert.Equal("Mortal Reminder", gw.Name); // AD option for a marksman, not Morellonomicon
        Assert.Contains("Soraka", gw.Reason);
    }

    [Fact]
    public void Owned_items_and_unique_passive_clashes_are_never_recommended()
    {
        var game = Game(P("Darius", "ORDER", "TOP", 14, 3053, 3047), 0,
            P("Syndra", "CHAOS", "MIDDLE", 14, 3089), P("Annie", "CHAOS", "TOP", 14, 3089, 1058));

        var names = All(ItemRecommender.Recommend(game, Data)).Select(r => r.Name).ToList();
        Assert.NotEmpty(names);
        Assert.DoesNotContain("Sterak's Gage", names);
        Assert.DoesNotContain("Maw of Malmortius", names);   // shares the Lifeline passive
        Assert.DoesNotContain(names, n => Data.Items.ShopCompleted.First(i => i.Name == n).IsBoots); // already has boots
    }

    [Fact]
    public void Buy_now_counts_owned_components()
    {
        Player Jinx() => P("Jinx", "ORDER", "BOTTOM", 9, 1038, 3006);
        Player[] enemies = [P("Caitlyn", "CHAOS", "BOTTOM", 9, 3031)];

        var poor = ItemRecommender.Recommend(Game(Jinx(), 2000, enemies), Data);
        Assert.Equal("Infinity Edge", poor.Core!.Name);
        Assert.Equal(3500 - 1300, poor.Core.RemainingCost);
        Assert.StartsWith("Buy Pickaxe", poor.BuyNow);   // most expensive affordable component

        var rich = ItemRecommender.Recommend(Game(Jinx(), 2300, enemies), Data);
        Assert.StartsWith("Complete Infinity Edge now", rich.BuyNow);
    }

    [Fact]
    public void Missing_boots_suggests_boots_that_fit_the_threats()
    {
        var tank = Game(P("Malphite", "ORDER", "TOP", 8), 0,
            P("Zed", "CHAOS", "MIDDLE", 8, 3814), P("Caitlyn", "CHAOS", "BOTTOM", 8, 3031), P("Darius", "CHAOS", "TOP", 8, 3071));
        Assert.Contains(ItemRecommender.Recommend(tank, Data).Situational, r => r.Name == "Plated Steelcaps");

        var adc = Game(P("Jinx", "ORDER", "BOTTOM", 8), 0, P("Syndra", "CHAOS", "MIDDLE", 8));
        Assert.Contains(ItemRecommender.Recommend(adc, Data).Situational, r => r.Name == "Berserker's Greaves");
    }

    [Fact]
    public void Built_items_override_the_champions_default_damage_type()
    {
        var champ = Data.Champion("Jinx")!;
        var apJinx = P("Jinx", "ORDER", "BOTTOM", 10, 3089);
        Assert.True(ItemRecommender.Profile(champ, apJinx, Data.Items).Ap);
        Assert.False(ItemRecommender.Profile(champ, P("Jinx", "ORDER", "BOTTOM", 10), Data.Items).Ap);
    }

    [Fact]
    public void Unknown_champion_or_missing_data_returns_no_advice()
    {
        var game = Game(P("NotAChampion", "ORDER", "TOP", 5), 0, P("Zed", "CHAOS", "TOP", 5));
        Assert.Equal(ItemAdvice.None, ItemRecommender.Recommend(game, Data));
        Assert.Equal(ItemAdvice.None, ItemRecommender.Recommend(game, StaticData.Empty));
    }

    [Fact]
    public void Boots_upgraded_in_the_role_quest_slot_still_count_as_boots()
    {
        // 3172 (Gunmetal Greaves) sits in the extra boots slot after the bot lane quest and isn't tagged as boots.
        var me = P("Jinx", "ORDER", "BOTTOM", 12, 3031);
        me.Items.Add(new PlayerItem { ItemID = 3172, DisplayName = "Gunmetal Greaves", Price = 1500, Slot = 7 });
        var advice = ItemRecommender.Recommend(Game(me, 0, P("Syndra", "CHAOS", "MIDDLE", 12)), Data);
        Assert.DoesNotContain(All(advice), r => Info(r).IsBoots);

        Assert.True(Data.Items.IsUpgradedBoots(new PlayerItem { ItemID = 3172 }));            // via Berserker's "into"
        Assert.True(Data.Items.IsUpgradedBoots(new PlayerItem { ItemID = 3006 }));
        Assert.True(Data.Items.IsUpgradedBoots(new PlayerItem { ItemID = 9999, DisplayName = "Shiny Greaves" })); // by name
        Assert.False(Data.Items.IsUpgradedBoots(new PlayerItem { ItemID = 1001 }));           // basic boots don't count
    }

    [Theory]
    [InlineData("MIDDLE", 9, true)]
    [InlineData("BOTTOM", 9, true)]
    [InlineData("BOTTOM", 8, false)]
    [InlineData("TOP", 12, false)]
    public void Farsight_reminder_from_level_9_in_mid_and_bot(string position, int level, bool expected)
    {
        var me = P("Jinx", "ORDER", position, level, 3340);
        var advice = ItemRecommender.Recommend(Game(me, 0, P("Syndra", "CHAOS", "MIDDLE", 9)), Data);
        Assert.Equal(expected, advice.Reminder?.Contains("Farsight Alteration") == true);

        var swapped = P("Jinx", "ORDER", position, level, 3363);
        Assert.Null(ItemRecommender.Recommend(Game(swapped, 0, P("Syndra", "CHAOS", "MIDDLE", 9)), Data).Reminder);
    }

    [Fact]
    public void Support_is_reminded_to_buy_oracle_lens_once_the_support_item_upgrades()
    {
        LiveGame G(params int[] items) { var g = Game(P("Leona", "ORDER", "UTILITY", 5, items), 0, P("Zed", "CHAOS", "MIDDLE", 5)); g.GameData!.GameTime = 200; return g; }
        Assert.Null(ItemRecommender.Recommend(G(3865, 3340), Data).Reminder);              // still World Atlas
        Assert.Contains("Oracle Lens", ItemRecommender.Recommend(G(3866, 3340), Data).Reminder); // Runic Compass
        Assert.Null(ItemRecommender.Recommend(G(3866, 3364), Data).Reminder);              // already swapped

        var late = G(3865, 3340); late.GameData!.GameTime = ItemRecommender.SweeperFallbackSeconds;
        Assert.Contains("Oracle Lens", ItemRecommender.Recommend(late, Data).Reminder);
    }
}

public class HistoryTests
{
    private static LiveGame At(double time, Player me, params Player[] others)
    {
        var g = Game(me, 0, others);
        g.GameData!.GameTime = time;
        return g;
    }

    [Fact]
    public void Records_purchases_level_ups_and_team_gold()
    {
        var h = new GameHistory();
        h.Update(At(10, P("Jinx", "ORDER", "BOTTOM", 1, 1001, 2003), P("Zed", "CHAOS", "MIDDLE", 1)), Data);
        h.Update(At(400, P("Jinx", "ORDER", "BOTTOM", 3, 3006, 1038), P("Zed", "CHAOS", "MIDDLE", 4, 1037)), Data);

        var s = h.Snapshot();
        Assert.False(s.StartedLate);
        Assert.Equal(["Jinx#ORDER", "Zed#CHAOS"], s.Players.Select(p => p.Key));
        Assert.True(s.Players[0].MyTeam && s.Players[0].IsMe);
        // Starting boots at 10s (potion skipped), then Berserker's + B.F. Sword for Jinx and Pickaxe for Zed at 400s.
        Assert.Equal([(10.0, 1001, false), (400.0, 3006, true), (400.0, 1038, false), (400.0, 1037, false)],
            s.Items.Select(e => (e.Time, e.ItemId, e.Completed)));
        Assert.Equal([2, 3], s.Levels.Where(l => l.PlayerKey == "Jinx#ORDER").Select(l => l.Level));
        Assert.Equal(new GoldSample(400, 1100 + 1300, 875), s.Gold[^1]);
    }

    [Fact]
    public void Undone_purchase_is_removed_and_new_game_resets()
    {
        var h = new GameHistory();
        h.Update(At(300, P("Jinx", "ORDER", "BOTTOM", 5, 1036), P("Zed", "CHAOS", "MIDDLE", 5)), Data);
        h.Update(At(302, P("Jinx", "ORDER", "BOTTOM", 5, 1036, 1038), P("Zed", "CHAOS", "MIDDLE", 5)), Data);
        h.Update(At(306, P("Jinx", "ORDER", "BOTTOM", 5, 1036), P("Zed", "CHAOS", "MIDDLE", 5)), Data);
        var s = h.Snapshot();
        Assert.True(s.StartedLate);
        Assert.Empty(s.Items); // items owned when recording started late aren't invented as purchases; the undo is dropped

        h.Update(At(5, P("Ahri", "ORDER", "MIDDLE", 1), P("Zed", "CHAOS", "MIDDLE", 1)), Data);
        Assert.Equal("Ahri#ORDER", h.Snapshot().Players[0].Key);
        Assert.Single(h.Snapshot().Gold);
    }
}
