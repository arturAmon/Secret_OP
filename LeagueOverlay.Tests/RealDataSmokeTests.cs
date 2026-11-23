using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Data;
using Xunit.Abstractions;
using static LeagueOverlay.Tests.Fixtures;

namespace LeagueOverlay.Tests;

/// <summary>
/// Runs the recommender on the real current-patch Data Dragon data and prints the picks, so they can be
/// eyeballed. Asserts only basic sanity. Returns early (passes) when offline with nothing cached.
/// Run alone with: dotnet test --filter Category=RealData --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "RealData")]
public class RealDataSmokeTests(ITestOutputHelper output)
{
    private static StaticData? _data;

    private static async Task<StaticData?> LoadAsync()
    {
        if (_data is not null) return _data;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            return _data = await DataDragon.LoadAsync(http, DataDragon.DefaultCacheRoot);
        }
        catch { return null; }
    }

    // Real item ids: 3031 IE, 3006 Berserker's, 3089 Rabadon, 3157 Zhonya, 6655 Luden's, 3071 Cleaver, 3814 Edge of Night, 6692 Eclipse, 3075 Thornmail
    public static TheoryData<string, string, int, int[], string[]> Scenarios => new()
    {
        { "Jinx ADC vs Soraka + AP mid", "Jinx", 1800, [3006, 1038], ["Soraka:UTILITY", "Syndra:MIDDLE:3089", "Caitlyn:BOTTOM:3031"] },
        { "Ahri mid vs AD team", "Ahri", 3200, [3020, 6655], ["Zed:MIDDLE:3814:6692", "Darius:TOP:3071", "Caitlyn:BOTTOM:3031"] },
        { "Malphite top vs crit ADCs", "Malphite", 1400, [], ["Caitlyn:BOTTOM:3031:3006", "Jinx:BOTTOM:3031:3006", "Zed:MIDDLE:3814"] },
        { "Darius top vs tanks", "Darius", 900, [3047], ["Malphite:TOP:3075", "Leona:UTILITY:3075", "Syndra:MIDDLE:3089"] },
        { "Lux support vs AP", "Lux", 500, [], ["Syndra:MIDDLE:3089", "Annie:TOP:3157"] },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Prints_picks_for_realistic_games(string label, string champ, int gold, int[] myItems, string[] enemySpecs)
    {
        var data = await LoadAsync();
        if (data is null) { output.WriteLine("Data Dragon unavailable; skipped."); return; }

        var enemies = enemySpecs.Select(spec =>
        {
            var parts = spec.Split(':');
            return P(parts[0], "CHAOS", parts[1], 11, parts.Skip(2).Select(int.Parse).ToArray());
        }).ToArray();
        var game = Game(P(champ, "ORDER", "MIDDLE", 11, myItems), gold, enemies);

        var advice = ItemRecommender.Recommend(game, data);
        var threats = ItemRecommender.AnalyzeThreats(enemies, data);

        output.WriteLine($"== {label} (patch {data.Version}) ==");
        output.WriteLine($"threats: AP {threats.ApShare:P0}, tanks [{string.Join(", ", threats.Tanks)}], healers [{string.Join(", ", threats.Healers)}], crit [{string.Join(", ", threats.CritStackers)}]");
        output.WriteLine($"core: {advice.Core?.Name} ({advice.Core?.RemainingCost}g left) — {advice.Core?.Reason}");
        foreach (var s in advice.Situational) output.WriteLine($"situational: {s.Name} — {s.Reason}");
        output.WriteLine($"buy now: {advice.BuyNow}");

        Assert.NotNull(advice.Core);
        var ownedNames = myItems.Select(id => data.Items.Get(id)?.Name).ToHashSet();
        Assert.DoesNotContain(advice.Core!.Name, ownedNames);
        Assert.All(advice.Situational, s => Assert.DoesNotContain(s.Name, ownedNames));
    }

    /// <summary>Every game saved on this PC still loads with the current record format.</summary>
    [Fact]
    public void Saved_games_still_load()
    {
        var store = new Core.History.GameRecordStore(Core.History.GameRecordStore.DefaultRoot);
        int files = Directory.Exists(store.Root) ? Directory.GetFiles(store.Root, "*.json").Length : 0;
        var games = store.LoadRecent();
        foreach (var g in games)
            output.WriteLine($"{g.EndedAt:g} {g.ChampionName} {g.Result} titles: {string.Join(", ", Core.History.Awards.For(g).Select(a => a.Title))}");
        Assert.Equal(Math.Min(files, Core.History.GameRecordStore.Keep), games.Count);
    }

    /// <summary>Looks up saved games in Riot's match history (needs RIOT_API_KEY). Prints only; doesn't change the saved files.</summary>
    [Fact]
    public async Task Saved_games_are_found_in_match_history()
    {
        var key = Environment.GetEnvironmentVariable("RIOT_API_KEY");
        var games = new Core.History.GameRecordStore(Core.History.GameRecordStore.DefaultRoot).LoadRecent();
        if (string.IsNullOrEmpty(key) || games.Count == 0) { output.WriteLine("No key or no saved games; skipped."); return; }

        using var client = new Core.Builds.RiotClient(key, Core.Builds.RiotRegion.Find("EUW1"));
        foreach (var g in games)
        {
            var enriched = await Core.History.PostGameFetcher.TryAsync(g, client, CancellationToken.None);
            output.WriteLine($"{g.EndedAt:g} {g.ChampionName}: {(enriched is null ? "not found" : $"{enriched.PostGame!.MatchId}, result {g.Result} -> {enriched.Result}")}");
            if (enriched?.PostGame?.Find(g.Me!.Key) is { } me)
                output.WriteLine($"   {me.Kills}/{me.Deaths}/{me.Assists}, KP {me.KillParticipation:P0}, {me.Cs} cs, {me.GoldEarned:N0} gold earned, {me.TurretPlates} plates, vision {me.VisionScore}, {me.ChampionDamage:N0} dmg");
            if (enriched is not null)
            {
                var mate = g.Players.First(p => p.MyTeam && !p.IsMe);
                var duel = Core.History.RivalDuel.Compare(g.Players, mate.Key, g.Duration / 60, enriched.PostGame)!;
                output.WriteLine($"   as if {mate.Name} were the rival: you {duel.MyWins} - {duel.RivalWins} them over {duel.Stats.Count} stats");
            }
        }
    }

    /// <summary>Imports your recent games from match history (needs RIOT_API_KEY and a saved game to know who you are). Prints only.</summary>
    [Fact]
    public async Task Recent_games_import_from_match_history()
    {
        var key = Environment.GetEnvironmentVariable("RIOT_API_KEY");
        var data = await LoadAsync();
        var saved = new Core.History.GameRecordStore(Core.History.GameRecordStore.DefaultRoot).LoadRecent();
        if (string.IsNullOrEmpty(key) || data is null || saved.FirstOrDefault()?.Me?.Key is not { } me) { output.WriteLine("Skipped."); return; }

        using var client = new Core.Builds.RiotClient(key, Core.Builds.RiotRegion.Find("EUW1"));
        var games = await Core.History.MatchImporter.ImportRecentAsync(client, me, data, saved, 10, CancellationToken.None);
        output.WriteLine($"{games.Count} new games (saved games skipped: {saved.Count})");
        foreach (var g in games)
        {
            var m = g.Me!;
            output.WriteLine($"{g.EndedAt:g} {g.GameMode,-8} {g.ChampionName,-12} {g.Position,-8} {g.Result,-4} {g.Duration / 60:0}m  {m.Kills}/{m.Deaths}/{m.Assists}  " +
                             $"build {g.Build.Count} items, timeline {g.Timeline.Gold.Count} min / {g.Timeline.Items.Count} buys / {g.Timeline.Levels.Count} lvls, " +
                             $"titles: {string.Join(", ", Core.History.Awards.For(g).Take(3).Select(a => a.Title))}");
            Assert.True(g.Imported);
            Assert.Equal(10, g.Players.Count);
        }
    }

    /// <summary>Uses whatever popular builds the app has collected on this PC (EUW1) and prints the most-played champions' paths.</summary>
    [Fact]
    public async Task Prints_popular_paths_from_collected_games()
    {
        var data = await LoadAsync();
        var root = Core.Builds.BuildStore.DefaultRoot;
        if (data is null || !Directory.Exists(Path.Combine(root, "EUW1"))) { output.WriteLine("No collected builds; skipped."); return; }

        var prev = Core.Builds.BuildStore.PreviousPatch(data.Patch);
        var library = new Core.Builds.BuildLibrary(Core.Builds.BuildStore.Load(root, "EUW1", data.Patch),
            prev is null ? null : Core.Builds.BuildStore.Load(root, "EUW1", prev));
        var all = library.Current.Builds.Concat(library.Previous?.Builds ?? []).ToList();
        output.WriteLine($"{library.Current.MatchCount} games on {data.Patch}, {library.Previous?.MatchCount} on {prev}");

        foreach (var group in all.GroupBy(b => (b.Champion, b.Position)).OrderByDescending(g => g.Count()).Take(5))
        {
            var champ = data.ChampionByKey(group.Key.Champion);
            var build = library.For(group.Key.Champion, group.Key.Position);
            if (champ is null || build is null) continue;
            output.WriteLine($"== {champ.Name} {group.Key.Position}: {build.Games} games ({build.Patch}), {build.WinRate:P0} won");
            output.WriteLine("   path: " + string.Join(" > ", build.Path.Select((id, i) => $"{data.Items.Get(id)?.Name} {build.PathShare[i]:P0}")));
            output.WriteLine("   boots: " + string.Join(", ", build.BootsShare.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{data.Items.Get(kv.Key)?.Name} {kv.Value:P0}")));

            var me = P(champ.Id, "ORDER", group.Key.Position, 11);
            var game = Game(me, 1500, P("Zed", "CHAOS", "MIDDLE", 11, 3814), P("Darius", "CHAOS", "TOP", 11, 3071), P("Soraka", "CHAOS", "UTILITY", 11));
            var advice = ItemRecommender.Recommend(game, data, popular: build);
            output.WriteLine($"   overlay: NEXT {advice.Core?.Name} — {advice.Core?.Reason}");
            foreach (var s in advice.Situational) output.WriteLine($"            • {s.Name} — {s.Reason}");
            Assert.NotNull(advice.Core);
        }
    }
}
