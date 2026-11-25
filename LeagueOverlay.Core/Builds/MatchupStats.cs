namespace LeagueOverlay.Core.Builds;

/// <summary>
/// Win rates from collected high-ranked games: each champion in each role, against each champion in the same role (lane
/// matchups), against each enemy champion overall, and next to each allied champion. Collected games keep players in
/// match order (first five = one team), which is what makes teams and lanes recoverable.
/// Small samples are pulled towards 50% (<see cref="Smoothed"/>), so one lucky game doesn't make a "counter".
/// </summary>
public sealed class MatchupStats
{
    public readonly record struct Record(int Games, int Wins)
    {
        public double WinRate => Games == 0 ? 0.5 : Wins / (double)Games;
        /// <summary>Win rate pulled towards 50% by <paramref name="prior"/> imaginary games: honest with small samples.</summary>
        public double Smoothed(int prior = 10) => (Wins + prior * 0.5) / (Games + prior);
        public Record Add(bool win) => new(Games + 1, Wins + (win ? 1 : 0));
    }

    private readonly Dictionary<(int, string), Record> _role = new();
    private readonly Dictionary<(int, string, int), Record> _lane = new();
    private readonly Dictionary<(int, int), Record> _versus = new();
    private readonly Dictionary<(int, int), Record> _with = new();

    public static readonly MatchupStats Empty = new();
    public int Matches { get; private set; }
    public bool IsEmpty => Matches == 0;

    public Record Role(int champion, string position) => _role.GetValueOrDefault((champion, position));
    /// <summary>Your champion against the enemy in the same role.</summary>
    public Record Lane(int champion, string position, int enemy) => _lane.GetValueOrDefault((champion, position, enemy));
    public Record Versus(int champion, int enemy) => _versus.GetValueOrDefault((champion, enemy));
    public Record With(int champion, int ally) => _with.GetValueOrDefault((champion, ally));

    /// <summary>Champions with at least this many games in a role.</summary>
    public IEnumerable<int> PlayedIn(string position, int minGames) =>
        _role.Where(x => x.Key.Item2 == position && x.Value.Games >= minGames).Select(x => x.Key.Item1);

    /// <param name="older">Games from the previous patch: counted too, except anything involving a champion in <paramref name="ignore"/> (changed this patch).</param>
    public static MatchupStats From(IEnumerable<BuildRecord[]> matches, IEnumerable<BuildRecord[]>? older = null, IReadOnlySet<int>? ignore = null)
    {
        var s = new MatchupStats();
        foreach (var m in matches) s.Count(m, null);
        foreach (var m in older ?? []) s.Count(m, ignore);
        return s;
    }

    private void Count(BuildRecord[] m, IReadOnlySet<int>? ignore)
    {
        var s = this;
        {
            if (m.Length != 10) return;
            s.Matches++;
            for (int i = 0; i < 10; i++)
            {
                var me = m[i];
                if (ignore?.Contains(me.Champion) == true) continue;
                bool myTeam(int j) => (j < 5) == (i < 5);
                if (me.Position.Length > 0) s._role[(me.Champion, me.Position)] = s._role.GetValueOrDefault((me.Champion, me.Position)).Add(me.Win);
                for (int j = 0; j < 10; j++)
                {
                    if (j == i) continue;
                    var other = m[j];
                    if (ignore?.Contains(other.Champion) == true) continue;
                    if (myTeam(j))
                        s._with[(me.Champion, other.Champion)] = s._with.GetValueOrDefault((me.Champion, other.Champion)).Add(me.Win);
                    else
                    {
                        s._versus[(me.Champion, other.Champion)] = s._versus.GetValueOrDefault((me.Champion, other.Champion)).Add(me.Win);
                        if (me.Position.Length > 0 && other.Position == me.Position)
                            s._lane[(me.Champion, me.Position, other.Champion)] = s._lane.GetValueOrDefault((me.Champion, me.Position, other.Champion)).Add(me.Win);
                    }
                }
            }
        }
    }
}
