using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Core.Analysis;

public sealed record HistoryPlayer(string Key, string Name, string ChampionKey, bool MyTeam, bool IsMe);

/// <summary>Team item gold (the same numbers the overlay shows) at one moment of game time.</summary>
public sealed record GoldSample(double Time, int MyTeam, int Enemy);

/// <param name="Sold">True for an item sold back to the shop (rather than bought).</param>
public sealed record ItemEvent(double Time, string PlayerKey, int ItemId, string Name, bool Completed, bool Sold = false);

public sealed record LevelEvent(double Time, string PlayerKey, int Level);

/// <summary>Immutable copy of the history, safe to hand to the UI thread.</summary>
public sealed record HistorySnapshot(
    IReadOnlyList<HistoryPlayer> Players,
    IReadOnlyList<GoldSample> Gold,
    IReadOnlyList<ItemEvent> Items,
    IReadOnlyList<LevelEvent> Levels,
    double StartTime)
{
    public static readonly HistorySnapshot Empty = new([], [], [], [], 0);
    public double EndTime => Gold.Count > 0 ? Gold[^1].Time : 0;
    /// <summary>True if recording began after the game had already been going for a while (app started mid-game).</summary>
    public bool StartedLate => StartTime > GameHistory.EarlyGameSeconds;
}

/// <summary>
/// Builds a timeline from successive reads of the live game: team gold over time, item purchases and level-ups.
/// The client only reports the current state, so purchases and level-ups are found by comparing reads.
/// </summary>
public sealed class GameHistory
{
    /// <summary>Items present this early were bought in the fountain; later than this, the start is unknown.</summary>
    public const double EarlyGameSeconds = 90;
    /// <summary>An item that vanishes this soon after being bought, without being built into something, was undone or sold.</summary>
    public const double UndoSeconds = 15;
    /// <summary>When nothing happens, report a change this often anyway so the graph's time axis keeps moving.</summary>
    public const double QuietRefreshSeconds = 10;

    private readonly List<HistoryPlayer> _players = new();
    private readonly List<GoldSample> _gold = new();
    private readonly List<ItemEvent> _items = new();
    private readonly List<LevelEvent> _levels = new();
    private readonly Dictionary<string, Dictionary<int, int>> _lastItems = new();
    private readonly Dictionary<string, int> _lastLevels = new();
    private double _lastTime = -1, _startTime, _lastReported;
    private HistorySnapshot? _snapshot;

    public bool HasData => _gold.Count > 0;

    /// <summary>True if this read belongs to a different game than the one being recorded (or nothing is recorded yet).</summary>
    public bool IsNewGame(LiveGame game) =>
        _lastTime < 0 || (game.GameData?.GameTime ?? 0) < _lastTime - 5 ||
        !game.AllPlayers.Select(Key).ToHashSet().SetEquals(_players.Select(p => p.Key));

