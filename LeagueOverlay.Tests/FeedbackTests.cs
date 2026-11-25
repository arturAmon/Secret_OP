using System.Text.Json;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Live;
using static LeagueOverlay.Tests.Fixtures;

namespace LeagueOverlay.Tests;

/// <summary>Changes from playing with the rune version: healing themes, surprise plan kept, starters, sweeper, Feast.</summary>
public class FeedbackTests
{
    [Fact]
    public void Healing_theme_only_for_champions_with_heals_or_shields()
    {
        var healer = SurpriseBuilder.Themes.Single(t => t.Key == "healer");
        Assert.False(healer.Suits(Data.Champion("Ekko")!));  // magic 7, but nothing to heal with
        Assert.False(healer.Suits(Data.Champion("Annie")!));
        Assert.True(healer.Suits(Data.Champion("Lux")!));    // shield
        for (int seed = 0; seed < 50; seed++)
            Assert.NotEqual("healer", SurpriseBuilder.Plan(Data.Champion("Ekko")!, Data.Items, seed)?.Theme.Key);
    }

    [Fact]
    public void Surprise_plan_from_champion_select_is_kept_for_the_whole_game()
    {
        var now = new DateTime(2026, 11, 25, 20, 0, 0);
        var session = new SurpriseSession(() => now);
        var ahri = Data.Champion("Ahri")!;
        int rolls = 0;
        SurprisePlan? Roll() { rolls++; return SurpriseBuilder.Plan(ahri, Data.Items, rolls); }

        Assert.True(session.ChampSelect(ahri, Roll));
        var plan = session.Plan;
        Assert.False(session.ChampSelect(ahri, Roll));             // every read in champion select: same plan
        now = now.AddMinutes(1);
        session.NoGame();                                           // loading screen
        Assert.False(session.Game(ahri, Roll));                     // the game: carried over
        for (int i = 0; i < 30; i++) { now = now.AddSeconds(2); Assert.False(session.Game(ahri, Roll)); }
        now = now.AddSeconds(30);
        session.NoGame();                                           // a hiccup in the live data
        Assert.False(session.Game(ahri, Roll));
        Assert.Same(plan, session.Plan);
        Assert.Equal(1, rolls);

        session.Reroll();                                           // asked for
        Assert.True(session.Game(ahri, Roll));
        now = now.AddMinutes(10);
        session.NoGame();                                           // game over
        Assert.True(session.ChampSelect(ahri, Roll));              // next champion select: new plan, even on the same champion
        Assert.True(session.ChampSelect(Data.Champion("Jinx")!, Roll)); // other champion
    }

    private static LiveGame Start(string champ, string position, double time, params int[] items)
    {
        var g = Game(P(champ, "ORDER", position, 1, items), 500, P("Zed", "CHAOS", position, 1));
        g.GameData!.GameTime = time;
        return g;
    }

    [Fact]
    public void Starting_items_are_suggested_and_a_warning_shows_when_nothing_was_bought()
    {
        var early = ItemRecommender.Recommend(Start("Jinx", "BOTTOM", 10, 3340), Data);
        Assert.Equal("Starting items: Doran's Blade + Health Potion", early.Reminder);
        Assert.False(early.ReminderUrgent);
        Assert.Equal(StarterItems.DoransBlade, early.ReminderItemId);

        var late = ItemRecommender.Recommend(Start("Ahri", "MIDDLE", 50, 3340), Data);
        Assert.Equal("You haven't bought anything yet! Start with Doran's Ring + 2 Health Potion", late.Reminder);
        Assert.True(late.ReminderUrgent);

        var bought = ItemRecommender.Recommend(Start("Jinx", "BOTTOM", 50, 1055, 2003, 3340), Data);
        Assert.False(bought.ReminderUrgent);
        Assert.DoesNotContain("Starting", bought.Reminder ?? "");

        Assert.Equal([StarterItems.WorldAtlas, StarterItems.HealthPotion, StarterItems.HealthPotion], StarterItems.For("UTILITY", BuildStyle.Tank, false, false));
        Assert.Equal(StarterItems.ScorchclawPup, StarterItems.For("JUNGLE", BuildStyle.Fighter, false, true)[0]);
        Assert.Equal(StarterItems.MosstomperSeedling, StarterItems.For("JUNGLE", BuildStyle.Tank, false, true)[0]);
        Assert.Equal(StarterItems.DoransRing, StarterItems.For("BOTTOM", BuildStyle.Mage, true, false)[0]); // surprise AP ADC
    }

