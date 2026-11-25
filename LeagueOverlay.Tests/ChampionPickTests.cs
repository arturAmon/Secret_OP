using System.Text.Json;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Lcu;
using static LeagueOverlay.Tests.Fixtures;

namespace LeagueOverlay.Tests;

public class ChampionPickTests
{
    private static readonly StaticData Picks;
    static ChampionPickTests()
    {
        using var f = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "meraki_sample.json"));
        Picks = new StaticData(Data.Version, Data.Champions, Data.Items, Data.Runes) { Kits = ChampionKits.ParseMeraki(f) };
    }

    private const int Garen = 86, Darius = 122, Malphite = 54, Jinx = 222, Lux = 99, Ahri = 103, Zed = 238, Leona = 89, Caitlyn = 51, Soraka = 16;

    /// <summary>A collected match: team one = first five, positions top..support; team one won or lost.</summary>
    private static BuildRecord[] Match(int[] blue, int[] red, bool blueWon)
    {
        string[] pos = ["TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY"];
        return blue.Select((c, i) => new BuildRecord(c, pos[i], blueWon, [], 0))
            .Concat(red.Select((c, i) => new BuildRecord(c, pos[i], !blueWon, [], 0))).ToArray();
    }

    [Fact]
    public void Matchup_stats_recover_teams_lanes_and_synergy_from_match_order()
    {
        var matches = new List<BuildRecord[]>();
        for (int i = 0; i < 10; i++) matches.Add(Match([Malphite, 1, Ahri, Jinx, Leona], [Darius, 2, Zed, Caitlyn, Soraka], blueWon: i < 7));
        var s = MatchupStats.From(matches);
        Assert.Equal(10, s.Matches);
        Assert.Equal(new MatchupStats.Record(10, 7), s.Lane(Malphite, "TOP", Darius));
        Assert.Equal(new MatchupStats.Record(10, 3), s.Lane(Darius, "TOP", Malphite));
        Assert.Equal(new MatchupStats.Record(10, 7), s.With(Jinx, Leona));
        Assert.Equal(new MatchupStats.Record(10, 7), s.Versus(Ahri, Caitlyn));
        Assert.Equal(0, s.Lane(Malphite, "TOP", Zed).Games); // not in the same lane
        Assert.True(s.Lane(Malphite, "TOP", Darius).Smoothed() < 0.7); // pulled towards 50%
    }

    private static PickContext Ctx(string? pos, int[] allies, int[] enemies, IReadOnlyDictionary<int, ChampMastery>? mastery = null, int[]? bans = null, int[]? pickable = null) =>
        new(pos, allies.Select(a => new PickSlot(a, null)).ToList(), enemies.Select(e => new PickSlot(e, null)).ToList(),
            allies.Concat(enemies).Concat(bans ?? []).ToHashSet(), pickable?.ToHashSet(), mastery);

    [Fact]
    public void Best_picks_favour_good_lane_matchups_and_what_the_team_lacks()
    {
        // Malphite beats Darius top; Garen loses to him. Both play top.
        var matches = new List<BuildRecord[]>();
        for (int i = 0; i < 30; i++) matches.Add(Match([Malphite, 1, Ahri, Jinx, Leona], [Darius, 2, Zed, Caitlyn, Soraka], blueWon: i < 22));
        for (int i = 0; i < 30; i++) matches.Add(Match([Garen, 1, Ahri, Jinx, Leona], [Darius, 2, Zed, Caitlyn, Soraka], blueWon: i < 9));
        var stats = MatchupStats.From(matches);

        var best = ChampionRecommender.Best(Ctx("TOP", [Jinx, Zed], [Darius]), Picks, stats);
        Assert.Equal("Malphite", best[0].Name);
        Assert.Contains(best[0].Reasons, r => r.StartsWith("Good into Darius"));
        // Without any games: what the team lacks decides (two physical allies, no frontline -> magic tank).
        var comp = ChampionRecommender.Best(Ctx("TOP", [Jinx, Zed], []), Picks, MatchupStats.Empty);
        Assert.Equal("Malphite", comp[0].Name);
        Assert.Equal(["Your team needs magic damage", "Your team has no frontline yet"], comp[0].Reasons);
        var garen = best.FirstOrDefault(p => p.Name == "Garen");
        Assert.True(garen is null || garen.Reasons.Any(r => r.StartsWith("Careful")));

        // Champions you can't play, or banned, are never suggested.
        best = ChampionRecommender.Best(Ctx("TOP", [], [Darius], bans: [Malphite]), Picks, stats);
        Assert.DoesNotContain(best, p => p.Name == "Malphite");
        best = ChampionRecommender.Best(Ctx("TOP", [], [Darius], pickable: [Garen, Darius]), Picks, stats);
        Assert.All(best, p => Assert.Equal("Garen", p.Name));
    }

    [Fact]
    public void Surprise_pick_is_a_low_mastery_champion_for_the_role_and_stays_put()
    {
        var mastery = new Dictionary<int, ChampMastery> { [Jinx] = new(7, 250_000), [Caitlyn] = new(5, 60_000) };
        var ctx = Ctx("BOTTOM", [], [], mastery);
        for (int seed = 0; seed < 30; seed++)
        {
            var pick = ChampionRecommender.Surprise(ctx, Picks, null, seed)!;
            Assert.NotEqual("Jinx", pick.Name);
            Assert.NotEqual("Caitlyn", pick.Name);
            Assert.StartsWith("You've never played", pick.Reasons[0]);
        }
        // Any role: off-meta champions come up too.
        Assert.Contains(Enumerable.Range(0, 60).Select(seed => ChampionRecommender.Surprise(ctx, Picks, null, seed)!.Key),
            k => Picks.Kits.For(k)?.PlaysIn("BOTTOM") != true);
        var first = ChampionRecommender.Surprise(ctx, Picks, null, 1)!;
        Assert.Equal(first.Key, ChampionRecommender.Surprise(ctx, Picks, null, 999, current: first.Key)!.Key); // kept
    }

    [Fact]
    public void Surprise_pick_options_widen_or_narrow_the_pool()
    {
        var mastery = new Dictionary<int, ChampMastery> { [Jinx] = new(7, 250_000), [Caitlyn] = new(5, 60_000) };
        var ctx = Ctx("BOTTOM", [], [], mastery);
        IEnumerable<int> Picked(SurprisePickOptions o) =>
            Enumerable.Range(0, 300).Select(seed => ChampionRecommender.Surprise(ctx, Picks, null, seed, options: o)!.Key);

        // Mains included and every champion equally likely: high mastery champions come up too.
        var any = Picked(new(AnyMastery: true, Even: true)).ToList();
        Assert.Contains(Jinx, any);
        Assert.Contains(Caitlyn, any);
        // Only your role: nothing off-meta.
        Assert.All(Picked(new(OnlyMyRole: true)), k => Assert.True(Picks.Kits.For(k)!.PlaysIn("BOTTOM")));
        // A former main is mentioned without "only".
        var jinx = ChampionRecommender.Surprise(ctx, Picks, null, 0, current: Jinx, options: new(AnyMastery: true))!;
        Assert.Equal("250,000 mastery points on Jinx", jinx.Reasons[0].Replace(' ', ',').Replace('.', ','));
    }

    [Fact]
    public void Champion_select_session_gives_allies_enemies_bans_and_your_pick_action()
    {
        using var doc = JsonDocument.Parse("""
            { "localPlayerCellId": 2,
              "myTeam": [ { "cellId": 1, "championId": 222, "assignedPosition": "bottom" }, { "cellId": 2, "championId": 0, "championPickIntent": 0, "assignedPosition": "top" } ],
              "theirTeam": [ { "cellId": 6, "championId": 122, "assignedPosition": "" }, { "cellId": 7, "championId": 0 } ],
              "bans": { "myTeamBans": [238], "theirTeamBans": [] },
              "actions": [ [ { "id": 5, "actorCellId": 6, "type": "ban", "completed": true, "championId": 103 } ],
                           [ { "id": 9, "actorCellId": 2, "type": "pick", "completed": false, "championId": 0 } ] ] }
            """);
        var s = LcuClient.ParseSession(doc.RootElement)!;
        Assert.Equal(0, s.ChampionId);
        Assert.Equal("TOP", LcuClient.NormalizePosition(s.Position));
        Assert.Equal([new PickSlot(222, "BOTTOM")], s.Allies);
        Assert.Equal([new PickSlot(122, null), new PickSlot(0, null)], s.Enemies);
        Assert.Equal([103, 238], s.Bans);
        Assert.Equal(9, s.PickActionId);
    }
}
