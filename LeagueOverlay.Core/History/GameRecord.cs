using System.Text.Json;
using System.Text.Json.Serialization;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Core.History;

public sealed record RecordedPlayer(
    string Key, string Name, string ChampionKey, string? Position, bool MyTeam, bool IsMe,
    int Level, int Kills, int Deaths, int Assists, int CreepScore, int ItemGold, int[] Items, double VisionScore = 0)
{
    /// <summary>(kills + assists) / deaths, with zero deaths counted as one.</summary>
    public double Kda => (Kills + Assists) / (double)Math.Max(1, Deaths);

    /// <summary>Every player in a live game, allies first, each team in lane order.</summary>
    public static List<RecordedPlayer> AllFrom(LiveGame game, ItemCatalog items)
    {
        var myTeam = game.Me?.Team;
        return game.AllPlayers
            .OrderBy(p => p.Team != myTeam).ThenBy(p => LaneOrder(p.Position))
            .Select(p => new RecordedPlayer(
                KeyOf(p), p.ChampionName ?? p.ChampionKey, p.ChampionKey, p.Position, p.Team == myTeam,
                game.ActivePlayer is { } a && p.IsSameAs(a),
                p.Level, p.Scores.Kills, p.Scores.Deaths, p.Scores.Assists, p.Scores.CreepScore,
                items.ItemGold(p), ItemSlots.FromLive(p.Items, items), p.Scores.WardScore))
            .ToList();
    }

    public static string KeyOf(Player p) =>
        !string.IsNullOrEmpty(p.RiotId) ? p.RiotId : !string.IsNullOrEmpty(p.SummonerName) ? p.SummonerName : p.ChampionKey;

    private static int LaneOrder(string? position) => position?.ToUpperInvariant() switch
    {
        "TOP" => 0, "JUNGLE" => 1, "MIDDLE" => 2, "BOTTOM" => 3, "UTILITY" => 4, _ => 5,
    };
}

public sealed record RecordedLane(Lane Lane, int GoldDiff, int LevelDiff, int Score);

[JsonConverter(typeof(JsonStringEnumConverter<StepMatch>))]
public enum StepMatch
{
    /// <summary>Bought the item the overlay showed as NEXT.</summary>
    Followed,
    /// <summary>Bought one of the overlay's situational picks.</summary>
    Situational,
    /// <summary>Bought something else than suggested.</summary>
    Different,
    /// <summary>The overlay had no suggestion at the time (e.g. item data still loading).</summary>
    NoSuggestion,
}

/// <summary>One finished item you bought, next to what the overlay suggested right before.</summary>
public sealed record BuildStep(
    double Time, int ItemId, string ItemName,
    int? SuggestedId, string? SuggestedName, string? SuggestedReason,
    int[] SituationalIds, string[] SituationalNames, StepMatch Match);

/// <summary>Everything kept about one finished game, saved as JSON in %LOCALAPPDATA%\LeagueOverlay\games.</summary>
public sealed record GameRecord(
    DateTime EndedAt,
    string GameMode,
    /// <summary>"Win", "Lose", or "Unknown" (the app didn't see the end screen, e.g. it was closed mid-game).</summary>
    string Result,
    double Duration,
    string ChampionKey,
    string ChampionName,
    string? Position,
    int MyTeamGold,
    int EnemyTeamGold,
    List<RecordedLane> Lanes,
    List<RecordedPlayer> Players,
    List<BuildStep> Build,
    /// <summary>Most common build on this champion when the game ended (empty if there was no popular-build data).</summary>
    List<int> PopularPath,
    int PopularGames,
    HistorySnapshot Timeline,
    /// <summary>True if the end screen was missed and Result was worked out from which nexus turrets fell.</summary>
    bool ResultInferred = false,
    /// <summary>The last few game events seen (names only), to understand games whose result couldn't be read.</summary>
    List<string>? LastEvents = null,
    /// <summary>Final numbers from Riot's match history, added a few minutes after the game when there's an API key.</summary>
    PostGameStats? PostGame = null,
    /// <summary>Taken from Riot's match history rather than recorded live: no suggestions, and the graph shows gold earned.</summary>
    bool Imported = false)
{
    public RecordedPlayer? Me => Players.FirstOrDefault(p => p.IsMe);
    public int Followed => Build.Count(s => s.Match is StepMatch.Followed or StepMatch.Situational);
    public int Suggested => Build.Count(s => s.Match != StepMatch.NoSuggestion);
}

/// <summary>
/// Records the current game for the history: the timeline (via <see cref="GameHistory"/>) plus, for each finished
/// item you buy, what the overlay suggested on the read just before the purchase showed up.
/// </summary>
public sealed class GameRecorder
{
    /// <summary>Shorter sessions (a quick practice-tool check, a remake) aren't worth keeping.</summary>
    public const double MinGameSeconds = 5 * 60;

    private readonly GameHistory _history = new();
    private readonly List<BuildStep> _steps = new();
    private LiveGame? _lastGame;
    private OverlayState? _lastState;
    private ChampionBuild? _popular;
    private StaticData _data = StaticData.Empty;
    private string? _result;

    public HistorySnapshot Snapshot() => _history.Snapshot();
    public bool IsRecording => _lastGame is not null;