    [Fact]
    public void Popular_starting_items_come_from_the_first_minute_of_collected_games()
    {
        const string match = """
            { "info": { "queueId": 420, "gameDuration": 1900, "gameVersion": "16.23.1", "participants": [
                { "participantId": 1, "championId": 222, "teamPosition": "BOTTOM", "win": true } ] } }
            """;
        const string timeline = """
            { "info": { "frames": [ { "events": [
                { "type": "ITEM_PURCHASED", "participantId": 1, "itemId": 3340, "timestamp": 100 },
                { "type": "ITEM_PURCHASED", "participantId": 1, "itemId": 2003, "timestamp": 2000 },
                { "type": "ITEM_PURCHASED", "participantId": 1, "itemId": 1056, "timestamp": 2500 },
                { "type": "ITEM_UNDO", "participantId": 1, "beforeId": 1056, "afterId": 0, "timestamp": 3000 },
                { "type": "ITEM_PURCHASED", "participantId": 1, "itemId": 1055, "timestamp": 3500 },
                { "type": "ITEM_PURCHASED", "participantId": 1, "itemId": 2003, "timestamp": 300000 } ] } ] } }
            """;
        using var m = JsonDocument.Parse(match);
        using var t = JsonDocument.Parse(timeline);
        var b = MatchParser.Parse(m.RootElement, t.RootElement, Data.Items).Single();
        Assert.Equal([1055, 2003], b.Starter!); // trinket and the undone Ring left out, the later potion too

        var builds = Enumerable.Range(0, 12).Select(i => b with { Items = [3031] }).ToList();
        var popular = ChampionBuild.From(builds, "BOTTOM", "16.23")!;
        Assert.Equal([1055, 2003], popular.Starter);
        Assert.Equal([1055, 2003], StarterItems.Best(popular, "BOTTOM", BuildStyle.Mage, true, false));
    }

    [Fact]
    public void Junglers_are_told_to_take_the_sweeper_unless_they_jump_to_wards()
    {
        LiveGame Jungle(string champ, double time, int trinket)
        {
            var g = Game(P(champ, "ORDER", "JUNGLE", 6, 1055, trinket), 500, P("Zed", "CHAOS", "JUNGLE", 6));
            g.GameData!.GameTime = time;
            return g;
        }
        Assert.Null(ItemRecommender.TrinketReminder(Jungle("Darius", 200, 3340), Jungle("Darius", 200, 3340).Me!, Data.Items));
        var g = Jungle("Darius", ItemRecommender.JungleSweeperSeconds, 3340);
        Assert.Contains("Oracle Lens", ItemRecommender.TrinketReminder(g, g.Me!, Data.Items));
        g = Jungle("Darius", 900, 3364);
        Assert.Null(ItemRecommender.TrinketReminder(g, g.Me!, Data.Items)); // already has it
        g = Jungle("LeeSin", 900, 3340);
        Assert.Null(ItemRecommender.TrinketReminder(g, g.Me!, Data.Items)); // needs wards to jump to
    }

    [Fact]
    public void Feast_counts_how_many_minion_stacks_are_left()
    {
        var tracker = new StackTracker();
        var champ = Data.Champion("Chogath")!;
        LiveGame Cho(double time, int rank, int stacks, params GameEvent[] events)
        {
            var g = Game(P("Chogath", "ORDER", "TOP", 11), 500, P("Darius", "CHAOS", "TOP", 11));
            g.GameData!.GameTime = time;
            g.ActivePlayer!.Abilities = new Abilities { R = new Ability { AbilityLevel = rank } };
            g.ActivePlayer.ChampionStats = new ChampionStats { MaxHealth = champ.StatAt("hp", 11) + stacks * StackTracker.FeastHealthPerStack[rank - 1] };
            g.Events = new EventList { Events = events.ToList() };
            return g;
        }
        var kill = new GameEvent { EventName = "ChampionKill", EventTime = 703, KillerName = "Chogath", VictimName = "Darius" };

        var line = tracker.Update(Cho(600, 1, 2), Data).Lines.Single();      // app started with 2 stacks: taken as minions
        Assert.Equal("2 stacks", line.Value);
        Assert.StartsWith("Minions & monsters: 2 of 6 used, 4 left.", line.Meaning);
        line = tracker.Update(Cho(700, 1, 3), Data).Lines.Single();          // +1, no kill: a minion
        Assert.StartsWith("Minions & monsters: 3 of 6 used, 3 left.", line.Meaning);
        line = tracker.Update(Cho(705, 1, 4, kill), Data).Lines.Single();    // +1 with a champion kill: doesn't count
        Assert.StartsWith("Minions & monsters: 3 of 6 used", line.Meaning);
        line = tracker.Update(Cho(1000, 2, 8, kill), Data).Lines.Single();   // rank 2 (120 per stack), +4 minions: capped at 6
        Assert.Equal("8 stacks", line.Value);
        Assert.StartsWith("Minions & monsters: 6 of 6 used, none left", line.Meaning);
    }

