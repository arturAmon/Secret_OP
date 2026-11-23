using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Core.Analysis;

public sealed record GoldSummary(int MyTeamGold, int EnemyTeamGold)
{
    /// <summary>Positive = enemy team has spent more on items.</summary>
    public int EnemyLead => EnemyTeamGold - MyTeamGold;
}

public static class GoldAnalyzer
{
    /// <summary>The active player's team ("ORDER"/"CHAOS"). Falls back to ORDER if the active player can't be matched.</summary>
    public static string MyTeam(LiveGame game) => game.Me?.Team ?? "ORDER";

    public static IEnumerable<Player> Allies(LiveGame game) { var t = MyTeam(game); return game.AllPlayers.Where(p => p.Team == t); }
    public static IEnumerable<Player> Enemies(LiveGame game) { var t = MyTeam(game); return game.AllPlayers.Where(p => p.Team != t); }

    public static GoldSummary Analyze(LiveGame game, ItemCatalog items) =>
        new(Allies(game).Sum(items.ItemGold), Enemies(game).Sum(items.ItemGold));
}
