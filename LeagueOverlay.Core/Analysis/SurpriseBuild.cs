using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;

namespace LeagueOverlay.Core.Analysis;

/// <summary>
/// A style of build for surprise mode: which stats its items must have, how to rank them, and how well a champion fits
/// it (<see cref="Fit"/>: 0 = not viable, higher = the kit scales better with it, so it's picked more often).
/// </summary>
public sealed record SurpriseTheme(
    string Key, string Label, string Pitch,
    Stat[] Required, IReadOnlyDictionary<Stat, double> Weights, string[] Boots,
    Func<ChampionInfo, ChampionKit?, double> Fit)
{
    public bool Suits(ChampionInfo champ, ChampionKit? kit = null) => Fit(champ, kit) > 0;

    /// <summary>Runes that work with this style, by name (names survive patches; ids are looked up in the rune data).</summary>
    public RuneRecipe Runes { get; init; } = RuneRecipe.None;
}

/// <summary>
/// Runes for a surprise style: one of the keystones (its tree becomes the primary tree), runes preferred in every row,
/// the secondary trees to pick from, and the stat shards (offense, flex, defense).
/// </summary>
public sealed record RuneRecipe(string[] Keystones, string[] Secondary, string[] Prefer, int[] Shards)
{
    public static readonly RuneRecipe None = new([], [], [], [5008, 5008, 5011]);
}

/// <summary>An unexpected build for one champion: the style, five items in order, boots and a rune page to match. Same seed, same plan.</summary>
public sealed record SurprisePlan(string ChampionKey, string ChampionName, SurpriseTheme Theme, ValueList<int> Path, int Boots, int Seed)
{
    public string Title => $"{Theme.Label} {ChampionName}";
    /// <summary>Runes that fit the build; null when rune data isn't loaded.</summary>
    public RuneSetup? Runes { get; init; }
}

/// <summary>
/// Surprise build mode, in two steps:
/// <list type="number">
/// <item>The style: one the champion isn't usually built with but its abilities scale with (AP Ezreal: all four of his
///   abilities have AP ratios; never AP Jinx: only her E does). Styles are drawn at random, weighted by how well the kit
///   fits them (<see cref="ChampionKit"/> from Meraki's ability data; without it, stricter Data Dragon ratings).</item>
/// <item>The items: a semi-random path of items with that style's stats, boots, and runes to match.</item>
/// </list>
/// Champion + style combinations you blocked ("don't suggest again") are skipped.
/// </summary>
public static class SurpriseBuilder
{
    public const int PathLength = 5;

    /// <summary>The key a blocked champion + style is saved under: "Jinx:ap".</summary>
    public static string BlockKey(string championId, string themeKey) => $"{championId}:{themeKey}".ToLowerInvariant();

    // Fit scores. With kit data: counts of Q/W/E/R scaling with AP, AD or your defensive stats. Without: Data Dragon's 1-10 ratings.
    private static double ApFit(ChampionInfo c, ChampionKit? k) => k is null ? (c.Magic >= 6 ? 2 : 0) : k.ApAbilities >= 2 ? k.ApAbilities : 0;
    private static double AdFit(ChampionInfo c, ChampionKit? k) => k is null ? (c.Attack >= 6 ? 2 : 0) : k.AdAbilities >= 2 ? k.AdAbilities : 0;
    private static bool Attacker(ChampionInfo c, ChampionKit? k) => k is null ? c.HasTag("Marksman") || c.Attack >= 7 : k.AttackBased || c.HasTag("Marksman");

