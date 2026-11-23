using System.Text.Json;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Live;
using static LeagueOverlay.Tests.Fixtures;

namespace LeagueOverlay.Tests;

public class MatchParserTests
{
    // Two players: Jinx (participant 1) and Caitlyn (participant 2), in the real match-v5 shape.
    private const string Match = """
        { "info": { "queueId": 420, "gameDuration": 1900, "gameVersion": "16.23.840.6595", "participants": [
            { "participantId": 1, "championId": 222, "teamPosition": "BOTTOM", "win": true,  "roleBoundItem": 3006 },
            { "participantId": 2, "championId": 51,  "teamPosition": "BOTTOM", "win": false, "roleBoundItem": 0 } ] } }
        """;

    private const string Timeline = """
        { "info": { "frames": [
          { "events": [
            { "type": "ITEM_PURCHASED", "participantId": 0, "itemId": 3865, "timestamp": 0 },
            { "type": "ITEM_PURCHASED", "participantId": 1, "itemId": 1001, "timestamp": 10 },
            { "type": "ITEM_PURCHASED", "participantId": 2, "itemId": 3053, "timestamp": 20 },
            { "type": "ITEM_UNDO", "participantId": 2, "beforeId": 3053, "afterId": 0, "timestamp": 25 },
            { "type": "ITEM_PURCHASED", "participantId": 2, "itemId": 3047, "timestamp": 30 } ] },
          { "events": [
            { "type": "WARD_PLACED", "creatorId": 1, "timestamp": 600000 },
            { "type": "ITEM_PURCHASED", "participantId": 1, "itemId": 3031, "timestamp": 600100 },
            { "type": "ITEM_PURCHASED", "participantId": 1, "itemId": 2003, "timestamp": 600200 },
            { "type": "ITEM_PURCHASED", "participantId": 2, "itemId": 3174, "timestamp": 610000 },
            { "type": "ITEM_PURCHASED", "participantId": 2, "itemId": 3031, "timestamp": 620000 },
            { "type": "ITEM_PURCHASED", "participantId": 1, "itemId": 3033, "timestamp": 900000 },
            { "type": "ITEM_SOLD", "participantId": 1, "itemId": 3031, "timestamp": 950000 },
            { "type": "ITEM_PURCHASED", "participantId": 1, "itemId": 3031, "timestamp": 960000 } ] } ] } }
        """;

    [Fact]
    public void Builds_follow_purchase_order_with_undos_removed_and_boots_kept_apart()
    {
        using var m = JsonDocument.Parse(Match);
        using var t = JsonDocument.Parse(Timeline);
        Assert.Equal("16.23", MatchParser.UsablePatch(m.RootElement));

        var builds = MatchParser.Parse(m.RootElement, t.RootElement, Data.Items);
        var jinx = builds.Single(b => b.Champion == 222);
        Assert.Equal([3031, 3033], jinx.Items);    // potion skipped, re-bought IE counted once
        Assert.Equal(3006, jinx.Boots);            // only basic boots bought; Berserker's came from the role quest slot
        Assert.True(jinx.Win);
        Assert.Equal("BOTTOM", jinx.Position);

        var cait = builds.Single(b => b.Champion == 51);
        Assert.Equal([3031], cait.Items);          // undone Sterak's removed
        Assert.Equal(3047, cait.Boots);            // first tier-2 boots; the tier-3 upgrade maps back to it anyway
    }

    [Fact]
    public void Remakes_and_other_queues_are_not_used()
    {
        using var shortGame = JsonDocument.Parse(Match.Replace("1900", "400"));
        using var aram = JsonDocument.Parse(Match.Replace("420", "450"));
        Assert.Null(MatchParser.UsablePatch(shortGame.RootElement));
        Assert.Null(MatchParser.UsablePatch(aram.RootElement));
        Assert.Equal(3047, MatchParser.TierTwoBoots(3174, Data.Items));
        Assert.Null(MatchParser.TierTwoBoots(1001, Data.Items));
    }
}

public class BuildStatsTests
{
    private static BuildRecord B(string pos, params int[] items) => new(222, pos, true, items, 3006);

    [Fact]
    public void Path_takes_the_most_common_item_at_each_step()
    {
        var builds = new[]
        {
            B("BOTTOM", 3031, 3033, 3071), B("BOTTOM", 3031, 3033), B("BOTTOM", 3031, 3071, 3033), B("BOTTOM", 3071, 3031),
        };
        var stats = ChampionBuild.From(builds, "BOTTOM", "16.23")!;
        Assert.Equal([3031, 3033, 3071], stats.Path);
        Assert.Equal(0.75, stats.PathShare[0]);
        Assert.Equal(1.0, stats.Share(3031));
        Assert.Equal(1.0, stats.BootsShare[3006]);
    }

