using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Core.Analysis;

/// <summary>Everything the overlay draws. Value-equal, so the UI can skip redraws when nothing changed.</summary>
public sealed record OverlayState(GoldSummary Gold, LaneSummary? Lanes, Lane? MyLane, ItemAdvice Items)
{
    /// <param name="popularBuilds">Looks up popular builds by numeric champion key and position; null = rules only.</param>
    public static OverlayState Build(LiveGame game, StaticData data, int levelWeight = LaneAnalyzer.DefaultLevelWeight,
        Func<int, string?, ChampionBuild?>? popularBuilds = null)
    {
        var gold = GoldAnalyzer.Analyze(game, data.Items);
        var lanes = LaneAnalyzer.Analyze(game, data.Items, levelWeight);
        var myLane = game.Me is { } me ? LaneAnalyzer.LaneOf(me) : null;
        int? myLaneScore = myLane is { } l ? lanes?.Get(l)?.Score : null;
        var popular = game.Me is { } m && popularBuilds is not null && data.Champion(m.ChampionKey) is { Key: > 0 } champ
            ? popularBuilds(champ.Key, m.Position)
            : null;
        var items = ItemRecommender.Recommend(game, data, myLaneScore, popular);
        return new OverlayState(gold, lanes, myLane, items);
    }
}
