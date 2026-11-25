using System.Text.RegularExpressions;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Core.Analysis;

public sealed record Recommendation(int ItemId, string Name, int TotalCost, int RemainingCost, string Reason);

/// <param name="Reminder">A one-off shopping reminder, e.g. the trinket swap for your role.</param>
/// <param name="ReminderItemId">The item the reminder is about (for its icon).</param>
/// <param name="ReminderUrgent">The reminder is a warning (e.g. nothing bought at the start), drawn more loudly.</param>
public sealed record ItemAdvice(Recommendation? Core, ValueList<Recommendation> Situational, string? BuyNow, string? Reminder = null, int? ReminderItemId = null,
    bool ReminderUrgent = false)
{
    public static readonly ItemAdvice None = new(null, ValueList<Recommendation>.Empty, null);
    public bool IsEmpty => Core is null && Situational.Count == 0 && Reminder is null;
}

public enum BuildStyle { Marksman, Mage, AdAssassin, Fighter, Tank, Enchanter }

public sealed record ThreatProfile(double ApShare, ValueList<string> Tanks, ValueList<string> Healers, ValueList<string> CritStackers)
{
    public double AdShare => 1 - ApShare;
}

/// <summary>
/// Item suggestions for the active player.
/// With popular-build data (<see cref="ChampionBuild"/>): the next item is the next step of the most common build
/// on this champion and role, situational picks are limited to items players actually build on it, and boots follow
/// what's popular unless the enemy team calls for a common alternative.
/// Without it (no API key, or too few games): rule-based, from stat weights for the champion's class, adjusted for
/// what the enemy team is doing (damage split, tanks, healing, crit) and the lane state.
/// </summary>
public static class ItemRecommender
{
    // A "typical" amount of each stat on one item, so stats can be added together on one scale.
    internal static readonly Dictionary<Stat, double> Norm = new()
    {
        [Stat.AttackDamage] = 40, [Stat.AbilityPower] = 80, [Stat.Health] = 350, [Stat.Armor] = 40,
        [Stat.MagicResist] = 40, [Stat.AttackSpeed] = 30, [Stat.CritChance] = 25, [Stat.Lethality] = 15,
        [Stat.ArmorPenPercent] = 30, [Stat.MagicPenFlat] = 15, [Stat.MagicPenPercent] = 30, [Stat.AbilityHaste] = 15,
        [Stat.LifeSteal] = 10, [Stat.Omnivamp] = 8, [Stat.HealShieldPower] = 10, [Stat.Mana] = 400,
        [Stat.ManaRegen] = 75, [Stat.MoveSpeed] = 45, [Stat.Tenacity] = 30, [Stat.HealthRegen] = 100,
    };

