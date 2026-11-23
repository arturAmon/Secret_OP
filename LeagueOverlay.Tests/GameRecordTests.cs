using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.History;
using LeagueOverlay.Core.Live;
using static LeagueOverlay.Tests.Fixtures;

namespace LeagueOverlay.Tests;

public class GameRecordTests
{
    private static LiveGame At(double time, int[] jinxItems, string? result = null)
    {
        var g = Game(P("Jinx", "ORDER", "BOTTOM", 9, jinxItems), 0, P("Caitlyn", "CHAOS", "BOTTOM", 9, 3031));
        g.GameData!.GameTime = time;
        if (result is not null) g.Events = new EventList { Events = [new GameEvent { EventName = "GameEnd", Result = result }] };
        return g;
    }

    private static Recommendation Rec(int id, string name) => new(id, name, 3000, 3000, "test");

    private static OverlayState Showing(Recommendation? core, params Recommendation[] situational) =>
        new(new GoldSummary(1000, 1200), null, Lane.Bot, new ItemAdvice(core, new ValueList<Recommendation>(situational), null));

    private static GameRecord PlayOneGame(GameRecorder recorder)
    {
        var ie = Rec(3031, "Infinity Edge");
        var mortal = Rec(3033, "Mortal Reminder");
        var steelcaps = Rec(3047, "Plated Steelcaps");
        recorder.Update(At(300, [1038, 3006]), Data, Showing(ie, steelcaps), null);
        recorder.Update(At(302, [3031, 3006]), Data, Showing(mortal, steelcaps), null);      // followed the suggestion
        recorder.Update(At(500, [3031, 3006, 3071]), Data, Showing(mortal, steelcaps), null); // bought something else
        recorder.Update(At(700, [3031, 3006, 3071, 3047]), Data, Showing(mortal), null);     // a situational pick
        recorder.Update(At(701, [3031, 3172, 3071, 3047]), Data, Showing(mortal), null);     // boots upgraded by the quest: not a choice
        recorder.Update(At(702, [3031, 3172, 3071, 3047], result: "Win"), Data, Showing(mortal), null);
        return recorder.Finish()!;
    }

    [Fact]
    public void Each_finished_item_is_compared_with_what_was_suggested_just_before()
    {
        var record = PlayOneGame(new GameRecorder());

        Assert.Equal("Win", record.Result);
        Assert.Equal("Jinx", record.ChampionName);
        Assert.Equal([StepMatch.Followed, StepMatch.Different, StepMatch.Situational], record.Build.Select(s => s.Match));
        var different = record.Build[1];
        Assert.Equal("Black Cleaver", different.ItemName);
        Assert.Equal("Mortal Reminder", different.SuggestedName);
        Assert.Equal(2, record.Followed);
        Assert.Equal(3, record.Suggested);
        Assert.True(record.Me!.IsMe);
        Assert.Equal([3031, 3172, 3071, 3047, 0, 0, 0, 0], record.Me.Items); // stored by slot (index = slot)
        Assert.DoesNotContain(record.Build, s => s.ItemName == "Gunmetal Greaves");
    }

    [Fact]
    public void Missed_end_screen_falls_back_to_the_nexus_turrets()
    {
        static GameEvent Turret(string name) => new() { EventName = "TurretKilled", TurretKilled = name };
        var recorder = new GameRecorder();
        recorder.Update(At(300, [1038]), Data, Showing(null), null);
        var last = At(1700, [1038]);
        last.Events = new EventList { Events = [Turret("Turret_T2_C_05_A"), Turret("Turret_T2_C_01_A"), Turret("Turret_T2_C_02_A"), Turret("Turret_T1_L_03_A")] };
        recorder.Update(last, Data, Showing(null), null);
        var record = recorder.Finish()!; // the client closed before a GameEnd event was seen

        Assert.Equal("Win", record.Result); // Jinx is ORDER; both CHAOS nexus turrets fell
        Assert.True(record.ResultInferred);
        Assert.Equal(4, record.LastEvents!.Count);

        var unclear = At(1700, []);
        unclear.Events = new EventList { Events = [Turret("Turret_T2_C_01_A")] };
        Assert.Null(unclear.ResultFromNexusTurrets());
    }

    [Fact]
    public void Short_sessions_are_not_kept_and_a_new_game_closes_the_previous_one()
    {
        var recorder = new GameRecorder();
        recorder.Update(At(10, []), Data, Showing(null), null);
        recorder.Update(At(60, []), Data, Showing(null), null);
        Assert.Null(recorder.Finish()); // one minute in the practice tool

        recorder.Update(At(300, [1038]), Data, Showing(null), null);
        recorder.Update(At(400, [1038]), Data, Showing(null), null);
        var (_, finished) = recorder.Update(At(5, []), Data, Showing(null), null); // next game started; end screen missed
        Assert.NotNull(finished);
        Assert.Equal("Unknown", finished!.Result);
    }

    [Fact]
    public void Store_round_trips_and_keeps_only_the_last_ten()
    {
        var root = Path.Combine(Path.GetTempPath(), "lo_games_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new GameRecordStore(root);
            var record = PlayOneGame(new GameRecorder());
            for (int i = 0; i < 12; i++) store.Save(record with { EndedAt = new DateTime(2026, 11, 1).AddHours(i) });

            var loaded = store.LoadRecent();
            Assert.Equal(10, loaded.Count);
            Assert.Equal(10, Directory.GetFiles(root).Length);
            Assert.Equal(new DateTime(2026, 11, 1).AddHours(11), loaded[0].EndedAt); // newest first
            Assert.Equal(record.Build.Select(s => (s.ItemName, s.Match)), loaded[0].Build.Select(s => (s.ItemName, s.Match)));
            Assert.Equal(record.Timeline.Gold.Count, loaded[0].Timeline.Gold.Count);
            Assert.Equal(record.Timeline.Items.Count, loaded[0].Timeline.Items.Count);
            Assert.True(loaded[0].Timeline.Players.Single(p => p.IsMe).MyTeam);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