    [Fact]
    public void Library_prefers_role_then_falls_back_to_previous_patch_then_all_roles()
    {
        var root = Path.Combine(Path.GetTempPath(), "lo_builds_" + Guid.NewGuid().ToString("N"));
        try
        {
            var current = BuildStore.Load(root, "EUW1", "16.23");
            var previous = BuildStore.Load(root, "EUW1", "16.22");
            for (int i = 0; i < 8; i++) current.Add($"c{i}", [B("BOTTOM", 3031)]);
            for (int i = 0; i < 8; i++) previous.Add($"p{i}", [B("BOTTOM", 3033)]);
            for (int i = 0; i < 20; i++) current.Add($"s{i}", [B("UTILITY", 3089)]);
            var library = new BuildLibrary(current, previous);

            var bot = library.For(222, "BOTTOM")!;      // 8 + 8 in role beats 28 across all roles
            Assert.Equal("BOTTOM", bot.Position);
            Assert.Equal("16.23 + 16.22", bot.Patch);
            Assert.Equal(16, bot.Games);

            var mid = library.For(222, "MIDDLE")!;      // nobody plays it mid: all roles, current patch
            Assert.Null(mid.Position);
            Assert.Equal(28, mid.Games);
            Assert.Null(library.For(999, "TOP"));

            current.Save();
            Assert.Equal(28, BuildStore.Load(root, "EUW1", "16.23").MatchCount);
            Assert.Equal("16.22", BuildStore.PreviousPatch("16.23"));
            Assert.Null(BuildStore.PreviousPatch("17.1"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Rate_limiter_waits_until_the_window_has_room()
    {
        var now = new DateTime(2026, 1, 1);
        var limiter = new RateLimiter("10:1,20:10", () => now); // 90% margin: 9 per second, 18 per 10 seconds
        for (int i = 0; i < 9; i++) Assert.Equal(TimeSpan.Zero, limiter.TryAcquire());
        Assert.True(limiter.TryAcquire() > TimeSpan.Zero);

        now = now.AddSeconds(2);
        for (int i = 0; i < 9; i++) Assert.Equal(TimeSpan.Zero, limiter.TryAcquire());
        now = now.AddSeconds(2);
        Assert.True(limiter.TryAcquire() > TimeSpan.FromSeconds(5)); // the 10-second window is now the limit
    }
}

public class PopularRecommenderTests
{
    private static ChampionBuild Popular(int games = 100) => new(
        games, 0.52, [3031, 3071, 3033], [0.8, 0.4, 0.3],
        new Dictionary<int, double> { [3031] = 0.9, [3071] = 0.5, [3033] = 0.35, [3143] = 0.01 },
        new Dictionary<int, double> { [3006] = 0.7, [3047] = 0.2, [3111] = 0.1 },
        "BOTTOM", "16.23");

    private static LiveGame Game(params int[] myItems) =>
        Fixtures.Game(P("Jinx", "ORDER", "BOTTOM", 11, myItems), 0,
            P("Zed", "CHAOS", "MIDDLE", 11, 3814), P("Caitlyn", "CHAOS", "BOTTOM", 11, 3031), P("Darius", "CHAOS", "TOP", 11, 3071));

    [Fact]
    public void Next_item_follows_the_popular_path()
    {
        var advice = ItemRecommender.Recommend(Game(3031, 3006), Data, popular: Popular());
        Assert.Equal("Black Cleaver", advice.Core!.Name);     // 2nd step; Infinity Edge already owned
        Assert.Equal("Most common 2nd item · 40% of 100 games", advice.Core.Reason);
    }

    [Fact]
    public void Boots_follow_popularity_but_switch_for_threats_when_that_is_common_too()
    {
        // Mostly AD enemies: Steelcaps fits the threat and 20% of players buy them, so they win over Berserker's.
        var advice = ItemRecommender.Recommend(Game(3031), Data, popular: Popular());
        var boots = advice.Situational.First(r => Data.Items.Get(r.ItemId)!.IsBoots);
        Assert.Equal("Plated Steelcaps", boots.Name);
        Assert.Contains("20% pick these", boots.Reason);

        var rareSteelcaps = Popular() with { BootsShare = new Dictionary<int, double> { [3006] = 0.95, [3047] = 0.02 } };
        var advice2 = ItemRecommender.Recommend(Game(3031), Data, popular: rareSteelcaps);
        Assert.Equal("Berserker's Greaves", advice2.Situational.First(r => Data.Items.Get(r.ItemId)!.IsBoots).Name);
    }

    [Fact]
    public void Situational_items_nobody_builds_on_the_champion_are_dropped()
    {
        var advice = ItemRecommender.Recommend(Game(3031, 3006), Data, popular: Popular());
        Assert.DoesNotContain(advice.Situational, r => r.Name == "Randuin's Omen"); // 1% on this champion
    }

    [Fact]
    public void Too_few_games_falls_back_to_rules()
    {
        var advice = ItemRecommender.Recommend(Game(3031, 3006), Data, popular: Popular(games: 5));
        Assert.DoesNotContain("Most common", advice.Core!.Reason);
    }
}
