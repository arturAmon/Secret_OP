using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Live;
using static LeagueOverlay.Tests.Fixtures;

namespace LeagueOverlay.Tests;

public class StackTests
{
    private static GameEvent Kill(double t, string killer, string victim, params string[] assisters) =>
        new() { EventName = "ChampionKill", EventTime = t, KillerName = killer, VictimName = victim, Assisters = assisters.ToList() };

    private static LiveGame Ahri(double time, int[] items, params GameEvent[] events)
    {
        var g = Game(P("Ahri", "ORDER", "MIDDLE", 11, items), 1200, P("Zed", "CHAOS", "MIDDLE", 11), P("Lux", "ORDER", "UTILITY", 11));
        g.GameData!.GameTime = time;
        g.Events = new EventList { Events = events.ToList() };
        return g;
    }

    [Fact]
    public void Glory_is_counted_from_takedowns_and_deaths_and_full_seal_suggests_mejais()
    {
        var tracker = new StackTracker();
        Assert.Single(tracker.Update(Ahri(300, [1082]), Data).Lines); // bought: Glory line appears

        var events = new List<GameEvent>();
        for (int i = 0; i < 6; i++) events.Add(Kill(400 + i, "Ahri", "Zed"));
        for (int i = 0; i < 6; i++) events.Add(Kill(500 + i, "Lux", "Zed", "Ahri"));   // assists count too
        events.Add(Kill(600, "Zed", "Ahri"));                                            // 10 (capped) - 5
        for (int i = 0; i < 4; i++) events.Add(Kill(700 + i, "Ahri", "Zed"));            // 9: not full yet
        var game = Ahri(800, [1082], events.ToArray());
        var report = tracker.Update(game, Data);
        Assert.Equal("≈ 9 / 10", report.Lines[0].Value);
        Assert.False(report.DarkSealFull);

        events.Add(Kill(810, "Ahri", "Zed"));
        report = tracker.Update(Ahri(820, [1082], events.ToArray()), Data);
        Assert.True(report.DarkSealFull);
        Assert.Equal("+40 ability power", report.Lines[0].Detail);

        var advice = ItemRecommender.Recommend(Ahri(820, [1082, 3020], events.ToArray()), Data, darkSealFull: true);
        Assert.Equal("Dark Seal is full: upgrade to Mejai's Soulstealer (1,150g)", advice.Reminder);
        Assert.Equal(StackTracker.MejaiId, advice.ReminderItemId);
    }

    [Fact]
    public void Veigar_stacks_are_his_ability_power_beyond_items_and_shards()
    {
        var champ = Data.Champion("Veigar")!;
        var me = P("Veigar", "ORDER", "MIDDLE", 13, 3089);                       // Rabadon's: 130 AP
        var stats = new ChampionStats { AbilityPower = 318 };
        var runes = new FullRunes { StatRunes = [new RuneRef { Id = 5008 }, new RuneRef { Id = 5008 }] }; // 2 × 9 AP
        var line = StackTracker.ChampionStack(champ, me, stats, runes, Data.Items, gloryAp: 0)!;
        Assert.Equal("≈ 170 stacks", line.Value);

        var senna = StackTracker.ChampionStack(Data.Champion("Senna")!, P("Senna", "ORDER", "UTILITY", 12),
            new ChampionStats { AttackRange = 725, CritChance = 0.18 }, null, Data.Items, 0)!;
        Assert.Equal("Range 725", senna.Value);
        Assert.Equal("+125 range · +18% crit from Mist", senna.Detail);
    }
}

public class SurpriseTests
{
    [Fact]
    public void Surprise_plan_avoids_the_usual_style_and_is_stable_per_seed()
    {
        var jinx = Data.Champion("Jinx")!;
        for (int seed = 0; seed < 30; seed++)
        {
            var plan = SurpriseBuilder.Plan(jinx, Data.Items, seed)!;
            Assert.NotEqual("crit", plan.Theme.Key);                    // Jinx is usually built crit
            Assert.All(plan.Path, id => Assert.All(plan.Theme.Required, s => Assert.True(Data.Items.Get(id)!.Get(s) > 0)));
            Assert.True(Data.Items.Get(plan.Boots)!.IsUpgradedBoots);
            Assert.Equal(plan, SurpriseBuilder.Plan(jinx, Data.Items, seed)); // same seed, same plan
        }
        Assert.True(Enumerable.Range(0, 30).Select(s => SurpriseBuilder.Plan(jinx, Data.Items, s)!.Theme.Key).Distinct().Count() > 1);
    }

    [Fact]
    public void Surprise_mode_follows_the_plan()
    {
        var plan = SurpriseBuilder.Plan(Data.Champion("Jinx")!, Data.Items, 7)!;
        var game = Game(P("Jinx", "ORDER", "BOTTOM", 9, plan.Path[0]), 0, P("Zed", "CHAOS", "MIDDLE", 9));
        var advice = ItemRecommender.Recommend(game, Data, surprise: plan);
        Assert.Equal(plan.Path[1], advice.Core!.ItemId);                 // first item owned: the second one is next
        Assert.StartsWith($"Surprise build: {plan.Title} · item 2 of", advice.Core.Reason);
        Assert.Contains(advice.Situational, r => r.ItemId == plan.Boots);
    }
}
