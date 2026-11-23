using System.Text.Json;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.History;

namespace LeagueOverlay.Tests;

public class RivalTests
{
    private static RecordedPlayer P(string id, string champ, bool myTeam, int k, int d, int a, int cs, int gold, double vision, bool me = false) =>
        new(id, champ, champ, "BOTTOM", myTeam, me, 14, k, d, a, cs, gold, [], vision);

    private static readonly List<RecordedPlayer> Players =
    [
        P("Me#EUW", "Jinx", true, 10, 2, 5, 250, 14000, 20, me: true),
        P("Duo Friend#EUW", "Thresh", true, 2, 4, 15, 40, 9000, 70),
        P("Enemy#EUW", "Caitlyn", false, 6, 10, 2, 220, 12000, 15),
    ];

    [Fact]
    public void Duel_compares_live_stats_and_skips_unknown_ones()
    {
        var duel = RivalDuel.Compare(Players, "duofriend#euw", minutes: 25)!; // case and spaces don't matter
        Assert.Equal("Thresh", duel.Rival.Name);
        Assert.True(duel.SameTeam);
        Assert.False(duel.HasMatchHistory);
        Assert.DoesNotContain(duel.Stats, s => s.Label == "Turret plates"); // not known without match history

        DuelStat S(string label) => duel.Stats.Single(s => s.Label == label);
        Assert.Equal(1, S("Kills").Winner);
        Assert.Equal(1, S("Deaths").Winner);           // fewer deaths wins
        Assert.Equal(-1, S("Vision score").Winner);
        Assert.Equal(15 / 12.0, S("Kill participation").Mine!.Value, 3); // (10 + 5) / team kills 12
        Assert.Equal(duel.Stats.Count(s => s.Winner > 0), duel.MyWins);
        Assert.Null(RivalDuel.Compare(Players, "Nobody#EUW", 25));
    }

    private static string Match(int duration = 1700) => $$"""
        { "metadata": { "matchId": "EUW1_1" }, "info": { "gameDuration": {{duration}}, "participants": [
          { "riotIdGameName": "Me", "riotIdTagline": "EUW", "championName": "Jinx", "win": true, "kills": 11, "deaths": 2, "assists": 6,
            "totalMinionsKilled": 240, "neutralMinionsKilled": 12, "goldEarned": 15500, "totalDamageDealtToChampions": 30000,
            "damageDealtToBuildings": 9000, "visionScore": 22, "wardsPlaced": 9, "wardsKilled": 3, "visionWardsBoughtInGame": 2,
            "largestMultiKill": 3, "champLevel": 16, "challenges": { "killParticipation": 0.61, "turretPlatesTaken": 4, "soloKills": 2 } },
          { "riotIdGameName": "Duo Friend", "riotIdTagline": "EUW", "championName": "Thresh", "win": true, "kills": 2, "deaths": 5, "assists": 17,
            "totalMinionsKilled": 38, "neutralMinionsKilled": 0, "goldEarned": 9800, "totalDamageDealtToChampions": 9000,
            "damageDealtToBuildings": 1200, "visionScore": 85, "wardsPlaced": 40, "wardsKilled": 12, "visionWardsBoughtInGame": 9,
            "largestMultiKill": 1, "champLevel": 13, "challenges": { "killParticipation": 0.7, "turretPlatesTaken": 1, "soloKills": 0 } } ] } }
        """;

    [Fact]
    public void Match_history_adds_full_stats_and_overrides_live_numbers()
    {
        using var doc = JsonDocument.Parse(Match());
        var post = PostGameStats.Parse(doc.RootElement);
        var me = post.Find("me#euw")!;
        Assert.Equal(252, me.Cs);
        Assert.Equal(4, me.TurretPlates);
        Assert.Equal(0.61, me.KillParticipation, 3);

        var duel = RivalDuel.Compare(Players, "Duo Friend#EUW", 28, post)!;
        Assert.True(duel.HasMatchHistory);
        DuelStat S(string label) => duel.Stats.Single(s => s.Label == label);
        Assert.Equal(11, S("Kills").Mine);              // final numbers replace the last live read
        Assert.Equal((4.0, 1.0), (S("Turret plates").Mine!.Value, S("Turret plates").Theirs!.Value));
        Assert.Equal(15500, S("Gold earned").Mine);
        Assert.Equal(-1, S("Wards placed").Winner);
    }

