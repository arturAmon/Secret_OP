using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.History;

namespace LeagueOverlay.Tests;

public class InsightTests
{
    private static RecordedPlayer P(string name, string pos, bool myTeam, int k, int d, int a, int cs, int gold, double vision = 10, int level = 14, bool me = false) =>
        new(name, name, name, pos, myTeam, me, level, k, d, a, cs, gold, [], vision);

    private static GameRecord Game(List<RecordedPlayer> players, string result = "Win", double minutes = 30, List<GoldSample>? gold = null, int my = 50000, int enemy = 48000) =>
        new(DateTime.Now, "CLASSIC", result, minutes * 60, "Jinx", "Jinx", "BOTTOM", my, enemy, [], players, [], [], 0,
            new HistorySnapshot([], gold ?? [], [], [], 0));

    [Fact]
    public void Lane_matchup_pairs_the_same_position()
    {
        var players = new List<RecordedPlayer>
        {
            P("Jinx", "BOTTOM", true, 5, 1, 5, 240, 12000, me: true),
            P("Leona", "UTILITY", true, 1, 3, 12, 30, 7000),
            P("Caitlyn", "BOTTOM", false, 2, 4, 2, 210, 10500, level: 13),
            P("Soraka", "UTILITY", false, 0, 5, 9, 20, 6500),
        };
        var lane = LaneMatchup.Find(players, 30)!;
        Assert.Equal("Caitlyn", lane.Opponent.Name);
        Assert.Equal(1500, lane.GoldDiff);
        Assert.Equal(1.0, lane.CsPerMinDiff, 3);
        Assert.Equal(10 - 1, lane.KdaDiff, 3);      // (5+5)/1 vs (2+2)/4
        Assert.Equal(1, lane.LevelDiff);
        Assert.Null(LaneMatchup.Find([P("Jinx", "", true, 0, 0, 0, 0, 0, me: true)], 10));
    }

    [Fact]
    public void Titles_celebrate_what_went_well()
    {
        var players = new List<RecordedPlayer>
        {
            P("Jinx", "BOTTOM", true, 12, 0, 6, 260, 15000, vision: 20, me: true),
            P("Thresh", "UTILITY", true, 1, 4, 15, 40, 8000, vision: 80),
            P("Caitlyn", "BOTTOM", false, 3, 6, 2, 200, 11000),
        };
        var gold = new List<GoldSample> { new(600, 5000, 9000), new(1800, 50000, 48000) }; // 4k behind at 10 min
        var titles = Awards.For(Game(players, gold: gold)).Select(a => a.Title).ToList();

        Assert.Contains("Champ Farmer", titles);              // most kills
        Assert.Contains("CS Machine", titles);                // most farm
        Assert.Contains("Allergic to the Gray Screen", titles); // no deaths
        Assert.Contains("Landlord of the Lane", titles);      // 4k gold over Caitlyn
        Assert.Contains("Comeback Kid", titles);
        Assert.Contains("Nexus Demolition Crew", titles);
        Assert.DoesNotContain("Light in the Darkness", titles); // Thresh had more vision
        Assert.Equal("Comeback Kid", Awards.For(Game(players, gold: gold))[0].Title); // highest points first
    }

    [Fact]
    public void There_is_always_at_least_one_title()
    {
        var players = new List<RecordedPlayer>
        {
            P("Jinx", "BOTTOM", true, 0, 9, 1, 100, 6000, vision: 5, level: 11, me: true),
            P("Caitlyn", "BOTTOM", false, 12, 0, 3, 250, 15000, vision: 30, level: 16),
        };
        var awards = Awards.For(Game(players, result: "Lose"));
        Assert.Equal("Brave Summoner", Assert.Single(awards).Title);
        Assert.True(Awards.Score(awards) > 0);
    }
}
