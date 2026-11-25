using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;

namespace LeagueOverlay.Core.Analysis;

/// <summary>Champion suggestions in champion select: off, the best fit for the team, or a random champion you've barely played.</summary>
public enum PickMode { Off, Best, Surprise }

public sealed record ChampMastery(int Level, int Points);

/// <summary>A champion someone has picked or is hovering (0 = none yet), and their role if known.</summary>
public sealed record PickSlot(int Champion, string? Position);

/// <summary>
/// What's known in champion select: your role, allies and enemies so far, champions that can't be picked (bans, picks),
/// the champions you can play (owned + free rotation; null if unknown) and your mastery (null if unknown).
/// </summary>
public sealed record PickContext(
    string? Position, IReadOnlyList<PickSlot> Allies, IReadOnlyList<PickSlot> Enemies, IReadOnlySet<int> Unavailable,
    IReadOnlySet<int>? Pickable, IReadOnlyDictionary<int, ChampMastery>? Mastery);

/// <summary>
/// How "Surprise me" picks: <paramref name="AnyMastery"/> includes champions you play a lot, <paramref name="Even"/> makes
/// every champion equally likely (instead of favouring your least played), <paramref name="OnlyMyRole"/> keeps to
/// champions really played in your role, <paramref name="AvoidLosingLanes"/> skips clearly losing lane matchups.
/// The defaults: barely played champions, least played most likely, any role, no clearly losing lanes.
/// </summary>
public sealed record SurprisePickOptions(bool AnyMastery = false, bool Even = false, bool OnlyMyRole = false, bool AvoidLosingLanes = true)
{
    public static readonly SurprisePickOptions Default = new();
}

public sealed record PickSuggestion(int Key, string ChampionId, string Name, ValueList<string> Reasons);

/// <summary>
/// Champion suggestions.
/// <para><b>Best for the team</b>: champions you can play in your role, scored on their win rate in that role, their lane
/// matchup against the likely enemy laner, how they do against the whole enemy team and next to your allies (all from
/// collected high-ranked games, smoothed towards 50% for small samples), what your team lacks (magic or physical damage,
/// a frontline: Meraki's champion ratings) and a small bonus for champions you have mastery on.</para>
/// <para><b>Surprise me</b>: a random champion you've barely played (lowest mastery first), in any role (off-meta picks
/// and hard champions included), avoiding only clearly losing lane matchups.</para>
/// </summary>
public static class ChampionRecommender
{
    /// <summary>Games in a role for a champion to count as played there when Meraki doesn't list the role (off-role picks).</summary>
    public const int MinRoleGames = 50;
    /// <summary>Games for a role win rate or a lane matchup to be shown as a reason.</summary>
    public const int MinStatGames = 8;
    /// <summary>Mastery points below which a champion counts as barely played.</summary>
    public const int LowMastery = 10_000;

    public static string RoleLabel(string? position) => position switch
    {
        "TOP" => "top", "JUNGLE" => "jungle", "MIDDLE" => "mid", "BOTTOM" => "ADC", "UTILITY" => "support", _ => "any role",
    };

    /// <summary>Champions that can be suggested: available, playable by you and (unless <paramref name="anyRole"/>) played in your role.</summary>
    private static IEnumerable<ChampionInfo> Candidates(PickContext ctx, StaticData data, MatchupStats stats, bool anyRole = false)
    {
        foreach (var c in data.Champions.Values)
        {
            if (c.Key <= 0 || ctx.Unavailable.Contains(c.Key)) continue;
            if (ctx.Pickable is { } p && !p.Contains(c.Key)) continue;
            if (!anyRole && ctx.Position is { } pos)
            {
                // Meraki's positions when known; games in the role only count when it's a real (not one-off) pick there.
                var kit = data.Kits.For(c.Key);
                int games = stats.Role(c.Key, pos).Games;
                bool plays = kit is not null ? kit.PlaysIn(pos) || games >= MinRoleGames : games >= MinStatGames;
                if (!plays) continue;
            }
            yield return c;
        }
    }

