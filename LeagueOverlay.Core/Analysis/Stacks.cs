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
    private double _lastTime;
    private FeastCount? _feast;

    /// <summary>Cho'Gath's Feast stacks by source, since minions and small monsters can only give a few.</summary>
    private sealed class FeastCount
    {
        public int Stacks, FromSmall;
    }

    /// <summary>Feast: max health per stack at R rank 1/2/3 (applies to all stacks), and the stacks minions and non-epic monsters can give.</summary>
    public static readonly int[] FeastHealthPerStack = [80, 120, 160];
    public const int FeastSmallUnitCap = 6;

    public StackReport Update(LiveGame game, StaticData data)
    {
        if (game.Me is not { } me || game.ActivePlayer is not { } active || game.GameData is not { } gd) return StackReport.None;
        // A new game: another champion, or the game clock went back. (Not the player list: it can fill in late while loading.)
        string gameKey = me.ChampionKey;
        if (gameKey != _gameKey || gd.GameTime < _lastTime - 5) { _gameKey = gameKey; _sealSince = _mejaiSince = null; _feast = null; }
        double previousTime = _lastTime;
        _lastTime = gd.GameTime;

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

        if (active.ChampionStats is { } stats && data.Champion(me.ChampionKey) is { } champ)
        {
            var champLine = champ.Id == "Chogath"
                ? Feast(game, me, champ, stats, active, data.Items, previousTime)
                : ChampionStack(champ, me, stats, active.FullRunes, data.Items, gloryAp);
            if (champLine is not null) lines.Add(champLine);
        }

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

    /// <summary>
    /// Cho'Gath's Feast: stacks = bonus health / health per stack at the current R rank. Each new stack is put down to a
    /// champion or epic monster when you got such a kill since the last read, otherwise to a minion or small monster
    /// (those only count for the first 6). Stacks already there when the app started are assumed to be small ones first.
    /// </summary>
    private StackInfo? Feast(LiveGame game, Player me, ChampionInfo champ, ChampionStats stats, ActivePlayer active, ItemCatalog items, double since)
    {
        int rank = active.Abilities?.R?.AbilityLevel ?? 0;
        const string meaning = "Champions and epic monsters (dragons, Rift Herald, Baron, Voidgrubs) always give a stack; minions and other monsters only until 6.";
        if (rank <= 0) return new StackInfo("Feast", "No stacks yet", "learn Feast (R) at level 6", meaning);
        int level = Math.Max(1, me.Level);
        int Shards(int id) => active.FullRunes?.StatRunes.Count(r => r.Id == id) ?? 0;
        double shardHealth = Shards(HealthShard) * 65 + Shards(HealthScalingShard) * (10 + 170.0 * (level - 1) / 17);
        double bonus = Math.Max(0, stats.MaxHealth - champ.StatAt("hp", level) - items.StatTotal(me, Stat.Health) - shardHealth);
        int stacks = (int)Math.Round(bonus / FeastHealthPerStack[Math.Min(rank, 3) - 1]);

        if (_feast is null)
        {
            _feast = new FeastCount { Stacks = stacks, FromSmall = Math.Min(stacks, FeastSmallUnitCap) };
        }
        else if (stacks > _feast.Stacks)
        {
            int added = stacks - _feast.Stacks;
            int bigKills = BigKills(game, me, since - 3);
            int small = Math.Max(0, added - bigKills);
            _feast.FromSmall = Math.Min(FeastSmallUnitCap, _feast.FromSmall + small);
            _feast.Stacks = stacks;
        }
        int left = FeastSmallUnitCap - _feast.FromSmall;
        return new StackInfo("Feast", $"{_feast.Stacks} stacks", $"+{bonus:N0} max health",
            $"Minions & monsters: {_feast.FromSmall} of {FeastSmallUnitCap} used, " +
            (left > 0 ? $"{left} left. " : "none left: only champions and epic monsters now. ") + meaning);
    }

    /// <summary>Your champion kills and epic monster kills since a game time.</summary>
    private static int BigKills(LiveGame game, Player me, double since)
    {
        var names = new[] { me.RiotIdGameName, me.SummonerName, me.RiotId }
            .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!.Split('#')[0].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (game.Events?.Events ?? []).Count(e => e.EventTime > since &&
            e.EventName is "ChampionKill" or "DragonKill" or "HeraldKill" or "BaronKill" or "HordeKill" &&
            e.KillerName is { } k && names.Contains(k.Split('#')[0].Trim()));
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