    private static readonly LeagueOverlay.Core.Data.ChampionKits Kits;
    static FeedbackTests()
    {
        using var f = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "meraki_sample.json"));
        Kits = LeagueOverlay.Core.Data.ChampionKits.ParseMeraki(f);
    }

    private static IEnumerable<string> ViableStyles(string champion)
    {
        var c = Data.Champion(champion)!;
        var kit = Kits.For(c.Key);
        Assert.NotNull(kit);
        return SurpriseBuilder.Themes.Where(t => t.Suits(c, kit)).Select(t => t.Key);
    }

    [Fact]
    public void Ability_scaling_is_read_from_Meraki_data()
    {
        var jinx = Kits.For(222)!;
        Assert.Equal(1, jinx.ApAbilities);          // only E
        Assert.True(jinx.AdAbilities >= 2);
        Assert.True(jinx.AttackBased);
        Assert.True(jinx.Ranged);
        Assert.Equal(4, Kits.For(81)!.ApAbilities); // Ezreal
        Assert.True(Kits.For(16)!.HealsAllies);     // Soraka
        Assert.True(Kits.For(99)!.HealsAllies);     // Lux shields allies
        Assert.False(Kits.For(245)!.HealsAllies);   // Ekko only shields himself
        Assert.True(Kits.For(54)!.DefenseAbilities >= 1); // Malphite scales with armor
        Assert.Equal(0, Kits.For(86)!.ApAbilities); // Garen

        var cache = LeagueOverlay.Core.Data.ChampionKits.FromCacheJson(Kits.ToCacheJson());
        Assert.Equal(Kits.For(222), cache.For(222));
    }

    [Fact]
    public void Surprise_styles_need_a_kit_that_scales_with_them()
    {
        Assert.DoesNotContain("ap", ViableStyles("Jinx"));
        Assert.DoesNotContain("battlemage", ViableStyles("Jinx"));
        Assert.Contains("tank", ViableStyles("Jinx"));
        Assert.Contains("ap", ViableStyles("Ezreal"));
        Assert.DoesNotContain("ap", ViableStyles("Garen"));
        Assert.DoesNotContain("healer", ViableStyles("Ekko"));
        Assert.Contains("healer", ViableStyles("Lux"));
        for (int seed = 0; seed < 100; seed++)
        {
            var plan = SurpriseBuilder.Plan(Data.Champion("Jinx")!, Data.Items, seed, kit: Kits.For(222));
            Assert.NotNull(plan);
            Assert.DoesNotContain(plan!.Theme.Key, new[] { "ap", "battlemage", "healer" });
        }
    }

    [Fact]
    public void Better_fits_are_picked_more_often_and_blocked_styles_never()
    {
        var ezreal = Data.Champion("Ezreal")!;
        var kit = Kits.For(81);
        var counts = Enumerable.Range(0, 400).Select(seed => SurpriseBuilder.Plan(ezreal, Data.Items, seed, kit: kit)?.Theme.Key)
            .GroupBy(k => k).ToDictionary(g => g.Key ?? "", g => g.Count());
        Assert.True(counts.GetValueOrDefault("ap") > counts.GetValueOrDefault("tank"), string.Join(", ", counts)); // AP fits 4, tank 1

        var blocked = new HashSet<string> { SurpriseBuilder.BlockKey("Ezreal", "ap") };
        for (int seed = 0; seed < 100; seed++)
            Assert.NotEqual("ap", SurpriseBuilder.Plan(ezreal, Data.Items, seed, kit: kit, blocked: blocked)?.Theme.Key);
    }
}
