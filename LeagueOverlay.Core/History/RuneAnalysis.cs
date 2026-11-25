using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;

namespace LeagueOverlay.Core.History;

/// <summary>One labelled end-of-game number of a rune: "Total Damage Dealt" · "2,340 (78/min)".</summary>
public sealed record RuneStatLine(string Label, string Value);

/// <summary>
/// What one rune did in the game, how that compares with the rune's usual result (Ratio: yours / typical),
/// and an alternative from the same row that would probably have done more, if there's a clear one.
/// </summary>
public sealed record RuneLine(int RuneId, string Name, IReadOnlyList<RuneStatLine> Stats, string? Compare, double? Ratio, string? Suggestion, int? SuggestedRune);

public sealed record RuneReport(RuneSetup Page, IReadOnlyList<RuneLine> Lines, IReadOnlyList<string> Notes, bool HasBenchmarks);

/// <summary>
/// Post-game look at your runes. Match history gives each rune's own end-of-game numbers (the ones in the client's
/// end-of-game rune tooltips): damage dealt, healing, gold, stacks... Those are compared with what the same rune
/// usually does in high-ranked games (<see cref="RuneBenchmarks"/>, per minute), and the other runes of the same row
/// with the same kind of number (damage with damage, healing with healing) with how much they usually do.
/// A few rules about this game (its length, the enemy's crowd control) add notes.
/// </summary>
public static class RuneAnalysis
{
    /// <summary>An alternative is only suggested when it usually does this much more than yours did.</summary>
    public const double ClearGap = 1.25;
    /// <summary>Enemy team crowd control (seconds per minute of game) above which tenacity is worth mentioning; ~5 is a normal game.</summary>
    public const double HeavyCcPerMinute = 8;

    public static RuneReport? Analyse(PostGamePlayer me, PostGameStats post, double minutes, RuneCatalog runes, RuneBenchmarks? bench, int championKey)
    {
        if (me.RunePage is not { } page || !runes.IsLoaded) return null;
        bench ??= RuneBenchmarks.Empty;
        minutes = Math.Max(1, minutes);
        var lines = new List<RuneLine>();
        var notes = new List<string>();

        for (int i = 0; i < 6 && i < page.Perks.Count; i++)
        {
            int id = page.Perks[i];
            string name = runes.Name(id);
            var labels = runes.Labels(id);
            var stats = new List<RuneStatLine>();
            foreach (var l in labels)
            {
                int raw = me.RuneVar(i, l.Var);
                if (raw == 0 && l != labels[0]) continue;
                stats.Add(new RuneStatLine(l.Label, Format(l, raw, me.RuneVar(i, l.Var + 1), minutes)));
            }

            string? compare = null, suggestion = null;
            double? ratio = null;
            int? suggested = null;
            var main = runes.MainStat(id) ?? labels.FirstOrDefault(l => l.Kind == RuneStatKind.Count);
            if (main is not null)
            {
                double mine = main.PerMinute ? me.RuneVar(i, main.Var) / minutes : me.RuneVar(i, main.Var);
                var usual = bench.For(id, main.Var, championKey, main.PerMinute, out bool onChamp);
                if (usual is { Median: > 0 })
                {
                    ratio = mine / usual.Median;
                    string verdict = ratio >= ClearGap ? "well above usual" : ratio >= 0.8 ? "about usual" : "below usual";
                    string unit = main.PerMinute ? "/min" : main.Kind == RuneStatKind.Percent ? "%" : "";
                    compare = $"{verdict}: {ratio:P0} of the typical {usual.Median:#,0.#}{unit}{(onChamp ? " on this champion" : "")}";
                }
                if (main.Kind is RuneStatKind.Damage or RuneStatKind.Healing or RuneStatKind.Shield)
                {
                    var best = runes.Alternatives(id)
                        .Select(alt => (Alt: alt, Stat: runes.MainStat(alt)))
                        .Where(x => x.Stat is { } s && s.Kind == main.Kind)
                        .Select(x => (x.Alt, Usual: bench.For(x.Alt, x.Stat!.Var, championKey, true, out _)))
                        .Where(x => x.Usual is { Median: > 0 })
                        // Clearly more than yours did, and generally stronger than your rune too (not just your quiet game).
                        .Where(x => x.Usual!.Median > Math.Max(mine, 1) * ClearGap && (usual is null || x.Usual.Median > usual.Median * 1.1))
                        .OrderByDescending(x => x.Usual!.Median)
                        .FirstOrDefault();
                    if (best.Usual is { } b)
                    {
                        string what = main.Kind switch { RuneStatKind.Healing => "healing", RuneStatKind.Shield => "shielding", _ => "damage" };
                        double expected = b.Median * minutes;
                        double done = mine * minutes;
                        suggested = best.Alt;
                        suggestion = done < 1
                            ? $"{runes.Name(best.Alt)} usually gives about {expected:N0} {what} in a game this long."
                            : $"{runes.Name(best.Alt)} usually gives about {expected:N0} {what} in a game this long, {expected / done - 1:P0} more than {name} did here.";
                    }
                }
            }
            lines.Add(new RuneLine(id, name, stats, compare, ratio, suggestion, suggested));
        }

        GameLengthRules(page, minutes, runes, lines);

        // Lots of crowd control from the enemy and nothing on the page against it.
        int enemyCc = post.Players.Where(p => p.TeamId != me.TeamId).Sum(p => p.CcSeconds);
        bool hasTenacity = page.Perks.Contains(5013) || page.Perks.Contains(runes.ByName("Unflinching")?.Id ?? -1) ||
                           page.Perks.Contains(runes.ByName("Legend: Tenacity")?.Id ?? -1);
        if (enemyCc / minutes >= HeavyCcPerMinute && !hasTenacity)
            notes.Add($"The enemy team applied a lot of crowd control ({enemyCc:N0} seconds in total). The Tenacity and Slow Resist shard or Unflinching would have shortened it.");

        return new RuneReport(page, lines, notes, !bench.IsEmpty);
    }

    /// <summary>Scaling runes in short games and early runes in long ones, when there's no clearer suggestion for that rune.</summary>
    private static void GameLengthRules(RuneSetup page, double minutes, RuneCatalog runes, List<RuneLine> lines)
    {
        var pairs = new (string Scaling, string Early)[] { ("Gathering Storm", "Scorch"), ("Overgrowth", "Second Wind") };
        foreach (var (scaling, early) in pairs)
        {
            if (runes.ByName(scaling) is not { } s || runes.ByName(early) is not { } e) continue;
            int idx = lines.FindIndex(l => l.RuneId == s.Id);
            if (idx >= 0 && minutes < 22 && lines[idx].Suggestion is null)
                lines[idx] = lines[idx] with { Suggestion = $"Short game ({minutes:0} min): {scaling} needs time to pay off; {early} helps earlier.", SuggestedRune = e.Id };
            idx = lines.FindIndex(l => l.RuneId == e.Id);
            if (idx >= 0 && minutes > 35 && lines[idx].Suggestion is null)
                lines[idx] = lines[idx] with { Suggestion = $"Long game ({minutes:0} min): {scaling} would have kept growing.", SuggestedRune = s.Id };
        }
    }

    private static string Format(RuneStatLabel l, int raw, int next, double minutes) => l.Kind switch
    {
        RuneStatKind.Time => $"{raw}:{next:00}",
        RuneStatKind.Percent => $"{raw}%",
        RuneStatKind.Damage or RuneStatKind.Healing or RuneStatKind.Shield or RuneStatKind.Gold => $"{raw:N0} ({raw / minutes:0}/min)",
        _ => $"{raw:N0}",
    };
}
