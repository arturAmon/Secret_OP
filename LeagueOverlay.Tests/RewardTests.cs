using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.History;
using LeagueOverlay.Core.Lcu;
using Xunit;

namespace LeagueOverlay.Tests;

public class RewardTests
{
    private static int _minute;

    private static BuildStep Step(int id) => new(0, id, "", null, null, null, [], [], StepMatch.NoSuggestion);

    private static GameRecord Game(string theme, int[] built, string result = "Win", double minutes = 25, string account = "Me#EUW", DateTime? at = null)
    {
        var me = new RecordedPlayer(account, "Me", "Garen", "TOP", true, true, 16, 5, 3, 7, 200, 12000, built);
        var plan = new SurpriseRecord($"{theme} Garen", theme, [3031, 3036, 3072, 3094, 3046], 3006, null);
        return new GameRecord(at ?? new DateTime(2026, 11, 1).AddMinutes(++_minute * 60), "CLASSIC", result, minutes * 60, "Garen", "Garen", "TOP", 0, 0, [],
            [me], built.Select(Step).ToList(), [], 0, new HistorySnapshot([], [], [], [], 0), Surprise: plan);
    }

    [Fact]
    public void Only_surprise_builds_really_played_in_real_games_count()
    {
        Assert.True(Rewards.Counts(Game("tank", [3031, 3006, 3071])));
        Assert.False(Rewards.Counts(Game("tank", [3031, 3071])));              // only one planned item
        Assert.False(Rewards.Counts(Game("tank", [3031, 3036], minutes: 4)));  // remake
        Assert.False(Rewards.Counts(Game("tank", [3031, 3036]) with { Surprise = null }));
    }

    [Fact]
    public void Theme_and_count_achievements_unlock_their_cosmetics_once()
    {
        var p = new RewardProgress();
        var tank = Game("tank", [3031, 3036], result: "Lose");
        var unlocked = Rewards.Record(p, tank);
        Assert.Equal(["first", "tank"], unlocked.Select(a => a.Id));
        Assert.Empty(Rewards.Record(p, tank)); // the same game never counts twice
        Assert.Equal(1, p.SurpriseGames);
        Assert.True(Rewards.IsUnlocked(p, CosmeticKind.Border, "rivets"));
        Assert.False(Rewards.IsUnlocked(p, CosmeticKind.Background, "arcane"));
        Assert.True(Rewards.IsUnlocked(p, CosmeticKind.Background, "default"));

        Assert.Contains(Rewards.Record(p, Game("ap", [3031, 3036])), a => a.Id == "ap" );
        Assert.Contains("win", p.Unlocked.Keys);
        for (int i = 0; i < 3; i++) Rewards.Record(p, Game("ap", [3031, 3036]));
        Assert.Equal(5, p.SurpriseGames);
        Assert.True(Rewards.IsUnlocked(p, CosmeticKind.Border, "neon"));
        Assert.Equal("ten", Rewards.NextMilestone(p)!.Id);
    }

    [Fact]
    public void Locked_styles_fall_back_to_the_default_and_backfill_runs_once()
    {
        var p = new RewardProgress { Style = new CardStyle("arcane", "rivets", "pink", "crown") };
        Assert.Equal(CardStyle.Default, Rewards.Effective(p));

        var games = new[] { Game("ap", [3031, 3036]), Game("tank", [3031, 3036]), Game("crit", [3071]) };
        Rewards.Backfill(p, games);
        Assert.Equal(2, p.SurpriseGames);
        Assert.Empty(Rewards.Backfill(p, games));
        Assert.Equal(new CardStyle("arcane", "rivets", "pink", "none"), Rewards.Effective(p));
    }

    [Fact]
    public void Reward_files_are_per_account()
    {
        var root = Path.Combine(Path.GetTempPath(), "lo-rewards-" + Guid.NewGuid());
        try
        {
            var store = new RewardStore(root);
            var p = new RewardProgress();
            Rewards.Record(p, Game("tank", [3031, 3036]));
            p.Style = new CardStyle(Border: "rivets");
            store.Save("Me#EUW", p);
            var back = store.Load("me #euw");
            Assert.Equal(1, back.SurpriseGames);
            Assert.Equal("rivets", back.Style.Border);
            Assert.Equal(0, store.Load("Other#EUW").SurpriseGames);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Game_history_is_per_account_and_keeps_ten_games_each()
    {
        var root = Path.Combine(Path.GetTempPath(), "lo-games-" + Guid.NewGuid());
        try
        {
            var store = new GameRecordStore(root);
            var start = new DateTime(2026, 1, 1);
            for (int i = 0; i < 12; i++) store.Save(Game("ap", [3031], account: "Me#EUW", at: start.AddHours(i)));
            for (int i = 0; i < 3; i++) store.Save(Game("ap", [3031], account: "Smurf#EUW", at: start.AddHours(20 + i)));

            store.Account = "Me#EUW";
            var mine = store.LoadRecent();
            Assert.Equal(GameRecordStore.Keep, mine.Count);
            Assert.All(mine, g => Assert.Equal("Me#EUW", g.Me!.Key));
            Assert.Equal(start.AddHours(11), mine[0].EndedAt);
            store.Account = "smurf#euw";
            Assert.Equal(3, store.LoadRecent().Count);
            store.Account = null;
            Assert.Equal(GameRecordStore.Keep, store.LoadRecent().Count);
            Assert.Equal(13, Directory.GetFiles(root, "*.json").Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Client_account_comes_from_the_current_summoner()
    {
        Assert.Equal("Ramen25#Ramen", LcuClient.ParseAccount("""{"gameName":"Ramen25","tagLine":"Ramen","summonerId":1}"""));
        Assert.Null(LcuClient.ParseAccount("""{"displayName":"Old","summonerId":1}"""));
        Assert.Null(LcuClient.ParseAccount("not json"));
    }
}
