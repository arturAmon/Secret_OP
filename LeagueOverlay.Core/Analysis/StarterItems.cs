using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Core.Analysis;

/// <summary>
/// Starting items: what high-ranked players start with on the champion and role when collected games have it
/// (<see cref="ChampionBuild.Starter"/>), otherwise the usual start for the role and style:
/// jungle pet, support quest item, Doran's item matching the damage type, plus potions.
/// Lists repeat an item for each copy (two potions = two entries).
/// </summary>
public static class StarterItems
{
    public const int DoransShield = 1054, DoransBlade = 1055, DoransRing = 1056, HealthPotion = 2003, WorldAtlas = 3865;
    public const int ScorchclawPup = 1101, GustwalkerHatchling = 1102, MosstomperSeedling = 1103;

    /// <summary>After this many seconds without buying anything, the overlay warns you.</summary>
    public const double NothingBoughtAlertSeconds = 45;
    /// <summary>The starting items stay on the overlay this long (minions reach lane a little after).</summary>
    public const double ShowUntilSeconds = 90;

    public static ValueList<int> For(string? position, BuildStyle style, bool ap, bool smite)
    {
        string pos = smite ? "JUNGLE" : position?.ToUpperInvariant() ?? "";
        int[] items = pos switch
        {
            "JUNGLE" => [style == BuildStyle.Tank ? MosstomperSeedling : style == BuildStyle.Marksman ? GustwalkerHatchling : ScorchclawPup, HealthPotion],
            "UTILITY" => [WorldAtlas, HealthPotion, HealthPotion],
            "TOP" when style == BuildStyle.Tank => [DoransShield, HealthPotion],
            _ when ap || style is BuildStyle.Mage or BuildStyle.Enchanter => [DoransRing, HealthPotion, HealthPotion],
            _ => [DoransBlade, HealthPotion],
        };
        return new ValueList<int>(items);
    }

    /// <summary>The build style a surprise theme plays like, for its starting items.</summary>
    public static (BuildStyle Style, bool Ap) ForTheme(string themeKey) => themeKey switch
    {
        "ap" or "battlemage" => (BuildStyle.Mage, true),
        "healer" => (BuildStyle.Enchanter, true),
        "tank" => (BuildStyle.Tank, false),
        "bruiser" => (BuildStyle.Fighter, false),
        "lethality" => (BuildStyle.AdAssassin, false),
        _ => (BuildStyle.Marksman, false),
    };

    /// <summary>Popular start from collected games when there is one, else the rule above.</summary>
    public static ValueList<int> Best(ChampionBuild? popular, string? position, BuildStyle style, bool ap, bool smite) =>
        popular?.Starter is { Count: > 0 } s ? new ValueList<int>(s) : For(position, style, ap, smite);

    /// <summary>"Doran's Blade + 2 Health Potion".</summary>
    public static string Describe(IEnumerable<int> items, ItemCatalog catalog) =>
        string.Join(" + ", items.GroupBy(id => id).Select(g => (g.Count() > 1 ? $"{g.Count()} " : "") + (catalog.Get(g.Key)?.Name ?? $"item {g.Key}")));

    /// <summary>True while you still own nothing but the trinket (start of the game).</summary>
    public static bool NothingBought(Player me, ItemCatalog catalog) =>
        me.Items.All(i => i.ItemID == 0 || catalog.Get(i.ItemID)?.Tags.Contains("Trinket") == true || i.ItemID is 3340 or 3363 or 3364);

    /// <summary>
    /// The reminder for the start of the game: the starting items while there's time, a warning when nothing was bought
    /// after <see cref="NothingBoughtAlertSeconds"/>. Null once you've bought something or the start is over.
    /// </summary>
    public static (string Text, int ItemId, bool Urgent)? Reminder(LiveGame game, Player me, ItemCatalog catalog, ValueList<int> starter)
    {
        double t = game.GameData?.GameTime ?? 0;
        if (t > ShowUntilSeconds * 2 || !NothingBought(me, catalog) || starter.Count == 0) return null;
        string what = Describe(starter, catalog);
        if (t >= NothingBoughtAlertSeconds) return ($"You haven't bought anything yet! Start with {what}", starter[0], true);
        return t <= ShowUntilSeconds ? ($"Starting items: {what}", starter[0], false) : null;
    }
}
