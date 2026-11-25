using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;

namespace LeagueOverlay.Core.History;

/// <summary>How a surprise game went: a headline and a few lines (runes played, items built, whether the runes paid off).</summary>
public sealed record SurpriseReview(string Headline, IReadOnlyList<string> Lines);

/// <summary>
/// Looks back at a game played with a surprise build: whether you actually ran the surprise runes and built its items,
/// and whether the runes paid off. "Paid off" uses the keystone's end-of-game number compared with what that keystone
/// usually does (from <see cref="RuneAnalysis"/>); without typical values it just says what the keystone did.
/// </summary>
public static class SurpriseReviewer
{
    public static SurpriseReview? Review(GameRecord g, PostGamePlayer? me, RuneReport? runes, RuneCatalog catalog)
    {
        if (g.Surprise is not { } plan) return null;
        var lines = new List<string>();
        bool won = g.Result == "Win";

        // Items: how many of the planned items you finished.
        var built = g.Build.Select(b => b.ItemId).Concat(g.Me?.Items ?? []).ToHashSet();
        int done = plan.Path.Count(built.Contains);
        lines.Add(done == plan.Path.Count ? $"You built the whole surprise path ({done} items)."
            : done == 0 ? "None of the surprise items were finished this game."
            : $"You finished {done} of the {plan.Path.Count} surprise items.");

        // Runes: did you play the page, and did it pay off?
        bool? paidOff = null;
        var planned = plan.RunePage;
        var played = me?.RunePage;
        if (planned is not null && played is not null)
        {
            if (planned == played) lines.Add("You played the surprise rune page.");
            else if (planned.Keystone == played.Keystone) lines.Add($"You kept the surprise keystone ({catalog.Name(planned.Keystone)}) and changed some other runes.");
            else lines.Add($"You played {catalog.Name(played.Keystone)} instead of the surprise keystone ({catalog.Name(planned.Keystone)}).");

            if (planned.Keystone == played.Keystone && runes?.Lines.FirstOrDefault() is { } keystone)
            {
                string did = keystone.Stats.FirstOrDefault() is { } st ? $"{keystone.Name}: {st.Label.ToLowerInvariant()} {st.Value}" : keystone.Name;
                if (keystone.Ratio is { } r)
                {
                    paidOff = r >= 0.9;
                    lines.Add(r >= RuneAnalysis.ClearGap ? $"The runes paid off: {did}, {r:P0} of what it usually does."
                        : r >= 0.9 ? $"The runes held up: {did}, about what it usually does ({r:P0})."
                        : $"The runes didn't quite pay off: {did}, {r:P0} of what it usually does.");
                }
                else lines.Add($"{did}.");
            }
        }
        else if (planned is not null && me is null)
            lines.Add("Rune results appear once Riot's match history has this game (needs an API key).");
        else if (planned is null)
            lines.Add("Rolled during the game, so there were no surprise runes.");

        string headline = (won, paidOff) switch
        {
            (true, true) => $"{plan.Title} worked: a win, and the runes paid off",
            (true, _) => $"{plan.Title} took the win",
            (false, true) => $"{plan.Title}: the runes did their job, the game didn't go your way",
            _ when g.Result == "Lose" => $"{plan.Title}: a brave try",
            _ => plan.Title,
        };
        return new SurpriseReview(headline, lines);
    }
}
