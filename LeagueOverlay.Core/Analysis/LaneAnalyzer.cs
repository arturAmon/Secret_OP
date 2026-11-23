using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Core.Analysis;

public enum Lane { Top, Jungle, Mid, Bot }

/// <summary>All values from my team's side: positive = we're ahead.</summary>
public sealed record LaneLead(Lane Lane, int GoldDiff, int LevelDiff, int Score);

public sealed record LaneSummary(ValueList<LaneLead> Lanes, Lane? Highlight)
{
    public LaneLead? Get(Lane lane) => Lanes.FirstOrDefault(l => l.Lane == lane);
}

public static class LaneAnalyzer
{
    public const int DefaultLevelWeight = 350;

    public static Lane? LaneOf(Player p) => (p.Position ?? "").ToUpperInvariant() switch
    {
        "TOP" => Lane.Top,
        "JUNGLE" => Lane.Jungle,
        "MIDDLE" or "MID" => Lane.Mid,
        "BOTTOM" or "UTILITY" or "SUPPORT" => Lane.Bot, // bot lane = ADC + support
        _ => p.HasSmite ? Lane.Jungle : null,          // customs / modes without positions
    };

    /// <summary>Lead per lane = item gold diff + levelWeight × level diff. Null when lanes can't be paired.</summary>
    public static LaneSummary? Analyze(LiveGame game, ItemCatalog items, int levelWeight = DefaultLevelWeight)
    {
        var allies = GoldAnalyzer.Allies(game).ToList();
        var enemies = GoldAnalyzer.Enemies(game).ToList();
        var leads = new List<LaneLead>();

        foreach (var lane in Enum.GetValues<Lane>())
        {
            var mine = allies.Where(p => LaneOf(p) == lane).ToList();
            var theirs = enemies.Where(p => LaneOf(p) == lane).ToList();
            if (mine.Count == 0 || theirs.Count == 0) continue;

            int gold = mine.Sum(items.ItemGold) - theirs.Sum(items.ItemGold);
            int level = mine.Sum(p => p.Level) - theirs.Sum(p => p.Level);
            leads.Add(new LaneLead(lane, gold, level, gold + levelWeight * level));
        }

        if (leads.Count == 0) return null;
        var top = leads.MaxBy(l => Math.Abs(l.Score))!;
        return new LaneSummary(new ValueList<LaneLead>(leads), top.Score == 0 ? null : top.Lane);
    }
}
