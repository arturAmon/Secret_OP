using System.Text.Json;
using System.Text.RegularExpressions;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Core.Data;

public enum Stat
{
    AttackDamage, AbilityPower, Health, Armor, MagicResist, AttackSpeed, CritChance, Lethality,
    ArmorPenPercent, MagicPenFlat, MagicPenPercent, AbilityHaste, LifeSteal, Omnivamp,
    HealShieldPower, Mana, ManaRegen, MoveSpeed, Tenacity, HealthRegen,
}

public sealed class ItemInfo
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Full price of the item.</summary>
    public required int TotalCost { get; init; }
    /// <summary>Combine cost on top of the components (ddragon gold.base).</summary>
    public required int BaseCost { get; init; }
    public required IReadOnlySet<string> Tags { get; init; }
    public required IReadOnlyList<int> From { get; init; }
    public required IReadOnlyList<int> Into { get; init; }
    public required int Depth { get; init; }
    public required IReadOnlyDictionary<Stat, double> Stats { get; init; }
    /// <summary>Named unique passives, e.g. "Lifeline". Two completed items sharing one can't be combined.</summary>
    public required IReadOnlySet<string> Passives { get; init; }
    /// <summary>Description with markup removed.</summary>
    public required string Text { get; init; }
    public required bool InShop { get; init; }
    /// <summary>gold.purchasable: false for quest upgrades (Runic Compass) and items you can't buy directly.</summary>
    public bool Purchasable { get; init; } = true;

    public bool IsBoots => Tags.Contains("Boots");
    /// <summary>Tier-2 boots (Steelcaps, Mercs, Berserker's, ...).</summary>
    public bool IsUpgradedBoots => IsBoots && Depth >= 2;
    public bool IsCompleted => IsBoots ? IsUpgradedBoots : Into.Count == 0 && TotalCost >= 2000;
    public bool IsConsumableOrTrinket => Tags.Contains("Consumable") || Tags.Contains("Trinket");
    /// <summary>Support quest item (World Atlas line): gold income + vision.</summary>
    public bool IsSupportItem => Tags.Contains("GoldPer") && Tags.Contains("Vision");
    public bool AppliesWounds => Text.Contains("Wounds", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Groups this item can't share with another completed item: its named passives, plus one shared
    /// anti-heal group (anti-heal doesn't stack, and not every anti-heal item names its passive the same).
    /// </summary>
    public IReadOnlySet<string> UniqueGroups => _uniqueGroups ??= AppliesWounds ? Passives.Append("(anti-heal)").ToHashSet() : Passives;
    private IReadOnlySet<string>? _uniqueGroups;

    public double Get(Stat s) => Stats.TryGetValue(s, out var v) ? v : 0;
}

public sealed class ItemCatalog
{
    public static readonly ItemCatalog Empty = new(new Dictionary<int, ItemInfo>());

    /// <summary>Every item in item.json, used for value lookups and recipe trees.</summary>
    public IReadOnlyDictionary<int, ItemInfo> ById { get; }
    /// <summary>Completed items you can buy on Summoner's Rift, one per name.</summary>
    public IReadOnlyList<ItemInfo> ShopCompleted { get; }