    /// <summary>
    /// Records one read. Returns whether the timeline changed (worth redrawing), and the previous game's record
    /// if this read is already a different game (the end of the last one was missed).
    /// </summary>
    public (bool TimelineChanged, GameRecord? Finished) Update(LiveGame game, StaticData data, OverlayState state, ChampionBuild? popular)
    {
        GameRecord? finished = null;
        if (_lastGame is not null && _history.IsNewGame(game)) finished = Finish();

        bool changed = _history.Update(game, data);
        _data = data;
        TrackPurchases();
        _lastState = state; // the next purchase is compared with what is being shown now
        if (popular is not null) _popular = popular;
        if (game.Result is { } r) _result = r;
        _lastGame = game;
        return (changed, finished);
    }

    private void TrackPurchases()
    {
        var snapshot = _history.Snapshot();
        var meKey = snapshot.Players.FirstOrDefault(p => p.IsMe)?.Key;
        var mine = snapshot.Items.Where(e => e.PlayerKey == meKey && e.Completed && !e.Sold).ToList();
        // Tier-3 boots cost nothing on top of their tier-2 pair: they're upgrades that happen on their own
        // (e.g. the bot lane quest's Gunmetal Greaves), not a buying decision.
        mine.RemoveAll(e => MatchParser.TierTwoBoots(e.ItemId, _data.Items) is { } tierTwo && tierTwo != e.ItemId);

        // Undone purchases disappear from the timeline; drop their steps too.
        _steps.RemoveAll(s => !mine.Any(e => e.Time == s.Time && e.ItemId == s.ItemId));
        foreach (var e in mine)
        {
            if (_steps.Any(s => s.Time == e.Time && s.ItemId == e.ItemId)) continue;
            var advice = _lastState?.Items;
            var core = advice?.Core;
            var situational = advice?.Situational ?? ValueList<Recommendation>.Empty;
            var match = core is null && situational.Count == 0 ? StepMatch.NoSuggestion
                : core is not null && SameItem(core.ItemId, core.Name, e) ? StepMatch.Followed
                : situational.Any(s => SameItem(s.ItemId, s.Name, e)) ? StepMatch.Situational
                : StepMatch.Different;
            _steps.Add(new BuildStep(e.Time, e.ItemId, e.Name, core?.ItemId, core?.Name, core?.Reason,
                situational.Select(s => s.ItemId).ToArray(), situational.Select(s => s.Name).ToArray(), match));
        }
        _steps.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    // Shop duplicates share a name across ids, so compare by name as well.
    private static bool SameItem(int id, string name, ItemEvent e) => id == e.ItemId || name == e.Name;

    /// <summary>Wraps up the current game. Null if nothing worth keeping was recorded.</summary>
    public GameRecord? Finish()
    {
        var game = _lastGame;
        var state = _lastState;
        var snapshot = _history.Snapshot();
        var steps = _steps.ToList();
        string? inferred = _result is null ? game?.ResultFromNexusTurrets() : null;
        string result = _result ?? inferred ?? "Unknown";
        var lastEvents = game?.Events?.Events.TakeLast(8).Select(e => $"{e.EventTime:0}s {e.EventName}{(e.TurretKilled is { } t ? " " + t : "")}").ToList();
        var popular = _popular;
        _lastGame = null; _lastState = null; _popular = null; _result = null; _steps.Clear();

        if (game?.Me is not { } me || state is null || snapshot.EndTime < MinGameSeconds) return null;
        var champ = _data.Champion(me.ChampionKey);
        var players = RecordedPlayer.AllFrom(game, _data.Items);

        return new GameRecord(
            DateTime.Now, game.GameData?.GameMode ?? "", result, snapshot.EndTime,
            me.ChampionKey, champ?.Name ?? me.ChampionName ?? me.ChampionKey, me.Position,
            state.Gold.MyTeamGold, state.Gold.EnemyTeamGold,
            state.Lanes?.Lanes.Select(l => new RecordedLane(l.Lane, l.GoldDiff, l.LevelDiff, l.Score)).ToList() ?? [],
            players, steps, popular?.Path.ToList() ?? [], popular?.Games ?? 0, snapshot,
            ResultInferred: inferred is not null, LastEvents: lastEvents);
    }
}

/// <summary>The last <see cref="Keep"/> games, one JSON file each.</summary>
public sealed class GameRecordStore(string root)
{
    public const int Keep = 10;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueOverlay", "games");

    public string Root { get; } = root;

    public void Save(GameRecord record)
    {
        Directory.CreateDirectory(Root);
        var safeChamp = string.Concat(record.ChampionKey.Where(char.IsLetterOrDigit));
        var path = Path.Combine(Root, $"{record.EndedAt:yyyyMMdd-HHmmss}-{safeChamp}.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(record, Json));
        File.Move(path + ".tmp", path, overwrite: true);
        foreach (var old in Files().Skip(Keep)) try { File.Delete(old); } catch (IOException) { }
    }

    /// <summary>Newest first. Files that can't be read (e.g. from an older version) are skipped.</summary>
    public List<GameRecord> LoadRecent()
    {
        var list = new List<GameRecord>();
        foreach (var file in Files().Take(Keep))
        {
            try { if (JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(file), Json) is { } r) list.Add(r); }
            catch (Exception e) when (e is JsonException or IOException or NotSupportedException) { }
        }
        return list;
    }

    // File names start with the end time, so name order is time order.
    private IEnumerable<string> Files() =>
        Directory.Exists(Root) ? Directory.GetFiles(Root, "*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal) : [];
}
