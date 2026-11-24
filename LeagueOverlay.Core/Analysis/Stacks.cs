using System.Text.RegularExpressions;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Core.Analysis;

/// <summary>One counter for the overlay, e.g. "Phenomenal Evil · ≈214 stacks · +214 ability power", with what it means.</summary>
public sealed record StackInfo(string Title, string Value, string Detail, string Meaning, int? ItemId = null);

public sealed record StackReport(ValueList<StackInfo> Lines, bool DarkSealFull)
{
    public static readonly StackReport None = new(ValueList<StackInfo>.Empty, false);
}

/// <summary>
/// Stacks the game client doesn't report directly, worked out from what it does report:
/// <list type="bullet">
/// <item>Glory on Dark Seal / Mejai's Soulstealer, counted from your takedowns and deaths since you bought it
///   (the cap, loss on death and AP per Glory are read from the item's text in Data Dragon; 1 Glory per takedown).</item>
/// <item>Champion stacks that show up in your stats (Veigar's AP, Senna's range and crit, Cho'Gath's and Sion's health,
///   Thresh's armor): your stat minus the champion's base at your level, your items' stats and rune shards.
///   Runes beyond the shards can make these a little high, so they're shown as approximate.</item>
/// </list>
/// Keeps a little state (when the Seal / Mejai's were bought), so use one instance per app.
/// </summary>
public sealed class StackTracker
{
    public const int DarkSealId = 1082, MejaiId = 3041;
    private const int AdaptiveShard = 5008, HealthShard = 5011, HealthScalingShard = 5001;

    private double? _sealSince, _mejaiSince;
    private string? _gameKey;

    public StackReport Update(LiveGame game, StaticData data)
    {
        if (game.Me is not { } me || game.ActivePlayer is not { } active || game.GameData is not { } gd) return StackReport.None;
        string gameKey = string.Join("|", game.AllPlayers.Select(p => p.RiotId ?? p.SummonerName));
        if (gameKey != _gameKey || gd.GameTime < (_sealSince ?? 0)) { _gameKey = gameKey; _sealSince = _mejaiSince = null; }

        var lines = new List<StackInfo>();
        bool ownsSeal = me.Items.Any(i => i.ItemID == DarkSealId), ownsMejai = me.Items.Any(i => i.ItemID == MejaiId);
        if (ownsSeal || ownsMejai) _sealSince ??= gd.GameTime;
        else _sealSince = null;
        if (ownsMejai) _mejaiSince ??= gd.GameTime;
        else _mejaiSince = null;

        int glory = 0;
        double gloryAp = 0;
        bool sealFull = false;
        if (_sealSince is { } since)
        {
            var seal = GloryRules.From(data.Items.Get(DarkSealId), 10, 5, 4);
            var mejai = GloryRules.From(data.Items.Get(MejaiId), 25, 10, 5);
            foreach (var e in Takedowns(game, me, since))
            {
                var rules = _mejaiSince is { } m && e.Time >= m ? mejai : seal;
                glory = Math.Clamp(glory + (e.Died ? -rules.LostOnDeath : 1), 0, rules.Cap);
            }
            var now = ownsMejai ? mejai : seal;
            glory = Math.Min(glory, now.Cap);
            gloryAp = glory * now.ApPerGlory;
            sealFull = ownsSeal && !ownsMejai && glory >= seal.Cap;
            string name = data.Items.Get(ownsMejai ? MejaiId : DarkSealId)?.Name ?? (ownsMejai ? "Mejai's Soulstealer" : "Dark Seal");
            lines.Add(new StackInfo($"{name} · Glory", $"≈ {glory} / {now.Cap}", $"+{gloryAp:0} ability power",
                $"Takedowns give Glory, {now.LostOnDeath} is lost on death." + (ownsSeal && !ownsMejai ? " Full Glory carries over into Mejai's." : ""),
                ownsMejai ? MejaiId : DarkSealId));
        }

        if (active.ChampionStats is { } stats && data.Champion(me.ChampionKey) is { } champ &&
            ChampionStack(champ, me, stats, active.FullRunes, data.Items, gloryAp) is { } champLine)
            lines.Add(champLine);

        return new StackReport(new ValueList<StackInfo>(lines), sealFull);
    }