    /// <summary>The enemy most likely playing a position: from their assigned role, else where the champion is most played.</summary>
    public static int? LaneOpponent(PickContext ctx, StaticData data, MatchupStats stats)
    {
        if (ctx.Position is not { } pos) return null;
        var known = ctx.Enemies.Where(e => e.Champion > 0).ToList();
        if (known.FirstOrDefault(e => e.Position == pos) is { } assigned) return assigned.Champion;
        string? Likely(int champ)
        {
            var byGames = new[] { "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY" }
                .Select(p => (Pos: p, Games: stats.Role(champ, p).Games)).OrderByDescending(x => x.Games).First();
            return byGames.Games >= 5 ? byGames.Pos : data.Kits.For(champ)?.Positions?.FirstOrDefault();
        }
        return known.FirstOrDefault(e => Likely(e.Champion) == pos)?.Champion;
    }

    public static IReadOnlyList<PickSuggestion> Best(PickContext ctx, StaticData data, MatchupStats? stats, int count = 3)
    {
        stats ??= MatchupStats.Empty;
        string? pos = ctx.Position;
        string Name(int key) => data.ChampionByKey(key)?.Name ?? "?";
        var allies = ctx.Allies.Where(a => a.Champion > 0).Select(a => a.Champion).ToList();
        var enemies = ctx.Enemies.Where(e => e.Champion > 0).Select(e => e.Champion).ToList();
        int? opponent = LaneOpponent(ctx, data, stats);
        var allyKits = allies.Select(data.Kits.For).OfType<ChampionKit>().ToList();

        var scored = new List<(ChampionInfo Champ, double Score, List<(double W, string Text)> Reasons)>();
        foreach (var c in Candidates(ctx, data, stats))
        {
            double score = 0;
            var reasons = new List<(double, string)>();
            var kit = data.Kits.For(c.Key);

            if (pos is not null && stats.Role(c.Key, pos) is { Games: >= MinStatGames } role)
            {
                double d = role.Smoothed(40) - 0.5;
                score += d;
                if (d > 0.01) reasons.Add((d, $"Strong {RoleLabel(pos)} pick: won {role.WinRate:P0} of {role.Games} games"));
            }
            if (opponent is { } opp && pos is not null && stats.Lane(c.Key, pos, opp) is { Games: >= MinStatGames } lane)
            {
                double d = lane.Smoothed(20) - 0.5;
                score += 1.5 * d;
                if (d > 0.015) reasons.Add((1.5 * d, $"Good into {Name(opp)}: won {lane.WinRate:P0} of {lane.Games} games"));
                else if (d < -0.015) reasons.Add((1.5 * -d, $"Careful: tough into {Name(opp)} ({lane.WinRate:P0} of {lane.Games} games)"));
            }
            if (enemies.Count > 0)
            {
                var vs = enemies.Select(e => stats.Versus(c.Key, e)).Where(r => r.Games >= 5).ToList();
                if (vs.Count > 0)
                {
                    double d = vs.Average(r => r.Smoothed(25) - 0.5);
                    score += d;
                    if (d > 0.01) reasons.Add((d, "Does well against their team"));
                }
            }
            if (allies.Count > 0)
            {
                var with = allies.Select(a => (Ally: a, Rec: stats.With(c.Key, a))).Where(x => x.Rec.Games >= 5).ToList();
                if (with.Count > 0)
                {
                    double d = with.Average(x => x.Rec.Smoothed(25) - 0.5) * 0.7;
                    score += d;
                    var best = with.MaxBy(x => x.Rec.Smoothed(25));
                    if (d > 0.008) reasons.Add((d, $"Pairs well with {Name(best.Ally)}"));
                }
            }
            if (kit is not null && allyKits.Count >= 2)
            {
                // What the team lacks: a damage type, a frontline.
                int magic = allyKits.Count(k => k.MagicDamage);
                if (magic == 0 && kit.MagicDamage) { score += 0.03; reasons.Add((0.03, "Your team needs magic damage")); }
                else if (magic == allyKits.Count && !kit.MagicDamage) { score += 0.03; reasons.Add((0.03, "Your team needs physical damage")); }
                if (allyKits.All(k => k.Toughness < 3) && kit.Toughness >= 3) { score += 0.025; reasons.Add((0.025, "Your team has no frontline yet")); }
            }
            if (ctx.Mastery?.GetValueOrDefault(c.Key) is { Points: > 0 } m)
            {
                double comfort = Math.Min(0.03, 0.01 * Math.Log10(1 + m.Points / 1000.0));
                score += comfort;
                if (m.Points >= 20_000) reasons.Add((comfort, $"One of yours: {m.Points:N0} mastery points"));
            }
            scored.Add((c, score, reasons));
        }

        return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Champ.Name).Take(count)
            .Select(x => new PickSuggestion(x.Champ.Key, x.Champ.Id, x.Champ.Name,
                new ValueList<string>(x.Reasons.Count == 0 ? [$"Plays {RoleLabel(pos)}"] : x.Reasons.OrderByDescending(r => r.W).Take(3).Select(r => r.Text))))
            .ToList();
    }

    /// <summary>
    /// A random champion you've barely played, in any role. Keeps <paramref name="current"/> while it's still a valid pick,
    /// so the suggestion doesn't change every few seconds.
    /// </summary>
    public static PickSuggestion? Surprise(PickContext ctx, StaticData data, MatchupStats? stats, int seed, int? current = null,
                                           SurprisePickOptions? options = null)
    {
        stats ??= MatchupStats.Empty;
        options ??= SurprisePickOptions.Default;
        int Points(int key) => ctx.Mastery?.GetValueOrDefault(key)?.Points ?? 0;
        int? opponent = LaneOpponent(ctx, data, stats);
        var all = Candidates(ctx, data, stats, anyRole: !options.OnlyMyRole).ToList();
        // Skip clearly losing lane matchups (when there's enough data to tell).
        bool Losing(ChampionInfo c) => opponent is { } opp && ctx.Position is { } p && stats.Lane(c.Key, p, opp) is { Games: >= MinStatGames } r && r.Smoothed(20) < 0.45;
        var ok = options.AvoidLosingLanes ? all.Where(c => !Losing(c)).ToList() : all;
        if (ok.Count == 0) ok = all;
        var low = options.AnyMastery ? ok : ok.Where(c => Points(c.Key) < LowMastery).ToList();
        if (low.Count == 0) low = ok.OrderBy(c => Points(c.Key)).Take(5).ToList(); // you've played everything: your least played
        if (low.Count == 0) return null;

        var pick = current is { } cur ? low.FirstOrDefault(c => c.Key == cur) : null;
        if (pick is null)
        {
            // Lower mastery = more likely (unless every champion should be equally likely).
            var rng = new Random(seed);
            double Weight(ChampionInfo c) => options.Even ? 1 : 1.0 / (1 + Points(c.Key) / 2000.0);
            double total = low.Sum(Weight), roll = rng.NextDouble() * total;
            foreach (var c in low.OrderBy(c => c.Key)) { roll -= Weight(c); if (roll <= 0) { pick = c; break; } }
            pick ??= low[^1];
        }

        var reasons = new List<string>
        {
            ctx.Mastery is null ? $"A random {RoleLabel(ctx.Position)} champion (mastery unknown)"
                : Points(pick.Key) == 0 ? $"You've never played {pick.Name}"
                : Points(pick.Key) < LowMastery ? $"Only {Points(pick.Key):N0} mastery points on {pick.Name}"
                : $"{Points(pick.Key):N0} mastery points on {pick.Name}",
        };
        if (ctx.Position is { } pos)
            reasons.Add(data.Kits.For(pick.Key)?.PlaysIn(pos) == true || stats.Role(pick.Key, pos).Games >= MinRoleGames
                ? $"A real {RoleLabel(pos)} pick" : $"Off-meta {RoleLabel(pos)} pick: good luck!");
        if (opponent is { } o && ctx.Position is { } lp && stats.Lane(pick.Key, lp, o) is { Games: >= MinStatGames } lane && lane.Smoothed(20) >= 0.5)
            reasons.Add($"Holds up into {data.ChampionByKey(o)?.Name} ({lane.WinRate:P0} of {lane.Games} games)");
        if (data.Kits.For(pick.Key)?.Difficulty is 1) reasons.Add("Easy to pick up");
        return new PickSuggestion(pick.Key, pick.Id, pick.Name, new ValueList<string>(reasons));
    }
}
