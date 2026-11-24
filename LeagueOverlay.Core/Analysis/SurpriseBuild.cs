using LeagueOverlay.Core.Data;

namespace LeagueOverlay.Core.Analysis;

/// <summary>A style of build for surprise mode: which stats its items must have and how to rank them.</summary>
public sealed record SurpriseTheme(
    string Key, string Label, string Pitch,
    Stat[] Required, IReadOnlyDictionary<Stat, double> Weights, string[] Boots,
    Func<ChampionInfo, bool> Suits);

/// <summary>An unexpected build for one champion: the style, five items in order and boots. Same seed, same plan.</summary>
public sealed record SurprisePlan(string ChampionKey, string ChampionName, SurpriseTheme Theme, ValueList<int> Path, int Boots, int Seed)
{
    public string Title => $"{Theme.Label} {ChampionName}";
}

/// <summary>
/// Surprise build mode: picks a style that isn't how the champion is usually built but that it still gets something from
/// (AP Ezreal, healing Senna, tank Jinx...), then a semi-random path of items with that style's stats.
/// Which styles suit a champion comes from its Data Dragon ratings (attack / magic / defense) and tags.
/// </summary>
public static class SurpriseBuilder
{
    public const int PathLength = 5;

    private static readonly HashSet<string> Healers = new(StringComparer.OrdinalIgnoreCase)
        { "Senna", "Soraka", "Sona", "Nami", "Yuumi", "Seraphine", "Lulu", "Janna", "Karma", "Milio", "Renata", "Taric", "Bard", "Ivern", "Kayle" };

    public static readonly IReadOnlyList<SurpriseTheme> Themes =
    [
        new("ap", "AP", "Every item adds ability power, so the abilities hit far harder than anyone expects.",
            [Stat.AbilityPower], new Dictionary<Stat, double> { [Stat.AbilityPower] = 1, [Stat.AbilityHaste] = 0.5, [Stat.MagicPenPercent] = 0.6, [Stat.MagicPenFlat] = 0.6 },
            ["Sorcerer's Shoes"], c => c.Magic >= 2),
        new("crit", "Crit", "Big crits from a champion nobody expects them from.",
            [Stat.CritChance], new Dictionary<Stat, double> { [Stat.AttackDamage] = 1, [Stat.CritChance] = 1, [Stat.AttackSpeed] = 0.6 },
            ["Berserker's Greaves"], c => c.Attack >= 4),
        new("onhit", "On-hit", "Attack speed and on-hit effects: lots of very fast autos.",
            [Stat.AttackSpeed], new Dictionary<Stat, double> { [Stat.AttackSpeed] = 1, [Stat.AttackDamage] = 0.4, [Stat.AbilityPower] = 0.3 },
            ["Berserker's Greaves"], c => c.Attack >= 3),
        new("lethality", "Lethality", "Armor-ignoring burst: pick a target and delete them.",
            [Stat.Lethality], new Dictionary<Stat, double> { [Stat.Lethality] = 1, [Stat.AttackDamage] = 1, [Stat.AbilityHaste] = 0.4 },
            ["Boots of Swiftness", "Ionian Boots of Lucidity"], c => c.Attack >= 4),
        new("tank", "Tank", "An unkillable frontline: health, armor and magic resist.",
            [Stat.Health], new Dictionary<Stat, double> { [Stat.Health] = 1, [Stat.Armor] = 0.8, [Stat.MagicResist] = 0.8, [Stat.AbilityHaste] = 0.3 },
            ["Plated Steelcaps", "Mercury's Treads"], _ => true),
        new("bruiser", "Bruiser", "Damage and health: stay in fights long enough to win them.",
            [Stat.AttackDamage, Stat.Health], new Dictionary<Stat, double> { [Stat.AttackDamage] = 0.8, [Stat.Health] = 0.9, [Stat.AbilityHaste] = 0.5, [Stat.Omnivamp] = 0.4 },
            ["Plated Steelcaps", "Mercury's Treads", "Ionian Boots of Lucidity"], c => c.Attack >= 3),
        new("healer", "Healing", "Heal and shield your team through every fight.",
            [Stat.HealShieldPower], new Dictionary<Stat, double> { [Stat.HealShieldPower] = 1, [Stat.AbilityHaste] = 0.6, [Stat.ManaRegen] = 0.5, [Stat.AbilityPower] = 0.3 },
            ["Ionian Boots of Lucidity", "Boots of Swiftness"], c => c.HasTag("Support") || Healers.Contains(c.Id) || c.Magic >= 6),
        new("battlemage", "Battlemage", "Ability power plus health: brawl with your spells.",
            [Stat.AbilityPower, Stat.Health], new Dictionary<Stat, double> { [Stat.AbilityPower] = 0.8, [Stat.Health] = 0.8, [Stat.AbilityHaste] = 0.4 },
            ["Sorcerer's Shoes", "Mercury's Treads"], c => c.Magic >= 4),
    ];

    /// <summary>How the champion is normally built, as a theme key (so surprise mode never picks it).</summary>
    public static string UsualTheme(ChampionInfo champ)
    {
        bool ap = champ.Magic > champ.Attack;
        return champ.PrimaryTag switch
        {
            "Marksman" => ap ? "ap" : "crit",
            "Mage" => "ap",
            "Assassin" => ap ? "ap" : "lethality",
            "Tank" => "tank",
            "Support" => champ.HasTag("Tank") ? "tank" : ap && champ.HasTag("Mage") ? "ap" : "healer",
            _ => ap ? "battlemage" : "bruiser",
        };
    }

    public static SurprisePlan? Plan(ChampionInfo champ, ItemCatalog items, int seed)
    {
        var rng = new Random(seed);
        string usual = UsualTheme(champ);
        var themes = Themes.Where(t => t.Key != usual && t.Suits(champ)).OrderBy(_ => rng.Next()).ToList();
        foreach (var theme in themes)
        {
            var path = PickItems(theme, items, rng);
            if (path.Count < 3) continue; // not enough items of this style in the shop right now
            return new SurprisePlan(champ.Id, champ.Name, theme, new ValueList<int>(path.Select(i => i.Id).ToList()), PickBoots(theme, items, rng), seed);
        }
        return null;
    }

    private static List<ItemInfo> PickItems(SurpriseTheme theme, ItemCatalog items, Random rng)
    {
        // Rank by the style's stats with a random factor, so each roll differs but stays on-theme.
        var ranked = items.ShopCompleted
            .Where(i => !i.IsBoots && !i.IsSupportItem && theme.Required.All(s => i.Get(s) > 0))
            .Select(i => (Item: i, Score: theme.Weights.Sum(w => w.Value * i.Get(w.Key) / ItemRecommender.Norm[w.Key]) * (0.55 + rng.NextDouble() * 0.9)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item);
        var path = new List<ItemInfo>();
        foreach (var item in ranked)
        {
            if (path.Any(p => p.UniqueGroups.Overlaps(item.UniqueGroups))) continue; // can't own both
            path.Add(item);
            if (path.Count == PathLength) break;
        }
        return path;
    }

    private static int PickBoots(SurpriseTheme theme, ItemCatalog items, Random rng)
    {
        var tierTwo = items.ShopCompleted.Where(i => i.IsUpgradedBoots && i.Depth == 2).ToList();
        var fitting = tierTwo.Where(b => theme.Boots.Contains(b.Name)).ToList();
        var pool = fitting.Count > 0 ? fitting : tierTwo;
        return pool.Count == 0 ? 0 : pool[rng.Next(pool.Count)].Id;
    }
}