    private sealed record GloryRules(int Cap, int LostOnDeath, double ApPerGlory)
    {
        private static readonly Regex Cap_ = new(@"up to (\d+)", RegexOptions.IgnoreCase);
        private static readonly Regex Lost = new(@"(\d+) Glory is lost on death", RegexOptions.IgnoreCase);
        private static readonly Regex Ap = new(@"(\d+) Ability Power per Glory", RegexOptions.IgnoreCase);

        public static GloryRules From(ItemInfo? item, int cap, int lost, double ap)
        {
            string text = item?.Text ?? "";
            int Num(Regex r, int fallback) => r.Match(text) is { Success: true } m ? int.Parse(m.Groups[1].Value) : fallback;
            return new GloryRules(Num(Cap_, cap), Num(Lost, lost), Num(Ap, (int)ap));
        }
    }

    /// <summary>Your kills/assists and deaths since a game time, in order.</summary>
    private static IEnumerable<(double Time, bool Died)> Takedowns(LiveGame game, Player me, double since)
    {
        var names = new[] { me.RiotIdGameName, me.SummonerName, me.RiotId }
            .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!.Split('#')[0].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool IsMe(string? n) => n is not null && names.Contains(n.Split('#')[0].Trim());
        foreach (var e in game.Events?.Events ?? [])
        {
            if (e.EventName != "ChampionKill" || e.EventTime < since) continue;
            if (IsMe(e.VictimName)) yield return (e.EventTime, true);
            else if (IsMe(e.KillerName) || e.Assisters.Any(IsMe)) yield return (e.EventTime, false);
        }
    }

    /// <summary>The champion's own stacking mechanic, when it shows up in a stat the client reports.</summary>
    public static StackInfo? ChampionStack(ChampionInfo champ, Player me, ChampionStats stats, FullRunes? runes, ItemCatalog items, double gloryAp)
    {
        int level = Math.Max(1, me.Level);
        int Shards(int id) => runes?.StatRunes.Count(r => r.Id == id) ?? 0;
        double ShardHealth() => Shards(HealthShard) * 65 + Shards(HealthScalingShard) * (10 + 170.0 * (level - 1) / 17);
        double BonusHealth() => stats.MaxHealth - champ.StatAt("hp", level) - items.StatTotal(me, Stat.Health) - ShardHealth();

        switch (champ.Id)
        {
            case "Veigar":
            {
                double fromStacks = Math.Max(0, stats.AbilityPower - items.StatTotal(me, Stat.AbilityPower) - gloryAp - Shards(AdaptiveShard) * 9);
                return new StackInfo("Phenomenal Evil", $"≈ {fromStacks:0} stacks", $"+{fromStacks:0} ability power",
                    "Every stack is 1 AP for the rest of the game. Hit champions with abilities and kill units with Q to stack.");
            }
            case "Senna":
            {
                double range = stats.AttackRange, bonusRange = Math.Max(0, range - champ.BaseStats.GetValueOrDefault("attackrange"));
                double crit = Math.Max(0, stats.CritPercent - items.StatTotal(me, Stat.CritChance));
                return new StackInfo("Absolution · Mist", $"Range {range:0}", $"+{bonusRange:0} range · +{crit:0}% crit from Mist",
                    "Collect Mist from fallen enemies and your own attacks: each gives attack damage and crit chance, and your range grows as you collect more.");
            }
            case "Chogath":
                return new StackInfo("Feast", $"+{Math.Max(0, BonusHealth()):N0} max health", "kept for the whole game",
                    "Every champion or large monster eaten with Feast (R) adds max health, size and range.");
            case "Sion":
                return new StackInfo("Soul Furnace", $"+{Math.Max(0, BonusHealth()):N0} max health", "kept for the whole game",
                    "Units you kill add max health permanently; large minions and champions give more.");
            case "Thresh":
            {
                double bonusArmor = Math.Max(0, stats.Armor - champ.StatAt("armor", level) - items.StatTotal(me, Stat.Armor));
                return new StackInfo("Damnation · Souls", $"+{bonusArmor:0} armor", $"and about +{bonusArmor:0} ability power",
                    "Collect the souls enemies drop near you: each gives armor and ability power for the rest of the game.");
            }
            default:
                return null;
        }
    }
}
