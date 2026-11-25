using System.Text.Json;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.History;
using LeagueOverlay.Core.Lcu;
using static LeagueOverlay.Tests.Fixtures;

namespace LeagueOverlay.Tests;

public class RuneTests
{
    /// <summary>Rune trees and end-of-game stat labels of patch 16.23 (trimmed copies of runesReforged.json / perks.json).</summary>
    public static readonly RuneCatalog Runes = RuneCatalog.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "runesReforged.json")),
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "perks.json")));

    // Conqueror, Triumph, Legend: Alacrity, Last Stand | Domination: Taste of Blood, Treasure Hunter | shards
    private static readonly int[] ConquerorPage = [8010, 9111, 9104, 8299, 8139, 8135, 5008, 5008, 5011];

    private static string Perks(int[] p, int[]? vars = null)
    {
        vars ??= new int[18];
        string Sel(int i) => $$"""{ "perk": {{p[i]}}, "var1": {{vars[i * 3]}}, "var2": {{vars[i * 3 + 1]}}, "var3": {{vars[i * 3 + 2]}} }""";
        return $$"""
            "perks": { "statPerks": { "offense": {{p[6]}}, "flex": {{p[7]}}, "defense": {{p[8]}} },
              "styles": [ { "description": "primaryStyle", "style": 8000, "selections": [ {{Sel(0)}}, {{Sel(1)}}, {{Sel(2)}}, {{Sel(3)}} ] },
                          { "description": "subStyle", "style": 8100, "selections": [ {{Sel(4)}}, {{Sel(5)}} ] } ] }
            """;
    }

    [Fact]
    public void Catalog_reads_trees_shards_and_end_of_game_labels()
    {
        Assert.Equal(5, Runes.Styles.Count);
        Assert.Equal("Conqueror", Runes.Name(8010));
        Assert.Equal(0, Runes.Get(8010)!.Row);
        Assert.Contains(8005, Runes.Alternatives(8010)); // Press the Attack, same row
        Assert.DoesNotContain(8010, Runes.Alternatives(8010));
        Assert.Equal("Adaptive Force", Runes.Name(5008));

        Assert.Equal(new RuneStatLabel("Total healing", 1, RuneStatKind.Healing), Runes.MainStat(8010));
        Assert.Equal(RuneStatKind.Damage, Runes.MainStat(8112)!.Kind);           // Electrocute
        Assert.Equal(RuneStatKind.Shield, Runes.Labels(8214)[1].Kind);           // Aery: damage shielded
        Assert.Equal(RuneStatKind.Count, Runes.Labels(8128)[1].Kind);            // Dark Harvest souls
        Assert.Equal(RuneStatKind.Percent, Runes.Labels(8429)[0].Kind);          // Conditioning uptime
        Assert.Equal(RuneStatKind.Time, Runes.Labels(9104)[0].Kind);             // Legend completed at mm:ss
        Assert.Equal(RuneStatKind.Other, Runes.Labels(8226)[1].Kind);            // mana is not healing
    }

    [Fact]
    public void Match_runes_are_parsed_into_builds_and_post_game()
    {
        int[] vars = new int[18];
        vars[0] = 1500; // Conqueror healing
        string match = $$"""
            { "metadata": { "matchId": "EUW1_1" }, "info": { "queueId": 420, "gameDuration": 1800, "gameVersion": "16.23.1", "participants": [
              { "participantId": 1, "championId": 86, "championName": "Garen", "teamPosition": "TOP", "win": true, "teamId": 100,
                "riotIdGameName": "Me", "riotIdTagline": "EUW", {{Perks(ConquerorPage, vars)}} } ] } }
            """;
        using var m = JsonDocument.Parse(match);
        using var t = JsonDocument.Parse("""{ "info": { "frames": [] } }""");
        var build = MatchParser.Parse(m.RootElement, t.RootElement, Data.Items).Single();
        Assert.True(build.HasRunes);
        Assert.Equal(8000, RuneSetup.FromArray(build.Runes)!.Primary);
        Assert.Equal(ConquerorPage, RuneSetup.FromArray(build.Runes)!.Perks);
        Assert.Equal(1500, build.RuneVars![0]);
        Assert.Equal(1800, build.Duration);

        var post = PostGameStats.Parse(m.RootElement);
        Assert.Equal(4, post.Format);
        Assert.Equal(8010, post.Players[0].RunePage!.Keystone);
        Assert.Equal(1500, post.Players[0].RuneVar(0, 1));
    }

    [Fact]
    public void Builds_saved_before_runes_still_load_and_are_skipped_for_runes()
    {
        var old = JsonSerializer.Deserialize<BuildRecord>("""{ "c": 86, "p": "TOP", "w": true, "i": [3071], "b": 3047 }""")!;
        Assert.False(old.HasRunes);
        Assert.Null(RunePage.From([old], "TOP", "16.23"));
        var json = JsonSerializer.Serialize(old);
        Assert.DoesNotContain("\"r\"", json); // no nulls written for old records
    }

    private static BuildRecord Rec(int[] page, bool win, int keystoneVar = 0, int duration = 1800)
    {
        var vars = new int[18];
        vars[0] = keystoneVar;
        return new BuildRecord(86, "TOP", win, [], 0, new[] { 8000, 8100 }.Concat(page).ToArray(), vars, duration);
    }

    [Fact]
    public void Popular_page_is_the_most_common_page_with_the_most_common_keystone()
    {
        int[] grasp = [8437, 8446, 8429, 8451, 9111, 9104, 5008, 5008, 5011];
        int[] otherConq = [8010, 9111, 9105, 8299, 8139, 8135, 5008, 5008, 5011];
        var builds = Enumerable.Range(0, 5).Select(i => Rec(grasp, true))              // single most common page...
            .Concat(Enumerable.Range(0, 4).Select(i => Rec(ConquerorPage, i % 2 == 0))) // ...but Conqueror is the most common keystone
            .Concat(Enumerable.Range(0, 3).Select(i => Rec(otherConq, false))).ToList();
        var page = RunePage.From(builds, "TOP", "16.23")!;
        Assert.Equal(ConquerorPage, page.Setup.Perks);
        Assert.Equal(12, page.Games);
        Assert.Equal(0.5, page.WinRate);
    }

    [Fact]
    public void Surprise_runes_are_valid_pages_and_fit_the_theme()
    {
        foreach (var theme in SurpriseBuilder.Themes)
        for (int seed = 0; seed < 20; seed++)
        {
            var page = SurpriseBuilder.PickRunes(theme.Runes, Runes, new Random(seed))!;
            Assert.True(page.IsComplete);
            Assert.Contains(Runes.Name(page.Keystone), theme.Runes.Keystones);
            var primary = Runes.Style(page.Primary)!;
            for (int row = 0; row < 4; row++) Assert.Contains(page.Perks[row], primary.Rows[row]);
            var secondary = Runes.Style(page.Secondary)!;
            Assert.NotEqual(page.Primary, page.Secondary);
            int r1 = Runes.Get(page.Perks[4])!.Row, r2 = Runes.Get(page.Perks[5])!.Row;
            Assert.True(r1 > 0 && r2 > 0 && r1 != r2, theme.Key);
            Assert.All(new[] { page.Perks[4], page.Perks[5] }, id => Assert.Equal(secondary.Id, Runes.Get(id)!.Style));
            for (int i = 0; i < 3; i++) Assert.Contains(page.Perks[6 + i], RuneCatalog.ShardRows[i]);
        }
        // Every rune named in a recipe exists on this patch (a renamed rune would silently fall back to random).
        var names = SurpriseBuilder.Themes.SelectMany(t => t.Runes.Keystones.Concat(t.Runes.Prefer)).Distinct();
        Assert.All(names, n => Assert.True(Runes.ByName(n) is not null, $"unknown rune {n}"));
        Assert.All(SurpriseBuilder.Themes.SelectMany(t => t.Runes.Secondary).Distinct(), n => Assert.NotNull(Runes.StyleByName(n)));
    }

    [Fact]
    public void Surprise_plan_with_the_same_seed_gives_the_same_runes()
    {
        var champ = Data.Champion("Ahri")!;
        var a = SurpriseBuilder.Plan(champ, Data.Items, 42, Runes);
        var b = SurpriseBuilder.Plan(champ, Data.Items, 42, Runes);
        Assert.NotNull(a?.Runes);
        Assert.Equal(a!.Runes, b!.Runes);
    }

    private static (PostGamePlayer Me, PostGameStats Post) Game(int[] page, int keystoneVar, int enemyCc = 0)
    {
        var vars = new int[18];
        vars[0] = keystoneVar;
        var me = new PostGamePlayer("Me#EUW", "Garen", true, 5, 3, 7, 0.5, 200, 12000, 20000, 3000, 2, 20, 8, 2, 1, 1, 2, 16, TeamId: 100,
            Runes: new[] { 8000, 8100 }.Concat(page).ToArray(), RuneVars: vars);
        var enemy = new PostGamePlayer("Them#EUW", "Darius", false, 3, 5, 2, 0.4, 180, 10000, 18000, 1000, 0, 15, 6, 1, 1, 0, 1, 15, TeamId: 200, CcSeconds: enemyCc);
        return (me, new PostGameStats("EUW1_1", [me, enemy], PostGameStats.CurrentFormat));
    }

    [Fact]
    public void Analysis_compares_with_typical_values_and_suggests_only_clear_gains()
    {
        // Typical on Garen: Conqueror heals 40/min; Press the Attack (damage) can't be compared with healing;
        // Fleet Footwork heals 80/min.
        int[] fleet = [8021, 9111, 9104, 8299, 8139, 8135, 5008, 5008, 5011];
        var bench = RuneBenchmarks.From(Enumerable.Range(0, 25).Select(_ => Rec(ConquerorPage, true, 40 * 30))
            .Concat(Enumerable.Range(0, 25).Select(_ => Rec(fleet, true, 80 * 30))));

        var (me, post) = Game(ConquerorPage, 600); // 20/min in a 30 minute game
        var report = RuneAnalysis.Analyse(me, post, 30, Runes, bench, 86)!;
        var conq = report.Lines[0];
        Assert.Equal("Conqueror", conq.Name);
        Assert.Equal(0.5, conq.Ratio!.Value, 3);
        Assert.StartsWith("below usual", conq.Compare);
        Assert.Equal(8021, conq.SuggestedRune);
        Assert.Contains("Fleet Footwork usually gives about 2,400 healing", conq.Suggestion);
        Assert.True(report.HasBenchmarks);

        // A great Conqueror game (90/min) beats Fleet's typical value: no suggestion.
        (me, post) = Game(ConquerorPage, 90 * 30);
        conq = RuneAnalysis.Analyse(me, post, 30, Runes, bench, 86)!.Lines[0];
        Assert.StartsWith("well above usual", conq.Compare);
        Assert.Null(conq.Suggestion);

        // Without benchmarks: only what the runes did.
        report = RuneAnalysis.Analyse(me, post, 30, Runes, RuneBenchmarks.Empty, 86)!;
        Assert.False(report.HasBenchmarks);
        Assert.All(report.Lines, l => Assert.Null(l.Compare));
        Assert.Equal("Total healing", report.Lines[0].Stats[0].Label);
        Assert.Equal("2,700 (90/min)", report.Lines[0].Stats[0].Value);
    }

    [Fact]
    public void Analysis_notes_heavy_enemy_crowd_control_without_tenacity()
    {
        var (me, post) = Game(ConquerorPage, 0, enemyCc: 300);
        Assert.Contains(RuneAnalysis.Analyse(me, post, 30, Runes, null, 86)!.Notes, n => n.Contains("crowd control"));
        int[] tenacity = [8010, 9111, 9104, 8299, 8139, 8135, 5008, 5008, 5013];
        (me, post) = Game(tenacity, 0, enemyCc: 300);
        Assert.Empty(RuneAnalysis.Analyse(Game(ConquerorPage, 0, enemyCc: 180).Me, Game(ConquerorPage, 0, enemyCc: 180).Post, 32, Runes, null, 86)!.Notes); // normal amount
        Assert.Empty(RuneAnalysis.Analyse(me, post, 30, Runes, null, 86)!.Notes);
    }

    [Fact]
    public void Lockfile_and_champion_select_session_are_read()
    {
        Assert.Equal(new LcuCredentials(51234, "abc-DEF_1", "https"), LcuCredentials.Parse("LeagueClient:1234:51234:abc-DEF_1:https"));
        Assert.Null(LcuCredentials.Parse("garbage"));

        using var hovering = JsonDocument.Parse("""
            { "localPlayerCellId": 2, "myTeam": [ { "cellId": 1, "championId": 86 }, { "cellId": 2, "championId": 0, "championPickIntent": 103, "assignedPosition": "middle" } ],
              "actions": [ [ { "actorCellId": 2, "type": "pick", "completed": false } ] ] }
            """);
        Assert.Equal(new ChampSelectState(103, false, "middle"), LcuClient.ParseSession(hovering.RootElement));

        using var locked = JsonDocument.Parse("""
            { "localPlayerCellId": 2, "myTeam": [ { "cellId": 2, "championId": 103, "championPickIntent": 0, "assignedPosition": "" } ],
              "actions": [ [ { "actorCellId": 2, "type": "ban", "completed": true } ], [ { "actorCellId": 2, "type": "pick", "completed": true } ] ] }
            """);
        Assert.Equal(new ChampSelectState(103, true, null), LcuClient.ParseSession(locked.RootElement));
    }

    private static GameRecord SurpriseGame(string result, int[] finalItems, PostGameStats? post, int[]? plannedRunes)
    {
        var me = new RecordedPlayer("Me#EUW", "Garen", "Garen", "TOP", true, true, 16, 5, 3, 7, 200, 12000, finalItems);
        var plan = new SurpriseRecord("Crit Garen", "crit", [3031, 3036, 3072, 3094, 3046], 3006, plannedRunes);
        return new GameRecord(DateTime.Now, "CLASSIC", result, 1800, "Garen", "Garen", "TOP", 0, 0, [], [me], [], [], 0,
            new HistorySnapshot([], [], [], [], 0), PostGame: post, Surprise: plan);
    }

    [Fact]
    public void Surprise_games_are_saved_reviewed_and_earn_a_title()
    {
        int[] planned = new[] { 8000, 8100 }.Concat(ConquerorPage).ToArray();
        var bench = RuneBenchmarks.From(Enumerable.Range(0, 25).Select(_ => Rec(ConquerorPage, true, 40 * 30)));
        var (pm, post) = Game(ConquerorPage, 50 * 30); // played the surprise page; keystone 125% of usual
        var g = SurpriseGame("Win", [3031, 3036, 0, 0, 0, 0, 3006], post, planned);
        var runes = RuneAnalysis.Analyse(pm, post, 30, Runes, bench, 86);
        var review = SurpriseReviewer.Review(g, pm, runes, Runes)!;
        Assert.Equal("Crit Garen worked: a win, and the runes paid off", review.Headline);
        Assert.Contains("You finished 2 of the 5 surprise items.", review.Lines);
        Assert.Contains("You played the surprise rune page.", review.Lines);
        Assert.Contains(review.Lines, l => l.StartsWith("The runes paid off: Conqueror: total healing 1,500 (50/min), 125%"));
        Assert.Contains("Agent of Chaos", Awards.For(g).Select(a => a.Title));

        // Different keystone, lost, no typical values.
        int[] grasp = [8437, 9111, 9104, 8299, 8139, 8135, 5008, 5008, 5011];
        (pm, post) = Game(grasp, 900);
        g = SurpriseGame("Lose", [], post, planned);
        review = SurpriseReviewer.Review(g, pm, RuneAnalysis.Analyse(pm, post, 30, Runes, null, 86), Runes)!;
        Assert.Equal("Crit Garen: a brave try", review.Headline);
        Assert.Contains("You played Grasp of the Undying instead of the surprise keystone (Conqueror).", review.Lines);
        Assert.Contains("None of the surprise items were finished this game.", review.Lines);
        Assert.Contains("Fearless Experimenter", Awards.For(g).Select(a => a.Title));

        // Saved and loaded again; games saved before this have no Surprise.
        var json = JsonSerializer.Serialize(g);
        Assert.Equal("Crit Garen", JsonSerializer.Deserialize<GameRecord>(json)!.Surprise!.Title);
        Assert.Equal(planned, JsonSerializer.Deserialize<GameRecord>(json)!.Surprise!.Runes);
        Assert.Null(SurpriseReviewer.Review(g with { Surprise = null }, pm, null, Runes));
    }
}
