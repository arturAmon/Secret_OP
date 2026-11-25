using LeagueOverlay.Core.Data;

namespace LeagueOverlay.Core.Analysis;

/// <summary>
/// Keeps one surprise plan from lock-in until the game ends: rolled when you lock in a champion (or when the game starts
/// without one), carried into that game, kept for the whole game, and new only for a new champion, a new game or a reroll.
/// A game is "the same game" by champion and time, not by the player list, so a scoreboard that fills in late during
/// loading can't trigger a reroll.
/// </summary>
public sealed class SurpriseSession
{
    /// <summary>The live game may briefly not answer (loading, a hiccup); a plan survives this long without one.</summary>
    public static readonly TimeSpan GameGap = TimeSpan.FromMinutes(2);

    private enum Stage { None, ChampSelect, Game }
    private Stage _stage;
    private string? _champion;
    private DateTime _lastGameSeen;
    private readonly Func<DateTime> _now;

    public SurpriseSession(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public SurprisePlan? Plan { get; private set; }

    /// <summary>Locked in during champion select. Returns true when a new plan was rolled.</summary>
    public bool ChampSelect(ChampionInfo champ, Func<SurprisePlan?> roll)
    {
        if (Plan is not null && _stage == Stage.ChampSelect && Same(champ)) return false;
        if (Plan is not null && _stage == Stage.Game && Same(champ) && _now() - _lastGameSeen < GameGap) return false; // still the game
        return Roll(champ, roll, Stage.ChampSelect);
    }

    /// <summary>In game. Keeps the champion select plan (or this game's), rolls only when there's none for this champion.</summary>
    public bool Game(ChampionInfo champ, Func<SurprisePlan?> roll)
    {
        bool keep = Plan is not null && Same(champ) &&
                    (_stage == Stage.ChampSelect || (_stage is Stage.Game or Stage.None && _now() - _lastGameSeen < GameGap));
        _lastGameSeen = _now();
        if (keep) { _stage = Stage.Game; return false; }
        return Roll(champ, roll, Stage.Game);
    }

    /// <summary>No game running (it ended, or hasn't started).</summary>
    public void NoGame()
    {
        if (_stage == Stage.Game && _now() - _lastGameSeen >= GameGap) _stage = Stage.None;
    }

    /// <summary>A new plan on the next read.</summary>
    public void Reroll() => Plan = null;

    private bool Same(ChampionInfo champ) => string.Equals(_champion, champ.Id, StringComparison.OrdinalIgnoreCase);

    private bool Roll(ChampionInfo champ, Func<SurprisePlan?> roll, Stage stage)
    {
        Plan = roll();
        _champion = champ.Id;
        _stage = stage;
        if (stage == Stage.Game) _lastGameSeen = _now();
        return true;
    }
}