    /// <summary>
    /// Champions with heals or shields on allies in their kit: the only ones a healing build is fun on (healing power does
    /// nothing for Ekko). Elsewhere healing items can still show up one at a time, but never as the theme.
    /// </summary>
    public static readonly HashSet<string> Healers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Senna", "Soraka", "Sona", "Nami", "Yuumi", "Seraphine", "Lulu", "Janna", "Karma", "Milio", "Renata", "Taric", "Bard", "Ivern",
        "Kayle", "Lux", "Orianna", "Morgana", "Rakan", "Nidalee", "Shen",
    };

    public static readonly IReadOnlyList<SurpriseTheme> Themes =
    [
        new("ap", "AP", "Every item adds ability power, so the abilities hit far harder than anyone expects.",
            [Stat.AbilityPower], new Dictionary<Stat, double> { [Stat.AbilityPower] = 1, [Stat.AbilityHaste] = 0.5, [Stat.MagicPenPercent] = 0.6, [Stat.MagicPenFlat] = 0.6 },
            ["Sorcerer's Shoes"], ApFit)
        {
            Runes = new(["Arcane Comet", "Electrocute", "Summon Aery", "Deathfire Touch"], ["Inspiration", "Domination", "Sorcery"],
                ["Manaflow Band", "Transcendence", "Scorch", "Gathering Storm", "Cheap Shot", "Sudden Impact", "Ultimate Hunter",
                 "Magical Footwear", "Biscuit Delivery", "Cosmic Insight", "Absolute Focus", "Axiom Arcanist"], [5008, 5008, 5001]),
        },
        new("crit", "Crit", "Big crits from a champion nobody expects them from.",
            [Stat.CritChance], new Dictionary<Stat, double> { [Stat.AttackDamage] = 1, [Stat.CritChance] = 1, [Stat.AttackSpeed] = 0.6 },
            ["Berserker's Greaves"], (c, k) => Attacker(c, k) && (k is null || k.AdAbilities >= 1) ? 1 + 0.5 * (k?.AdAbilities ?? 1) : 0)
        {
            Runes = new(["Lethal Tempo", "Fleet Footwork", "Press the Attack"], ["Domination", "Sorcery", "Inspiration"],
                ["Presence of Mind", "Legend: Alacrity", "Legend: Bloodline", "Cut Down", "Coup de Grace", "Taste of Blood",
                 "Treasure Hunter", "Absolute Focus", "Gathering Storm", "Magical Footwear"], [5005, 5008, 5001]),
        },
        new("onhit", "On-hit", "Attack speed and on-hit effects: lots of very fast autos.",
            [Stat.AttackSpeed], new Dictionary<Stat, double> { [Stat.AttackSpeed] = 1, [Stat.AttackDamage] = 0.4, [Stat.AbilityPower] = 0.3 },
            ["Berserker's Greaves"], (c, k) => Attacker(c, k) && !c.Id.Equals("Yuumi", StringComparison.OrdinalIgnoreCase) ? 1.5 : 0)
        {
            Runes = new(["Lethal Tempo", "Hail of Blades", "Press the Attack"], ["Domination", "Resolve", "Sorcery"],
                ["Legend: Alacrity", "Presence of Mind", "Last Stand", "Cut Down", "Sudden Impact", "Treasure Hunter", "Absorb Life",
                 "Nimbus Cloak", "Celerity", "Overgrowth"], [5005, 5008, 5001]),
        },
        new("lethality", "Lethality", "Armor-ignoring burst: pick a target and delete them.",
            [Stat.Lethality], new Dictionary<Stat, double> { [Stat.Lethality] = 1, [Stat.AttackDamage] = 1, [Stat.AbilityHaste] = 0.4 },
            ["Boots of Swiftness", "Ionian Boots of Lucidity"], AdFit)
        {
            Runes = new(["Electrocute", "Dark Harvest", "First Strike", "Hail of Blades"], ["Precision", "Sorcery", "Inspiration"],
                ["Sudden Impact", "Cheap Shot", "Grisly Mementos", "Treasure Hunter", "Ultimate Hunter", "Relentless Hunter",
                 "Magical Footwear", "Cash Back", "Biscuit Delivery", "Coup de Grace", "Absolute Focus", "Gathering Storm", "Celerity"], [5008, 5008, 5011]),
        },
        new("tank", "Tank", "An unkillable frontline: health, armor and magic resist.",
            [Stat.Health], new Dictionary<Stat, double> { [Stat.Health] = 1, [Stat.Armor] = 0.8, [Stat.MagicResist] = 0.8, [Stat.AbilityHaste] = 0.3 },
            ["Plated Steelcaps", "Mercury's Treads"],
            // Anyone can tank, but a ranged carry with nothing scaling off it is a less fun tank: half as likely.
            (c, k) => k is { Ranged: true, DefenseAbilities: 0 } ? 0.5 : 1 + (k?.DefenseAbilities ?? (c.Defense >= 6 ? 1 : 0)))
        {
            Runes = new(["Grasp of the Undying", "Aftershock", "Guardian", "Glacial Augment"], ["Precision", "Inspiration", "Sorcery", "Domination"],
                ["Demolish", "Font of Life", "Shield Bash", "Conditioning", "Second Wind", "Bone Plating", "Overgrowth", "Unflinching",
                 "Revitalize", "Legend: Haste", "Biscuit Delivery", "Approach Velocity"], [5008, 5001, 5011]),
        },
        new("bruiser", "Bruiser", "Damage and health: stay in fights long enough to win them.",
            [Stat.AttackDamage, Stat.Health], new Dictionary<Stat, double> { [Stat.AttackDamage] = 0.8, [Stat.Health] = 0.9, [Stat.AbilityHaste] = 0.5, [Stat.Omnivamp] = 0.4 },
            ["Plated Steelcaps", "Mercury's Treads", "Ionian Boots of Lucidity"],
            (c, k) => k is null ? (c.Attack >= 5 ? 1.5 : 0) : k.AdAbilities >= 2 || (k.AdAbilities >= 1 && k.DefenseAbilities >= 1) ? 0.7 * k.AdAbilities + k.DefenseAbilities : 0)
        {
            Runes = new(["Conqueror", "Grasp of the Undying", "Fleet Footwork"], ["Resolve", "Domination", "Inspiration"],
                ["Triumph", "Legend: Haste", "Last Stand", "Legend: Alacrity", "Second Wind", "Bone Plating", "Unflinching", "Overgrowth",
                 "Taste of Blood", "Biscuit Delivery", "Demolish", "Conditioning"], [5008, 5001, 5011]),
        },
        new("healer", "Healing", "Heal and shield your team through every fight.",
            [Stat.HealShieldPower], new Dictionary<Stat, double> { [Stat.HealShieldPower] = 1, [Stat.AbilityHaste] = 0.6, [Stat.ManaRegen] = 0.5, [Stat.AbilityPower] = 0.3 },
            ["Ionian Boots of Lucidity", "Boots of Swiftness"], (c, k) => Healers.Contains(c.Id) || (k?.HealsAllies == true && (c.HasTag("Support") || c.HasTag("Mage"))) ? 2 : 0)
        {
            Runes = new(["Summon Aery", "Guardian", "Unsealed Spellbook"], ["Resolve", "Inspiration", "Sorcery"],
                ["Manaflow Band", "Transcendence", "Revitalize", "Font of Life", "Bone Plating", "Biscuit Delivery", "Cosmic Insight",
                 "Shield Bash", "Absolute Focus", "Waterwalking", "Nimbus Cloak"], [5007, 5008, 5011]),
        },
        new("battlemage", "Battlemage", "Ability power plus health: brawl with your spells.",
            [Stat.AbilityPower, Stat.Health], new Dictionary<Stat, double> { [Stat.AbilityPower] = 0.8, [Stat.Health] = 0.8, [Stat.AbilityHaste] = 0.4 },
            ["Sorcerer's Shoes", "Mercury's Treads"],
            (c, k) => k is null ? (c.Magic >= 6 ? 1.5 : 0) : k.ApAbilities >= 2 ? 0.7 * k.ApAbilities + k.DefenseAbilities + (k.Ranged ? 0 : 0.5) : 0)
        {
            Runes = new(["Conqueror", "Arcane Comet", "Aftershock", "Deathfire Touch"], ["Resolve", "Sorcery", "Domination", "Inspiration"],
                ["Triumph", "Legend: Haste", "Last Stand", "Manaflow Band", "Transcendence", "Scorch", "Gathering Storm", "Second Wind",
                 "Bone Plating", "Unflinching", "Revitalize", "Overgrowth", "Cosmic Insight"], [5008, 5001, 5011]),
        },
    ];

    /// <summary>How the champion is normally built, as a theme key (so surprise mode never picks it).</summary>
    public static string UsualTheme(ChampionInfo champ, ChampionKit? kit = null)
    {
        bool ap = champ.Magic > champ.Attack;
        return champ.PrimaryTag switch
        {
            "Marksman" => ap ? "ap" : "crit",
            "Mage" => "ap",
            "Assassin" => ap ? "ap" : "lethality",
            "Tank" => "tank",
            // Supports without heals or shields for allies aren't built as healers (Pyke goes lethality).
            "Support" when kit is { HealsAllies: false } && !Healers.Contains(champ.Id) =>
                champ.HasTag("Tank") ? "tank" : kit.ApAbilities >= 2 ? "ap" : "lethality",
            "Support" => champ.HasTag("Tank") ? "tank" : ap && champ.HasTag("Mage") ? "ap" : "healer",
            _ => ap ? "battlemage" : "bruiser",
        };
    }

    /// <param name="kit">How the champion's abilities scale; null falls back to Data Dragon's ratings.</param>
    /// <param name="blocked">Champion + style combinations not to suggest (<see cref="BlockKey"/>).</param>
    public static SurprisePlan? Plan(ChampionInfo champ, ItemCatalog items, int seed, RuneCatalog? runes = null,
        ChampionKit? kit = null, IReadOnlySet<string>? blocked = null)
    {
        var rng = new Random(seed);
        string usual = UsualTheme(champ, kit);
        // Weighted random order (each style's key is u^(1/fit)): better fits come first more often, but any viable style can.
        var themes = Themes
            .Where(t => t.Key != usual && blocked?.Contains(BlockKey(champ.Id, t.Key)) != true)
            .Select(t => (Theme: t, Fit: t.Fit(champ, kit)))
            .Where(x => x.Fit > 0)
            .Select(x => (x.Theme, Order: Math.Pow(rng.NextDouble(), 1 / x.Fit)))
            .OrderByDescending(x => x.Order)
            .Select(x => x.Theme)
            .ToList();
        foreach (var theme in themes)
        {
            var path = PickItems(theme, items, rng);
            if (path.Count < 3) continue; // not enough items of this style in the shop right now
            return new SurprisePlan(champ.Id, champ.Name, theme, new ValueList<int>(path.Select(i => i.Id).ToList()), PickBoots(theme, items, rng), seed)
            {
                Runes = runes is { IsLoaded: true } ? PickRunes(theme.Runes, runes, rng) : null,
            };
        }
        return null;
    }

    /// <summary>
    /// A valid page for the recipe: a keystone from the list (its tree is primary), the preferred rune in each primary row
    /// (random among preferred ones, or any if none fit), then two runes from different rows of a secondary tree.
    /// </summary>
    public static RuneSetup? PickRunes(RuneRecipe recipe, RuneCatalog runes, Random rng)
    {
        var keystones = recipe.Keystones.Select(runes.ByName).OfType<RuneInfo>().Where(r => r.Row == 0 && r.Style != RuneCatalog.ShardStyle).ToList();
        if (keystones.Count == 0)
            keystones = runes.Styles.Where(s => s.Rows.Count > 0).SelectMany(s => s.Rows[0]).Select(runes.Get).OfType<RuneInfo>().ToList();
        if (keystones.Count == 0) return null;
        var keystone = keystones[rng.Next(keystones.Count)];
        if (runes.Style(keystone.Style) is not { Rows.Count: >= 4 } primary) return null;
        var prefer = recipe.Prefer.Select(runes.ByName).OfType<RuneInfo>().Select(r => r.Id).ToHashSet();

        int Pick(IReadOnlyList<int> row)
        {
            var liked = row.Where(prefer.Contains).ToList();
            var pool = liked.Count > 0 ? liked : row;
            return pool[rng.Next(pool.Count)];
        }

        var perks = new List<int> { keystone.Id };
        for (int row = 1; row < 4; row++) perks.Add(Pick(primary.Rows[row]));

        var secondaries = recipe.Secondary.Select(runes.StyleByName).OfType<RuneStyle>().Where(s => s.Id != primary.Id && s.Rows.Count >= 4).ToList();
        if (secondaries.Count == 0) secondaries = runes.Styles.Where(s => s.Id != primary.Id && s.Rows.Count >= 4).ToList();
        if (secondaries.Count == 0) return null;
        var secondary = secondaries[rng.Next(secondaries.Count)];
        // Two different non-keystone rows: rows with a preferred rune first.
        var rows = Enumerable.Range(1, secondary.Rows.Count - 1)
            .OrderByDescending(r => secondary.Rows[r].Any(prefer.Contains)).ThenBy(_ => rng.Next()).Take(2).OrderBy(r => r).ToList();
        foreach (int r in rows) perks.Add(Pick(secondary.Rows[r]));

        for (int i = 0; i < 3; i++)
        {
            var row = RuneCatalog.ShardRows[i];
            perks.Add(i < recipe.Shards.Length && row.Contains(recipe.Shards[i]) ? recipe.Shards[i] : row[0]);
        }
        return new RuneSetup(primary.Id, secondary.Id, new ValueList<int>(perks));
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