    // Whatever boots upgrade into. Gunmetal Greaves (3172, from Berserker's Greaves, the bot lane role quest upgrade that sits
    // in the extra boots slot) is tagged "NonbootsMovement" instead of "Boots", so the tag alone misses it.
    private readonly HashSet<int> _bootUpgradeIds;
    private static readonly Regex BootsName = new(@"\b(Boots|Greaves|Shoes|Treads|Steelcaps|Soles|Crushers|Swiftmarch|Lucidity)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ItemCatalog(IReadOnlyDictionary<int, ItemInfo> byId)
    {
        ById = byId;
        _bootUpgradeIds = byId.Values.Where(i => i.IsBoots).SelectMany(i => i.Into).ToHashSet();
        ShopCompleted = byId.Values
            .Where(i => i.InShop && i.IsCompleted)
            .GroupBy(i => i.Name)
            .Select(g => g.OrderBy(i => i.Id).First())
            .OrderBy(i => i.Id)
            .ToList();
    }

    public ItemInfo? Get(int id) => ById.TryGetValue(id, out var i) ? i : null;

    /// <summary>
    /// True for tier-2+ boots in an inventory, including upgrades that aren't tagged as boots or that
    /// Data Dragon doesn't list yet (found through the boots' "into" lists, or by name as a last resort).
    /// </summary>
    public bool IsUpgradedBoots(PlayerItem item)
    {
        if (_bootUpgradeIds.Contains(item.ItemID)) return true;
        if (Get(item.ItemID) is { } info) return info.IsUpgradedBoots;
        return BootsName.IsMatch(item.DisplayName ?? "");
    }

    /// <summary>A finished item (legendary or upgraded boots), for the purchase timeline.</summary>
    public bool IsCompleted(PlayerItem item)
    {
        if (IsUpgradedBoots(item)) return true;
        if (Get(item.ItemID) is { } info) return info.IsCompleted && !info.IsConsumableOrTrinket;
        return item.Price >= 2000; // unknown to Data Dragon: judge by the client's price
    }

    /// <summary>Gold spent on one inventory entry. Falls back to the client's price if Data Dragon doesn't know the item.</summary>
    public int ValueOf(PlayerItem item) => (Get(item.ItemID)?.TotalCost ?? item.Price) * Math.Max(1, item.Count);

    public int ItemGold(Player p) => p.Items.Sum(ValueOf);

    public double StatTotal(Player p, Stat s) => p.Items.Sum(i => (Get(i.ItemID)?.Get(s) ?? 0) * Math.Max(1, i.Count));

    /// <summary>Gold still needed to finish <paramref name="target"/>, counting components already owned.</summary>
    public int RemainingCost(ItemInfo target, IEnumerable<int> ownedItemIds) => Remaining(target, ownedItemIds.ToList());

    private int Remaining(ItemInfo item, List<int> pool)
    {
        if (pool.Remove(item.Id)) return 0;
        int cost = item.BaseCost;
        foreach (var childId in item.From)
            cost += Get(childId) is { } child ? Remaining(child, pool) : 0;
        return cost;
    }

    /// <summary>All components anywhere in the item's recipe tree (not including the item itself).</summary>
    public IEnumerable<ItemInfo> Components(ItemInfo item)
    {
        foreach (var childId in item.From)
        {
            if (Get(childId) is not { } child) continue;
            yield return child;
            foreach (var grandChild in Components(child)) yield return grandChild;
        }
    }

    // ---- parsing ----

    private static readonly Regex StatsBlock = new(@"<stats>(.*?)</stats>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex StatLine = new(@"^([\d.]+)(%?)\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex PassiveName = new(@"<passive>([^<]+)</passive>", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex LineBreak = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const int MaxShopItemId = 10000;

    public static ItemCatalog Parse(string itemJson, int map = 11)
    {
        using var doc = JsonDocument.Parse(itemJson);
        var byId = new Dictionary<int, ItemInfo>();
        foreach (var prop in doc.RootElement.GetProperty("data").EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out var id)) continue;
            var e = prop.Value;
            var gold = e.GetProperty("gold");
            var description = e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var tags = e.TryGetProperty("tags", out var t) ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet() : new HashSet<string>();

            bool onMap = e.TryGetProperty("maps", out var maps) && maps.TryGetProperty(map.ToString(), out var m) && m.GetBoolean();
            bool inStore = !e.TryGetProperty("inStore", out var s) || s.GetBoolean();
            bool locked = e.TryGetProperty("requiredChampion", out _) || e.TryGetProperty("requiredAlly", out _);
            bool purchasable = gold.GetProperty("purchasable").GetBoolean();
            int total = gold.GetProperty("total").GetInt32();

            byId[id] = new ItemInfo
            {
                Id = id,
                Name = e.GetProperty("name").GetString() ?? id.ToString(),
                TotalCost = total,
                BaseCost = gold.GetProperty("base").GetInt32(),
                Tags = tags,
                From = IntList(e, "from"),
                Into = IntList(e, "into"),
                Depth = e.TryGetProperty("depth", out var dep) ? dep.GetInt32() : 1,
                Stats = ParseStats(description),
                Passives = PassiveName.Matches(description).Select(x => x.Groups[1].Value.Trim().TrimEnd(':').Trim()).ToHashSet(),
                Text = Tag.Replace(LineBreak.Replace(description, " "), ""),
                // Real shop items have 4-digit ids. 6-digit ids (e.g. 663193) are copies for special modes that
                // can also report map 11, even when the real item (3193) is no longer sold.
                Purchasable = purchasable,
                InShop = id < MaxShopItemId && onMap && inStore && purchasable && !locked && total > 0 &&
                         !tags.Contains("Consumable") && !tags.Contains("Trinket"),
            };
        }
        return new ItemCatalog(byId);
    }

    private static IReadOnlyList<int> IntList(JsonElement e, string name) =>
        e.TryGetProperty(name, out var arr)
            ? arr.EnumerateArray().Select(x => int.TryParse(x.GetString(), out var v) ? v : 0).Where(v => v > 0).ToList()
            : Array.Empty<int>();

    /// <summary>Reads "&lt;attention&gt;45&lt;/attention&gt; Attack Damage" lines from the description's stats block.</summary>
    public static Dictionary<Stat, double> ParseStats(string description)
    {
        var result = new Dictionary<Stat, double>();
        var block = StatsBlock.Match(description);
        if (!block.Success) return result;
        foreach (var raw in LineBreak.Split(block.Groups[1].Value))
        {
            var line = Tag.Replace(raw, "").Trim();
            var match = StatLine.Match(line);
            if (!match.Success || !double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var value)) continue;
            bool percent = match.Groups[2].Value == "%";
            Stat? stat = match.Groups[3].Value.Trim() switch
            {
                "Attack Damage" => Stat.AttackDamage,
                "Ability Power" => Stat.AbilityPower,
                "Health" => Stat.Health,
                "Armor" => Stat.Armor,
                "Magic Resist" => Stat.MagicResist,
                "Attack Speed" => Stat.AttackSpeed,
                "Critical Strike Chance" => Stat.CritChance,
                "Lethality" => Stat.Lethality,
                "Armor Penetration" => Stat.ArmorPenPercent,
                "Magic Penetration" => percent ? Stat.MagicPenPercent : Stat.MagicPenFlat,
                "Ability Haste" => Stat.AbilityHaste,
                "Life Steal" => Stat.LifeSteal,
                "Omnivamp" => Stat.Omnivamp,
                "Heal and Shield Power" => Stat.HealShieldPower,
                "Mana" => Stat.Mana,
                "Base Mana Regen" or "Mana Regen per 5 seconds" => Stat.ManaRegen,
                "Move Speed" => Stat.MoveSpeed,
                "Tenacity" => Stat.Tenacity,
                "Base Health Regen" or "Health Regen per 5 seconds" => Stat.HealthRegen,
                _ => null,
            };
            if (stat is { } st) result[st] = result.GetValueOrDefault(st) + value;
        }
        return result;
    }
}