    [Fact]
    public void Extra_match_history_stats_timeline_and_titles()
    {
        const string match = """
            { "metadata": { "matchId": "EUW1_2" }, "info": { "gameDuration": 1800, "participants": [
              { "participantId": 1, "riotIdGameName": "Me", "riotIdTagline": "EUW", "championName": "Jinx", "teamId": 100, "teamPosition": "BOTTOM",
                "win": true, "kills": 14, "deaths": 3, "assists": 6, "totalDamageDealtToChampions": 42000, "totalDamageTaken": 15000,
                "quadraKills": 1, "tripleKills": 1, "objectivesStolen": 1, "firstBloodKill": true, "allInPings": 3, "onMyWayPings": 4,
                "totalHealsOnTeammates": 0, "totalDamageShieldedOnTeammates": 0, "timeCCingOthers": 12, "totalTimeSpentDead": 45,
                "challenges": { "dancedWithRiftHerald": 1, "dragonTakedowns": 3, "laneMinionsFirst10Minutes": 80, "jungleCsBefore10Minutes": 4 } },
              { "participantId": 2, "riotIdGameName": "Enemy", "riotIdTagline": "EUW", "championName": "Caitlyn", "teamId": 200, "teamPosition": "BOTTOM",
                "win": false, "kills": 4, "deaths": 9, "assists": 2, "totalDamageDealtToChampions": 21000, "totalDamageTaken": 22000,
                "timeCCingOthers": 30, "totalTimeSpentDead": 120, "challenges": { "dragonTakedowns": 1 } } ] } }
            """;
        const string timeline = """
            { "info": { "frames": [
              { "participantFrames": { "1": { "totalGold": 500, "minionsKilled": 0, "jungleMinionsKilled": 0, "xp": 0 },
                                       "2": { "totalGold": 500, "minionsKilled": 0, "jungleMinionsKilled": 0, "xp": 0 } } },
              { "participantFrames": { "1": { "totalGold": 900, "minionsKilled": 7, "jungleMinionsKilled": 1, "xp": 400 },
                                       "2": { "totalGold": 800, "minionsKilled": 5, "jungleMinionsKilled": 0, "xp": 380 } } } ] } }
            """;
        using var m = JsonDocument.Parse(match);
        using var t = JsonDocument.Parse(timeline);
        var post = PostGameStats.Parse(m.RootElement).WithTimeline(m.RootElement, t.RootElement);
        var me = post.Find("Me#EUW")!;
        Assert.Equal(PostGameStats.CurrentFormat, post.Format);
        Assert.Equal(7, me.Pings);
        Assert.Equal(84, me.CsAt10);
        Assert.Equal([500, 900], me.GoldByMinute!);
        Assert.Equal(8, me.CsAt(1));
        Assert.Equal("Caitlyn", post.Opponent(me)!.Champion);

        var players = new List<RecordedPlayer> { P("Me#EUW", "Jinx", true, 14, 3, 6, 200, 15000, 20, me: true), P("Enemy#EUW", "Caitlyn", false, 4, 9, 2, 180, 11000, 15) };
        var record = new GameRecord(DateTime.Now, "CLASSIC", "Win", 1800, "Jinx", "Jinx", "BOTTOM", 0, 0, [], players, [], [], 0,
            new HistorySnapshot([], [], [], [], 0), PostGame: post);
        var titles = Awards.For(record).Select(a => a.Title).ToList();
        Assert.Contains("Four of a Kind", titles);          // the quadra wins over the triple
        Assert.DoesNotContain("Hat Trick", titles);
        Assert.Contains("Smite Thief", titles);
        Assert.Contains("Damage Dealer Deluxe", titles);
        Assert.Contains("Dragon Tamer", titles);
        Assert.Contains("Early Bird", titles);
        Assert.Contains("Dance Partner", titles);
        Assert.DoesNotContain("Crowd Control Freak", titles); // Caitlyn had more

        var duel = RivalDuel.Compare(players, "Enemy#EUW", 30, post)!;
        Assert.Equal(1, duel.Stats.Single(s => s.Label == "Time spent dead").Winner); // less is better
        Assert.Equal("45s", duel.Stats.Single(s => s.Label == "Time spent dead").Show(45));
    }

    [Fact]
    public void A_saved_game_is_matched_by_champion_and_length()
    {
        var record = new GameRecord(DateTime.Now, "CLASSIC", "Unknown", 1690, "Jinx", "Jinx", "BOTTOM", 0, 0, [], Players, [], [], 0,
            new HistorySnapshot([], [], [], [], 0));
        using var same = JsonDocument.Parse(Match(1700));
        using var other = JsonDocument.Parse(Match(2400));
        Assert.True(PostGameFetcher.IsSameGame(same.RootElement, record));
        Assert.False(PostGameFetcher.IsSameGame(other.RootElement, record));
        Assert.False(PostGameFetcher.IsSameGame(same.RootElement, record with { ChampionKey = "Ezreal" }));
    }
}