    // Champions whose kits heal a lot (Data Dragon ids). Item-based healing is detected separately.
    private static readonly HashSet<string> HealingChampions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Aatrox", "Soraka", "Vladimir", "Sylas", "Warwick", "DrMundo", "Yuumi", "Fiddlesticks", "Swain",
        "Nidalee", "Briar", "Olaf", "Illaoi", "Sona", "Nami", "Renata", "Maokai", "Volibear",
    };

    // Anti-tank effects described in passives rather than stats: %-health damage, armor/MR shred.
    private static readonly Regex AntiTank = new(
        @"% (of )?(the )?(target's )?(max(imum)?|current) Health|reduc\w* (the target's )?(Armor|Magic Resist)|(Armor|Magic Resist) by \d+%",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AntiCrit = new(@"critical strike|attack speed", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>With build data, situational picks must be built in at least this share of games on the champion.</summary>
    public const double MinSituationalShare = 0.03;
    /// <summary>A threat-fitting boots choice replaces the most popular boots if at least this share of players use it.</summary>
    public const double MinThreatBootsShare = 0.10;

    public const int LaneSwing = 500; // lead/deficit (gold-equivalent) before the lane state changes priorities

    public const int FarsightId = 3363, OracleLensId = 3364;
    public const int FarsightLevel = 9;
    /// <summary>Fallback for the sweeper reminder if the support item's upgrade can't be seen.</summary>
    public const double SweeperFallbackSeconds = 6 * 60;
    /// <summary>Junglers: swap to Oracle Lens around the first back.</summary>
    public const double JungleSweeperSeconds = 4 * 60;
    /// <summary>Champions that jump to their own wards (Lee Sin's W, Jax's Q, Katarina's E): they keep the Stealth Ward.</summary>
    public static readonly HashSet<string> WardHoppers = new(StringComparer.OrdinalIgnoreCase) { "LeeSin", "Jax", "Katarina" };

    /// <summary>
    /// Trinket swap for your role: Farsight Alteration from level 9 in mid / bot (ADC),
    /// Oracle Lens as support once the support item has upgraded (usually the first back).
    /// </summary>
    public static string? TrinketReminder(LiveGame game, Player me, ItemCatalog items) => TrinketReminder(game, me, items, out _);

    public static string? TrinketReminder(LiveGame game, Player me, ItemCatalog items, out int itemId)
    {
        itemId = 0;
        bool Owns(int id) => me.Items.Any(i => i.ItemID == id);
        string Name(int id, string fallback) => items.Get(id)?.Name ?? fallback;
        switch (me.Position?.ToUpperInvariant())
        {
            case "MIDDLE" or "BOTTOM" when me.Level >= FarsightLevel && !Owns(FarsightId):
                itemId = FarsightId;
                return $"Level {FarsightLevel}: swap trinket to {Name(FarsightId, "Farsight Alteration")}";
            case "JUNGLE" when !Owns(OracleLensId) && !WardHoppers.Contains(me.ChampionKey) && (game.GameData?.GameTime ?? 0) >= JungleSweeperSeconds:
                itemId = OracleLensId;
                return $"Swap trinket to {Name(OracleLensId, "Oracle Lens")}: sweep the enemy wards around your camps and objectives";
            case "UTILITY" when !Owns(OracleLensId):
                bool upgraded = me.Items.Any(i => items.Get(i.ItemID) is { IsSupportItem: true } s && (!s.Purchasable || s.From.Count > 0));
                itemId = OracleLensId;
                return upgraded || (game.GameData?.GameTime ?? 0) >= SweeperFallbackSeconds
                    ? $"Swap trinket to {Name(OracleLensId, "Oracle Lens")} (sweeper)"
                    : null;
            default:
                return null;
        }
    }

    /// <param name="darkSealFull">Dark Seal has full Glory (from <see cref="StackTracker"/>): remind AP champions to upgrade it.</param>
    /// <param name="surprise">Surprise build mode: follow this plan instead of the usual suggestions.</param>
    public static ItemAdvice Recommend(LiveGame game, StaticData data, int? myLaneScore = null, ChampionBuild? popular = null,
        bool darkSealFull = false, SurprisePlan? surprise = null)
    {
        var advice = RecommendItems(game, data, myLaneScore, popular, darkSealFull, surprise);
        // Start of the game: the starting items, and a warning if nothing was bought. Takes priority over other reminders.
        if (game.Me is { } me && data.IsLoaded && data.Champion(me.ChampionKey) is { } champ)
        {
            var (style, ap) = surprise is not null && string.Equals(surprise.ChampionKey, champ.Id, StringComparison.OrdinalIgnoreCase)
                ? StarterItems.ForTheme(surprise.Theme.Key) : Profile(champ, me, data.Items);
            var starter = StarterItems.Best(surprise is null ? popular : null, me.Position, style, ap, me.HasSmite);
            if (StarterItems.Reminder(game, me, data.Items, starter) is { } start)
                advice = advice with { Reminder = start.Text, ReminderItemId = start.ItemId, ReminderUrgent = start.Urgent };
        }
        return advice;
    }

    private static ItemAdvice RecommendItems(LiveGame game, StaticData data, int? myLaneScore, ChampionBuild? popular,
        bool darkSealFull, SurprisePlan? surprise)
    {
        if (game.Me is not { } me || game.ActivePlayer is not { } active) return ItemAdvice.None;
        var reminder = TrinketReminder(game, me, data.Items, out int reminderItem);
        int? reminderItemId = reminder is null ? null : reminderItem;
        if (!data.IsLoaded || data.Champion(me.ChampionKey) is not { } champ)
            return reminder is null ? ItemAdvice.None : ItemAdvice.None with { Reminder = reminder, ReminderItemId = reminderItemId };

        var items = data.Items;
        var (style, ap) = Profile(champ, me, items);

        // Full Dark Seal on an AP champion: upgrading to Mejai's keeps the Glory and raises the cap. Takes priority over the trinket.
        if (darkSealFull && ap && items.Get(StackTracker.DarkSealId) is { } seal && seal.Into.Select(items.Get).FirstOrDefault() is { } mejai)
        {
            int cost = items.RemainingCost(mejai, me.Items.Select(i => i.ItemID));
            reminder = $"Dark Seal is full: upgrade to {mejai.Name} ({cost:N0}g)";
            reminderItemId = mejai.Id;
        }

        if (surprise is not null && string.Equals(surprise.ChampionKey, champ.Id, StringComparison.OrdinalIgnoreCase))
            return SurpriseAdvice(surprise, me, active, items, reminder, reminderItemId);
        var threats = AnalyzeThreats(GoldAnalyzer.Enemies(game), data);
        var weights = Weights(style, ap, myLaneScore);
        bool squishy = style is BuildStyle.Marksman or BuildStyle.Mage or BuildStyle.AdAssassin or BuildStyle.Enchanter;

        var ownedIds = me.Items.SelectMany(i => Enumerable.Repeat(i.ItemID, Math.Max(1, i.Count))).ToList();
        var ownedCompleted = me.Items.Select(i => items.Get(i.ItemID)).OfType<ItemInfo>().Where(i => i.IsCompleted).ToList();
        var ownedNames = ownedCompleted.Select(i => i.Name).ToHashSet();
        var ownedGroups = ownedCompleted.SelectMany(i => i.UniqueGroups).ToHashSet();
        // Checked on the raw inventory: the bot lane role quest moves boots to their own slot and can upgrade them
        // into ids Data Dragon doesn't list, which ownedCompleted would miss.
        bool hasBoots = me.Items.Any(items.IsUpgradedBoots);

        Scored Score(ItemInfo item)
        {
            double baseScore = weights.Sum(w => w.Value * item.Get(w.Key) / Norm[w.Key]) * Math.Sqrt(3000.0 / Math.Max(item.TotalCost, 1000));
            var (bonus, antiHeal, reason) = Situational(item, baseScore, threats, squishy, ap);
            return new Scored(item, baseScore, bonus, antiHeal, reason);
        }

        bool Allowed(ItemInfo item) => !ownedNames.Contains(item.Name) && !item.UniqueGroups.Overlaps(ownedGroups);

        bool usePopular = popular is { Games: >= BuildLibrary.MinGames };

        var candidates = items.ShopCompleted
            .Where(i => !i.IsBoots && Allowed(i) && Fits(i, style, ap))
            .Select(Score)
            .Where(s => s.Base > 0.3)
            .OrderByDescending(s => s.Total)
            .ToList();

        // Core item: next step of the most common build, else the item most often built that's still missing,
        // else the rule-based best fit. Anti-heal is a situational pick, so it never counts toward the rule-based core.
        Scored? core = null;
        string? coreReason = null;
        if (usePopular)
        {
            for (int step = 0; step < popular!.Path.Count && core is null; step++)
            {
                if (items.Get(popular.Path[step]) is not { InShop: true } item || !Allowed(item)) continue;
                core = Score(item);
                coreReason = $"Most common {Ordinal(step + 1)} item · {Pct(popular.PathShare[step])} of {popular.Games:N0} games";
            }
            if (core is null)
            {
                var (item, share) = popular.ItemShare.OrderByDescending(kv => kv.Value)
                    .Select(kv => (Item: items.Get(kv.Key), Share: kv.Value))
                    .FirstOrDefault(x => x.Item is { InShop: true } i && Allowed(i) && x.Share >= MinSituationalShare);
                if (item is not null)
                {
                    core = Score(item);
                    coreReason = $"Built in {Pct(share)} of {popular.Games:N0} games";
                }
            }
        }
        if (core is null)
        {
            core = candidates.MaxBy(s => s.Total - s.AntiHeal);
            if (core is not null) coreReason = core.Reason ?? $"Best fit for {Label(style, ap)}";
        }
        var picked = new List<Scored>();
        if (core is not null) picked.Add(core);

        var situational = new List<Recommendation>();
        if (!hasBoots)
        {
            var bootsOptions = items.ShopCompleted.Where(i => i.IsUpgradedBoots && i.Depth == 2 && Allowed(i)).Select(Score).ToList();
            if (usePopular && popular!.BootsShare.Count > 0)
            {
                double BootsShare(Scored b) => popular.BootsShare.GetValueOrDefault(b.Item.Id);
                var forThreats = bootsOptions.Where(b => b.Bonus > 0 && BootsShare(b) >= MinThreatBootsShare).MaxBy(b => b.Total);
                var mostCommon = bootsOptions.MaxBy(BootsShare);
                if (forThreats is not null && forThreats != mostCommon)
                    situational.Add(ToRec(forThreats, $"{forThreats.Reason} · {Pct(BootsShare(forThreats))} pick these"));
                else if (mostCommon is not null && BootsShare(mostCommon) > 0)
                    situational.Add(ToRec(mostCommon, $"Most common boots · {Pct(BootsShare(mostCommon))}"));
            }
            else if (bootsOptions.MaxBy(s => s.Total) is { } boots)
            {
                situational.Add(ToRec(boots, boots.Reason ?? "No boots yet"));
            }
        }
        foreach (var s in candidates.Where(s => s.Item.Id != core?.Item.Id && s.Bonus > 0))
        {
            if (situational.Count >= 2) break;
            if (picked.Any(p => p.Item.UniqueGroups.Overlaps(s.Item.UniqueGroups))) continue; // can't own both
            string reason = s.Reason!;
            if (usePopular)
            {
                double share = popular!.Share(s.Item.Id);
                if (share < MinSituationalShare) continue; // the rules like it, but players on this champion don't build it
                reason += $" · {Pct(share)} build it";
            }
            picked.Add(s);
            situational.Add(ToRec(s, reason));
        }

        string? buyNow = null;
        Recommendation? coreRec = null;
        if (core is not null)
        {
            if (myLaneScore < -LaneSwing && squishy && !usePopular) coreReason += " · behind in lane";
            coreRec = ToRec(core, coreReason!);
            buyNow = BuyNow(core.Item, (int)active.CurrentGold);
        }
        return new ItemAdvice(coreRec, new ValueList<Recommendation>(situational), buyNow, reminder, reminderItemId);

        Recommendation ToRec(Scored s, string reason) =>
            new(s.Item.Id, s.Item.Name, s.Item.TotalCost, items.RemainingCost(s.Item, ownedIds), reason);

        string BuyNow(ItemInfo target, int gold)
        {
            int remaining = items.RemainingCost(target, ownedIds);
            if (remaining <= gold) return $"Complete {target.Name} now ({remaining:N0}g)";
            var step = items.Components(target)
                .DistinctBy(c => c.Id)
                .Where(c => !ownedIds.Contains(c.Id))
                .Select(c => (Item: c, Cost: items.RemainingCost(c, ownedIds)))
                .Where(x => x.Cost > 0 && x.Cost <= gold)
                .OrderByDescending(x => x.Cost)
                .FirstOrDefault();
            return step.Item is not null
                ? $"Buy {step.Item.Name} ({step.Cost:N0}g) → {target.Name}"
                : $"{remaining - gold:N0}g more for {target.Name}";
        }
    }

    /// <summary>Surprise mode: the next item of the plan you don't own yet, plus the plan's boots if you have none.</summary>
    private static ItemAdvice SurpriseAdvice(SurprisePlan plan, Player me, ActivePlayer active, ItemCatalog items, string? reminder, int? reminderItemId)
    {
        var owned = me.Items.Select(i => items.Get(i.ItemID)).OfType<ItemInfo>().Where(i => i.IsCompleted).ToList();
        var ownedNames = owned.Select(i => i.Name).ToHashSet();
        var ownedGroups = owned.SelectMany(i => i.UniqueGroups).ToHashSet();
        var ownedIds = me.Items.Select(i => i.ItemID).ToList();
        Recommendation Rec(ItemInfo item, string reason) => new(item.Id, item.Name, item.TotalCost, items.RemainingCost(item, ownedIds), reason);

        Recommendation? core = null;
        for (int i = 0; i < plan.Path.Count && core is null; i++)
            if (items.Get(plan.Path[i]) is { } item && !ownedNames.Contains(item.Name) && !item.UniqueGroups.Overlaps(ownedGroups))
                core = Rec(item, $"Surprise build: {plan.Title} · item {i + 1} of {plan.Path.Count}");

        var situational = new List<Recommendation>();
        if (!me.Items.Any(items.IsUpgradedBoots) && items.Get(plan.Boots) is { } boots) situational.Add(Rec(boots, "Surprise build boots"));
        if (core is null && reminder is null) reminder = $"Surprise build complete: you're a full {plan.Title}!";

        string? buyNow = null;
        if (core is not null && items.Get(core.ItemId) is { } target)
        {
            int gold = (int)active.CurrentGold;
            buyNow = core.RemainingCost <= gold ? $"Complete {target.Name} now ({core.RemainingCost:N0}g)"
                : items.Components(target).DistinctBy(c => c.Id).Where(c => !ownedIds.Contains(c.Id))
                    .Select(c => (Item: c, Cost: items.RemainingCost(c, ownedIds))).Where(x => x.Cost > 0 && x.Cost <= gold)
                    .OrderByDescending(x => x.Cost).FirstOrDefault() is { Item: not null } step
                    ? $"Buy {step.Item.Name} ({step.Cost:N0}g) → {target.Name}"
                    : $"{core.RemainingCost - gold:N0}g more for {target.Name}";
        }
        return new ItemAdvice(core, new ValueList<Recommendation>(situational), buyNow, reminder, reminderItemId);
    }

    private sealed record Scored(ItemInfo Item, double Base, double Bonus, double AntiHeal, string? Reason)
    {
        public double Total => Base + Bonus;
    }

    /// <summary>Keeps off-type items out: no AD items for mages, no pure tank items for carries, etc.</summary>
    public static bool Fits(ItemInfo item, BuildStyle style, bool ap)
    {
        bool hasAd = item.Get(Stat.AttackDamage) > 0, hasAp = item.Get(Stat.AbilityPower) > 0;
        bool adStats = hasAd || item.Get(Stat.CritChance) > 0 || item.Get(Stat.AttackSpeed) > 0 || item.Get(Stat.Lethality) > 0;
        bool defensive = item.Get(Stat.Health) > 0 || item.Get(Stat.Armor) > 0 || item.Get(Stat.MagicResist) > 0;
        return style switch
        {
            BuildStyle.Marksman => adStats && !(hasAp && !hasAd),
            BuildStyle.AdAssassin => hasAd,
            BuildStyle.Mage => hasAp,
            BuildStyle.Fighter => ap ? hasAp || (item.Get(Stat.Health) > 0 && !hasAd) : hasAd || (item.Get(Stat.Health) > 0 && !hasAp),
            BuildStyle.Tank => defensive && !hasAd && !hasAp,
            BuildStyle.Enchanter => (item.Get(Stat.HealShieldPower) > 0 || item.Get(Stat.ManaRegen) > 0 || hasAp) && !hasAd,
            _ => true,
        };
    }

    /// <summary>How a champion is usually built, before any items (e.g. in champion select).</summary>
    public static (BuildStyle Style, bool Ap) UsualProfile(ChampionInfo champ) => StyleOf(champ, champ.Magic > champ.Attack);

    public static (BuildStyle Style, bool Ap) Profile(ChampionInfo champ, Player me, ItemCatalog items)
    {
        bool ap = champ.Magic > champ.Attack;
        double ownAp = items.StatTotal(me, Stat.AbilityPower) / Norm[Stat.AbilityPower];
        double ownAd = items.StatTotal(me, Stat.AttackDamage) / Norm[Stat.AttackDamage];
        if (ownAp + ownAd >= 0.75) ap = ownAp > ownAd; // what they've actually built wins
        return StyleOf(champ, ap);
    }

    private static (BuildStyle Style, bool Ap) StyleOf(ChampionInfo champ, bool ap)
    {
        var style = champ.PrimaryTag switch
        {
            "Marksman" => ap ? BuildStyle.Mage : BuildStyle.Marksman,
            "Mage" => BuildStyle.Mage,
            "Assassin" => ap ? BuildStyle.Mage : BuildStyle.AdAssassin,
            "Tank" => BuildStyle.Tank,
            "Support" => champ.HasTag("Tank") ? BuildStyle.Tank : champ.HasTag("Mage") && ap ? BuildStyle.Mage : BuildStyle.Enchanter,
            _ => BuildStyle.Fighter,
        };
        return (style, ap);
    }

    private static Dictionary<Stat, double> Weights(BuildStyle style, bool ap, int? laneScore)
    {
        var w = style switch
        {
            BuildStyle.Marksman => new Dictionary<Stat, double>
            {
                [Stat.AttackDamage] = 1.0, [Stat.CritChance] = 1.0, [Stat.AttackSpeed] = 0.9,
                [Stat.LifeSteal] = 0.4, [Stat.ArmorPenPercent] = 0.5,
            },
            BuildStyle.Mage => new()
            {
                [Stat.AbilityPower] = 1.0, [Stat.AbilityHaste] = 0.6, [Stat.MagicPenFlat] = 0.7,
                [Stat.MagicPenPercent] = 0.7, [Stat.Mana] = 0.25, [Stat.Health] = 0.15,
            },
            BuildStyle.AdAssassin => new()
            {
                [Stat.AttackDamage] = 1.0, [Stat.Lethality] = 1.0, [Stat.AbilityHaste] = 0.5, [Stat.ArmorPenPercent] = 0.3,
            },
            BuildStyle.Tank => new()
            {
                [Stat.Health] = 1.0, [Stat.Armor] = 0.8, [Stat.MagicResist] = 0.8, [Stat.AbilityHaste] = 0.4,
            },
            BuildStyle.Enchanter => new()
            {
                [Stat.HealShieldPower] = 1.0, [Stat.AbilityPower] = 0.5, [Stat.AbilityHaste] = 0.7,
                [Stat.ManaRegen] = 0.6, [Stat.Mana] = 0.2,
            },
            _ when ap => new()
            {
                [Stat.AbilityPower] = 0.9, [Stat.Health] = 0.7, [Stat.AbilityHaste] = 0.6, [Stat.Omnivamp] = 0.4,
                [Stat.MagicPenFlat] = 0.3, [Stat.MagicPenPercent] = 0.3, [Stat.Armor] = 0.2, [Stat.MagicResist] = 0.2,
            },
            _ => new()
            {
                [Stat.AttackDamage] = 0.9, [Stat.Health] = 0.7, [Stat.AbilityHaste] = 0.6, [Stat.Omnivamp] = 0.4,
                [Stat.LifeSteal] = 0.2, [Stat.ArmorPenPercent] = 0.3, [Stat.Armor] = 0.2, [Stat.MagicResist] = 0.2,
            },
        };

        // Behind in lane: value survivability more. Ahead: press the damage advantage.
        Stat[] damage = [Stat.AttackDamage, Stat.AbilityPower, Stat.CritChance, Stat.Lethality];
        if (laneScore < -LaneSwing)
        {
            foreach (var s in damage) if (w.ContainsKey(s)) w[s] *= 0.9;
            foreach (var s in new[] { Stat.Health, Stat.Armor, Stat.MagicResist }) w[s] = w.GetValueOrDefault(s) + 0.3;
        }
        else if (laneScore > LaneSwing)
        {
            foreach (var s in damage) if (w.ContainsKey(s)) w[s] *= 1.1;
        }
        return w;
    }

    public static ThreatProfile AnalyzeThreats(IEnumerable<Player> enemies, StaticData data)
    {
        var list = enemies.ToList();
        var items = data.Items;
        double avgGold = list.Count > 0 ? Math.Max(1, list.Average(p => items.ItemGold(p))) : 1;
        double sumW = 0, sumAp = 0;
        var tanks = new List<string>();
        var healers = new List<string>();
        var crit = new List<string>();

        foreach (var e in list)
        {
            var champ = data.Champion(e.ChampionKey);
            string name = e.ChampionName ?? champ?.Name ?? "?";
            double ad = items.StatTotal(e, Stat.AttackDamage) / Norm[Stat.AttackDamage];
            double apv = items.StatTotal(e, Stat.AbilityPower) / Norm[Stat.AbilityPower];
            double apFrac = ad + apv >= 0.5 ? apv / (ad + apv)
                : champ is not null ? champ.Magic / (double)Math.Max(1, champ.Magic + champ.Attack)
                : 0.5;

            // Fed enemies matter more; tanks deal less damage.
            double w = 1 + items.ItemGold(e) / avgGold;
            if (champ?.PrimaryTag == "Tank") w *= 0.5;
            sumW += w;
            sumAp += w * apFrac;

            double hp = items.StatTotal(e, Stat.Health);
            double resists = items.StatTotal(e, Stat.Armor) + items.StatTotal(e, Stat.MagicResist);
            if (champ?.PrimaryTag == "Tank" || hp >= 1000 || resists >= 120) tanks.Add(name);

            if ((champ is not null && HealingChampions.Contains(champ.Id)) ||
                items.StatTotal(e, Stat.LifeSteal) >= 10 || items.StatTotal(e, Stat.Omnivamp) >= 8 ||
                items.StatTotal(e, Stat.HealShieldPower) >= 15)
                healers.Add(name);

            if (items.StatTotal(e, Stat.CritChance) >= 40 || items.StatTotal(e, Stat.AttackSpeed) >= 70) crit.Add(name);
        }

        return new ThreatProfile(sumW > 0 ? sumAp / sumW : 0.5,
            new ValueList<string>(tanks), new ValueList<string>(healers), new ValueList<string>(crit));
    }

    private static (double Bonus, double AntiHeal, string? Reason) Situational(ItemInfo item, double baseScore, ThreatProfile t, bool squishy, bool ap)
    {
        var parts = new List<(double Bonus, string Reason)>();
        bool useful = baseScore > 0.3; // the item already suits this champion
        double defenseScale = squishy ? 0.5 : 0.8;
        double antiHeal = 0;

        if (t.AdShare >= 0.65 && item.Get(Stat.Armor) > 0)
            parts.Add((defenseScale * Math.Min(item.Get(Stat.Armor) / 40, 1.5), $"{t.AdShare:P0} of enemy damage is physical"));
        if (t.ApShare >= 0.65 && item.Get(Stat.MagicResist) > 0)
            parts.Add((defenseScale * Math.Min(item.Get(Stat.MagicResist) / 40, 1.5), $"{t.ApShare:P0} of enemy damage is magic"));

        if (t.Healers.Count > 0 && item.AppliesWounds && useful)
        {
            antiHeal = 2.0;
            parts.Add((antiHeal, $"{Names(t.Healers)} heal → anti-heal"));
        }

        if (t.Tanks.Count >= 2 && useful)
        {
            bool pen = ap ? item.Get(Stat.MagicPenPercent) > 0 || item.Get(Stat.MagicPenFlat) > 0 : item.Get(Stat.ArmorPenPercent) > 0;
            bool dealsDamage = item.Get(Stat.AttackDamage) > 0 || item.Get(Stat.AbilityPower) > 0;
            if (pen || (dealsDamage && AntiTank.IsMatch(item.Text)))
                parts.Add((1.0, $"{t.Tanks.Count} tanky enemies → penetration"));
        }

        if (t.CritStackers.Count >= 2 && !squishy && item.Get(Stat.Armor) > 0 && item.Get(Stat.AttackSpeed) == 0 && AntiCrit.IsMatch(item.Text))
            parts.Add((0.8, $"{Names(t.CritStackers)} build crit/attack speed"));

        if (parts.Count == 0) return (0, 0, null);
        return (parts.Sum(p => p.Bonus), antiHeal, parts.MaxBy(p => p.Bonus).Reason);
    }

    private static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th" };

    private static string Pct(double share) => $"{Math.Round(share * 100):0}%";

    private static string Names(IReadOnlyList<string> names) =>
        names.Count <= 2 ? string.Join(" & ", names) : $"{names[0]}, {names[1]} +{names.Count - 2}";

    private static string Label(BuildStyle style, bool ap) => style switch
    {
        BuildStyle.Marksman => "marksman",
        BuildStyle.Mage => "AP damage",
        BuildStyle.AdAssassin => "AD assassin",
        BuildStyle.Tank => "tank",
        BuildStyle.Enchanter => "enchanter",
        _ => ap ? "AP fighter" : "fighter",
    };
}