    /// <summary>
    /// Records one read of the game. Returns true if the timeline visibly changed (a purchase, level-up or gold change),
    /// or every <see cref="QuietRefreshSeconds"/> otherwise, so callers can skip redrawing in between.
    /// </summary>
    public bool Update(LiveGame game, StaticData data)
    {
        if (game.Me is not { } me || game.GameData is not { } gd) return false;
        double t = gd.GameTime;
        var catalog = data.Items;

        bool newGame = IsNewGame(game);
        if (newGame) Reset(game, me, t);
        else if (t <= _lastTime) return false; // paused, or the same read twice
        _lastTime = t;

        bool recordInitial = newGame && t <= EarlyGameSeconds;
        int eventsBefore = _items.Count + _levels.Count;
        foreach (var p in game.AllPlayers)
        {
            string key = Key(p);
            var counts = new Dictionary<int, int>();
            foreach (var item in p.Items)
            {
                var info = catalog.Get(item.ItemID);
                if (info?.IsConsumableOrTrinket == true) continue; // potions, wards and trinkets would bury the timeline
                counts[item.ItemID] = counts.GetValueOrDefault(item.ItemID) + Math.Max(1, item.Count);
            }

            if (!newGame || recordInitial)
            {
                var before = newGame ? new Dictionary<int, int>() : _lastItems[key];
                var newIds = new List<int>();
                foreach (var (id, count) in counts)
                {
                    for (int n = before.GetValueOrDefault(id); n < count; n++)
                    {
                        var item = p.Items.First(i => i.ItemID == id);
                        _items.Add(new ItemEvent(t, key, id, catalog.Get(id)?.Name ?? item.DisplayName ?? id.ToString(), catalog.IsCompleted(item)));
                        newIds.Add(id);
                    }
                }
                // Items that disappeared: built into something new, upgraded, undone, or sold.
                foreach (var (id, count) in before)
                {
                    for (int n = counts.GetValueOrDefault(id); n < count; n++)
                    {
                        if (UsedUp(id, newIds, catalog)) continue;
                        int idx = _items.FindLastIndex(e => e.PlayerKey == key && e.ItemId == id && !e.Sold);
                        if (idx >= 0 && t - _items[idx].Time <= UndoSeconds) { _items.RemoveAt(idx); continue; } // undo: as if never bought
                        var info = catalog.Get(id);
                        _items.Add(new ItemEvent(t, key, id, info?.Name ?? id.ToString(), info?.IsCompleted == true || catalog.IsUpgradedBoots(new PlayerItem { ItemID = id }), Sold: true));
                    }
                }
            }
            _lastItems[key] = counts;

            if (!newGame && p.Level > _lastLevels[key])
                for (int lv = _lastLevels[key] + 1; lv <= p.Level; lv++) _levels.Add(new LevelEvent(t, key, lv));
            _lastLevels[key] = p.Level;
        }

        var gold = GoldAnalyzer.Analyze(game, catalog);
        var sample = new GoldSample(t, gold.MyTeamGold, gold.EnemyTeamGold);
        // Flat stretches only need their two end points.
        bool goldChanged = _gold.Count == 0 || !Same(_gold[^1], sample);
        if (_gold.Count >= 2 && !goldChanged && Same(_gold[^2], sample)) _gold[^1] = sample;
        else _gold.Add(sample);
        _snapshot = null;

        bool changed = newGame || goldChanged || _items.Count + _levels.Count != eventsBefore || t - _lastReported >= QuietRefreshSeconds;
        if (changed) _lastReported = t;
        return changed;
    }

    public HistorySnapshot Snapshot() =>
        _snapshot ??= new HistorySnapshot(_players.ToList(), _gold.ToList(), _items.ToList(), _levels.ToList(), _startTime);

    private void Reset(LiveGame game, Player me, double t)
    {
        _players.Clear(); _gold.Clear(); _items.Clear(); _levels.Clear(); _lastItems.Clear(); _lastLevels.Clear();
        _startTime = t;
        var myTeam = me.Team;
        // Allies first, then enemies, each in lane order so opponents line up row for row.
        foreach (var p in game.AllPlayers.OrderBy(p => p.Team != myTeam).ThenBy(LaneOrder))
        {
            _players.Add(new HistoryPlayer(Key(p), p.ChampionName ?? p.ChampionKey, p.ChampionKey, p.Team == myTeam, p.IsSameAs(game.ActivePlayer!)));
            _lastLevels[Key(p)] = p.Level;
        }
    }

    /// <summary>
    /// True if a vanished item went into something that appeared at the same time (a component of a new item, boots
    /// upgrading, Dark Seal into Mejai's) or is a support quest item that upgrades by itself: not a sale.
    /// </summary>
    public static bool UsedUp(int id, IReadOnlyCollection<int> newIds, ItemCatalog catalog)
    {
        var info = catalog.Get(id);
        if (info is { IsSupportItem: true }) return true;
        if (info is not null && info.Into.Any(newIds.Contains)) return true;
        return newIds.Any(n => catalog.Get(n) is { } created && catalog.Components(created).Any(c => c.Id == id));
    }

    private static int LaneOrder(Player p) => p.Position?.ToUpperInvariant() switch
    {
        "TOP" => 0, "JUNGLE" => 1, "MIDDLE" => 2, "BOTTOM" => 3, "UTILITY" => 4, _ => 5,
    };

    private static bool Same(GoldSample a, GoldSample b) => a.MyTeam == b.MyTeam && a.Enemy == b.Enemy;

    private static string Key(Player p) =>
        !string.IsNullOrEmpty(p.RiotId) ? p.RiotId : !string.IsNullOrEmpty(p.SummonerName) ? p.SummonerName : p.ChampionKey;
}
